// Control Flow
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>
#include <chrono>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpJmp_Rel(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // E9: JMP rel32, EB: JMP rel8
    int32_t offset;
    if (op->length == 2) {
        // 8-bit relative jump
        offset = (int32_t)(int8_t)(op->imm & 0xFF);
    } else {
        // 32-bit relative jump (E9)
        offset = (int32_t)op->imm;
    }
    state->ctx.eip += offset;
}

template <uint8_t Cond>
static FORCE_INLINE void OpJcc_Rel(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    // Condition check is compile-time specialized
    
    if (CheckConditionFixed<Cond>(state)) {
        int32_t offset;
        if (op->length == 2) {
            // 8-bit relative jump (0x7x opcodes)
            offset = (int32_t)(int8_t)(op->imm & 0xFF);
        } else {
            // 32-bit relative jump (0F 8x opcodes)
            offset = (int32_t)op->imm;
        }
        state->ctx.eip += offset;
    }
}

static FORCE_INLINE void OpCall_Rel(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // E8: CALL rel32
    // Push Return Address (Current EIP is already advanced to Next Insn by
    // Wrapper/Step)
    Push32(state, state->ctx.eip, utlb);
    // Jump relative to Next Insn
    state->ctx.eip += (int32_t)op->imm;
}

static FORCE_INLINE void OpRet(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C3: RET
    uint32_t ret_eip = Pop32(state, utlb);
    state->ctx.eip = ret_eip;
}

static FORCE_INLINE void OpRet_Imm16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C2: RET imm16
    uint32_t ret_eip = Pop32(state, utlb);
    state->ctx.eip = ret_eip;

    // Pop imm16 bytes from stack
    uint32_t esp = GetReg(state, ESP);
    esp += (uint16_t)op->imm;
    SetReg(state, ESP, esp);
}

static FORCE_INLINE void OpInt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CD ib: INT imm8
    // Note: Decoder puts imm8 in op->imm
    uint8_t vector = (uint8_t)op->imm;
    if (!state->hooks.on_interrupt(state, vector)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector;
    }
    utlb->invalidate();
}

static FORCE_INLINE void OpInt3(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CC: INT3 (Vector 3, Breakpoint)
    if (!state->hooks.on_interrupt(state, 3)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 3;
    }
    utlb->invalidate();
}

static FORCE_INLINE void OpInto(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CE: INTO (Interrupt 4 if Overflow Flag set)
    if (state->ctx.eflags & OF_MASK) {
        if (!state->hooks.on_interrupt(state, 4)) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 4;  // #OF
        }
    }
    utlb->invalidate();
}

static FORCE_INLINE void OpHlt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // HLT (0xF4)
    state->status = EmuStatus::Stopped;
    utlb->invalidate();
}

static FORCE_INLINE void OpNop(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 90: NOP
    // F3 90: PAUSE (REP NOP)
    if (op->prefixes.flags.rep) {
        CPU_RELAX();
    }
}

template <uint8_t Cond>
static FORCE_INLINE void OpCmov_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 4x: CMOVcc r32, r/m32
    // Condition check is compile-time specialized
    
    bool pass = CheckConditionFixed<Cond>(state);
    if (pass) {
        if (op->prefixes.flags.opsize) {
            uint16_t val = ReadModRM16(state, op, utlb);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
        } else {
            uint32_t val = ReadModRM32(state, op, utlb);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, val);
        }
    }
}

// ------------------------------------------------------------------------------------------------
// Flag Operations
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpPushf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9C: PUSHF/PUSHFD
    if (op->prefixes.flags.opsize) {
        // PUSHF (16-bit)
        Push16(state, (uint16_t)state->ctx.eflags, utlb);
    } else {
        // PUSHFD (32-bit)
        // Note: VM and RF flags are usually cleared in image pushed to stack?
        // For simple emulation, we push raw.
        Push32(state, state->ctx.eflags & 0x00FCFFFF, utlb);
    }
}

static FORCE_INLINE void OpPopf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9D: POPF/POPFD
    if (op->prefixes.flags.opsize) {
        // POPF (16-bit)
        uint16_t val = Pop16(state, utlb);
        // Only update bits allowed by mask (and within 16-bit range)
        uint32_t mask = state->ctx.eflags_mask & 0xFFFF;
        // Also always preserve Reserved Bit 1 (Value 2)
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        state->ctx.eflags = new_flags;
    } else {
        // POPFD (32-bit)
        uint32_t val = Pop32(state, utlb);
        uint32_t mask = state->ctx.eflags_mask;
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        // Reserved bits 3, 5, 15, 22..31 should strictly be preserved or zeroed
        // depending on CPU model. But respecting mask is sufficient for user mode
        // emulation.
        state->ctx.eflags = new_flags;
    }
}

static FORCE_INLINE void OpStc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F9: STC
    state->ctx.eflags |= CF_MASK;
}

static FORCE_INLINE void OpClc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F8: CLC
    state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpCmc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F5: CMC (Complement Carry)
    state->ctx.eflags ^= CF_MASK;
}

static FORCE_INLINE void OpStd(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FD: STD (Set Direction Flag)
    state->ctx.eflags |= 0x400;  // DF Mask
}

static FORCE_INLINE void OpCld(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FC: CLD (Clear Direction Flag)
    state->ctx.eflags &= ~0x400;
}

static FORCE_INLINE void OpSti(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FB: STI (Set Interrupt Flag)
    // Privileged Instruction. In User Mode (CPL=3, IOPL=0), this faults.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
}

static FORCE_INLINE void OpCli(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FA: CLI (Clear Interrupt Flag)
    // Privileged Instruction.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
}

static FORCE_INLINE void OpCpuid(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F A2: CPUID
    uint32_t leaf = GetReg(state, EAX);
    // uint32_t ecx_in = GetReg(state, ECX);

    uint32_t eax = 0, ebx = 0, ecx = 0, edx = 0;

    if (leaf == 0) {
        eax = 1;  // Max Leaf
        // "Genu" "ineI" "ntel"
        ebx = 0x756E6547;
        edx = 0x49656E69;
        ecx = 0x6C65746E;
    } else if (leaf == 1) {
        eax = 0x00000680;  // Pentium III approx
        ebx = 0;
        ecx = 0;
        edx = 0x00008000;  // Minimal features
        // Add SSE/SSE2 flags if needed:
        // EDX: Bit 25 (SSE), Bit 26 (SSE2)
        edx |= (1 << 25) | (1 << 26);
        // Bit 0 (FPU)
        edx |= 1;
        // Bit 4 (TSC)
        edx |= (1 << 4);
    }

    SetReg(state, EAX, eax);
    SetReg(state, EBX, ebx);
    SetReg(state, ECX, ecx);
    SetReg(state, EDX, edx);
}

static FORCE_INLINE void OpRdtsc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 31: RDTSC
    uint64_t tsc = 0;
    if (state->tsc_mode == 1) {
        // Real-time mode
        auto now = std::chrono::steady_clock::now();
        auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(now - state->tsc_start_time).count();
        // tsc = offset + (elapsed_ns * frequency) / 1,000,000,000
        // We use __int128 to prevent overflow during intermediate calculation if frequency is high
        unsigned __int128 val = (unsigned __int128)elapsed * state->tsc_frequency;
        tsc = state->tsc_offset + (uint64_t)(val / 1000000000ULL);
    } else {
        // Fixed increment mode
        tsc = state->tsc_offset + state->tsc_fixed_counter;
        state->tsc_fixed_counter += 1000; // Mock increment per call
    }

    uint32_t low = (uint32_t)tsc;
    uint32_t high = (uint32_t)(tsc >> 32);

    SetReg(state, EAX, low);
    SetReg(state, EDX, high);
}

static FORCE_INLINE void OpWait(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9B: WAIT/FWAIT n
    // Check pending FPU exceptions?
    // For now NOP.
}

static FORCE_INLINE void OpGroup_0FAE(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AE /r
    uint8_t sub = (op->modrm >> 3) & 7;
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    switch (sub) {
        case 2:  // LDMXCSR m32
            if (mod != 3) {
                uint32_t addr = ComputeLinearAddress(state, op);
                state->ctx.mxcsr = state->mmu.read<uint32_t>(addr, utlb);
            }
            break;
        case 3:  // STMXCSR m32
            if (mod != 3) {
                uint32_t addr = ComputeLinearAddress(state, op);
                state->mmu.write<uint32_t>(addr, state->ctx.mxcsr, utlb);
            }
            break;
        case 5:  // LFENCE (mod=3, rm=0)
            if (mod == 3 && rm == 0) {
                // Acquire fence
                std::atomic_thread_fence(std::memory_order_acquire);
            }
            break;
        case 6:  // MFENCE (mod=3, rm=0)
            if (mod == 3 && rm == 0) {
                // Seq_cst fence
                std::atomic_thread_fence(std::memory_order_seq_cst);
            }
            break;
        case 7:
            if (mod == 3 && rm == 0) {  // SFENCE
                // Release fence
                std::atomic_thread_fence(std::memory_order_release);
            } else if (mod != 3) {  // CLFLUSH
                                    // NOP
            }
            break;
        default:
            break;
    }
}

// ------------------------------------------------------------------------------------------------
// Fused Operations
// ------------------------------------------------------------------------------------------------

template <SpecializedOp Type>
ATTR_PRESERVE_NONE int64_t OpFused_CmpJcc(EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB utlb) {
    // We must advance EIP manually as we don't use DispatchWrapper for fused ops
    state->ctx.eip += op->length;

    // 1. Perform CMP
    if constexpr (Type == OP_FUSED_CMP_RR_JCC) {
        uint8_t dst_reg = op->modrm & 7;
        uint8_t src_reg = (op->modrm >> 3) & 7;
        AluSub<uint32_t, true>(state, GetReg(state, dst_reg), GetReg(state, src_reg));
    } else if constexpr (Type == OP_FUSED_CMP_RI_JCC) {
        // Safety: Currently RI fusion ONLY supports mod=3 (register destination)
        // because we use 'disp' for Jcc target.
        uint8_t mod = (op->modrm >> 6) & 3;
        if (mod == 3) {
            uint8_t dst_reg = op->modrm & 7;
            AluSub<uint32_t, true>(state, GetReg(state, dst_reg), op->imm);
        } else {
            // Fallback (Should not happen if decoder is correct)
            state->status = EmuStatus::Fault;
            state->fault_vector = 6;
            return 0;
        }
    } else if constexpr (Type == OP_FUSED_CMP_RM_JCC) {
        // CMP Gv, Ev (0x8B variant) -> CMP reg, [mem]
        uint8_t reg = (op->modrm >> 3) & 7;
        uint32_t v1 = GetReg(state, reg);
        uint32_t v2 = ReadModRM32(state, op, &utlb);
        AluSub<uint32_t, true>(state, v1, v2);
    } else if constexpr (Type == OP_FUSED_CMP_MR_JCC) {
        // CMP Ev, Gv (0x89 variant) -> CMP [mem], reg
        uint8_t reg = (op->modrm >> 3) & 7;
        uint32_t v1 = ReadModRM32(state, op, &utlb);
        uint32_t v2 = GetReg(state, reg);
        AluSub<uint32_t, true>(state, v1, v2);
    } else if constexpr (Type == OP_FUSED_CMP_RI8_JCC) {
        // Group 1 (0x83 /7) CMP r/m32, imm8
        uint32_t v1 = ReadModRM32(state, op, &utlb);
        uint32_t v2 = (uint32_t)(int32_t)(int8_t)(op->imm & 0xFF);
        AluSub<uint32_t, true>(state, v1, v2);
    } else if constexpr (Type == OP_FUSED_CMP_AL_I8_JCC) {
        // 0x3C: CMP AL, imm8
        uint8_t v1 = GetReg8(state, EAX);
        uint8_t v2 = (uint8_t)op->imm;
        AluSub<uint8_t, true>(state, v1, v2);
    } else if constexpr (Type == OP_FUSED_CMP_I8I8_JCC) {
        // 0x80 /7: CMP r/m8, imm8
        uint8_t v1 = ReadModRM8(state, op, &utlb);
        uint8_t v2 = (uint8_t)op->imm;
        AluSub<uint8_t, true>(state, v1, v2);
    }

    // 2. Perform Jcc (Condition in extra)
    uint8_t cond = op->extra;
    if (CheckCondition(state, cond)) {
        // Jump: target relative offset is in 'disp' or 'imm'
        int32_t target_offset;
        if constexpr (Type == OP_FUSED_CMP_RM_JCC || Type == OP_FUSED_CMP_MR_JCC || Type == OP_FUSED_CMP_RR_JCC) {
            target_offset = (int32_t)op->imm;
        } else {
            // RI, RI8, AL_I8, I8I8 use imm for CMP immediate, so Jcc target is in disp
            target_offset = (int32_t)op->disp;
        }
        state->ctx.eip += target_offset;
        
        // IMPORTANT: When jumping, we MUST return 0 to break the current 
        // threaded dispatch chain and return to X86_Run for a new block lookup.
        return instr_limit;
    } else {
        // No jump: EIP already advanced. Threaded Dispatch to NEXT instruction
        DecodedOp* next = op + 1;
        if (instr_limit > 0) {
            HandlerFunc h = (HandlerFunc)((intptr_t)g_HandlerBase + next->handler_offset);
            ATTR_MUSTTAIL return h(state, next, instr_limit - 1, utlb);
        }
    }
    return instr_limit;
}

template <SpecializedOp Type>
ATTR_PRESERVE_NONE int64_t OpFused_TestJcc(EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB utlb) {
    state->ctx.eip += op->length;

    // 1. Perform TEST
    if constexpr (Type == OP_FUSED_TEST_RR_JCC) {
        uint8_t dst_reg = op->modrm & 7;
        uint8_t src_reg = (op->modrm >> 3) & 7;
        AluAnd<uint32_t, true>(state, GetReg(state, dst_reg), GetReg(state, src_reg));
    } else if constexpr (Type == OP_FUSED_TEST_RM_JCC) {
        uint8_t reg = (op->modrm >> 3) & 7;
        uint32_t v1 = ReadModRM32(state, op, &utlb);
        uint32_t v2 = GetReg(state, reg);
        AluAnd<uint32_t, true>(state, v1, v2);
    } else if constexpr (Type == OP_FUSED_TEST_I8I8_RR_JCC) {
        uint8_t dst_reg = op->modrm & 7;
        uint8_t src_reg = (op->modrm >> 3) & 7;
        AluAnd<uint8_t, true>(state, GetReg8(state, dst_reg), GetReg8(state, src_reg));
    } else if constexpr (Type == OP_FUSED_TEST_I8I8_RM_JCC) {
        uint8_t reg = (op->modrm >> 3) & 7;
        uint8_t v1 = ReadModRM8(state, op, &utlb);
        uint8_t v2 = GetReg8(state, reg);
        AluAnd<uint8_t, true>(state, v1, v2);
    }

    // 2. Perform Jcc
    uint8_t cond = op->extra;
    if (CheckCondition(state, cond)) {
        int32_t target_offset = (int32_t)op->imm;
        state->ctx.eip += target_offset;
        return instr_limit;
    } else {
        DecodedOp* next = op + 1;
        if (instr_limit > 0) {
            HandlerFunc h = (HandlerFunc)((intptr_t)g_HandlerBase + next->handler_offset);
            ATTR_MUSTTAIL return h(state, next, instr_limit - 1, utlb);
        }
    }
    return instr_limit;
}

template<int I>
static void RegisterCmov() {
    g_Handlers[0x140 + I] = DispatchWrapper<OpCmov_GvEv<I>>;
    if constexpr (I + 1 < 16) RegisterCmov<I + 1>();
}

template<int I>
static void RegisterJcc() {
    g_Handlers[0x70 + I] = DispatchWrapper<OpJcc_Rel<I>>;   // Jcc rel8
    g_Handlers[0x180 + I] = DispatchWrapper<OpJcc_Rel<I>>;  // Jcc rel32 (0F 8x)
    if constexpr (I + 1 < 16) RegisterJcc<I + 1>();
}

void RegisterControlOps() {
    g_Handlers[0x90] = DispatchWrapper<OpNop>;
    g_Handlers[0x9B] = DispatchWrapper<OpWait>;
    g_Handlers[0xF4] = DispatchWrapper<OpHlt>;
    g_Handlers[0x9C] = DispatchWrapper<OpPushf>;
    g_Handlers[0x9D] = DispatchWrapper<OpPopf>;
    g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel>;    // JMP rel32
    g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel>;    // JMP rel8
    g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>;   // CALL rel32
    g_Handlers[0xC3] = DispatchWrapper<OpRet>;        // RET
    g_Handlers[0xC2] = DispatchWrapper<OpRet_Imm16>;  // RET imm16
    g_Handlers[0xCD] = DispatchWrapper<OpInt>;        // INT imm8
    g_Handlers[0xCC] = DispatchWrapper<OpInt3>;       // INT3
    g_Handlers[0xF5] = DispatchWrapper<OpCmc>;        // CMC
    g_Handlers[0xF8] = DispatchWrapper<OpClc>;        // CLC
    g_Handlers[0xF9] = DispatchWrapper<OpStc>;        // STC
    g_Handlers[0xFA] = DispatchWrapper<OpCli>;        // CLI
    g_Handlers[0xFB] = DispatchWrapper<OpSti>;        // STI
    g_Handlers[0xFC] = DispatchWrapper<OpCld>;        // CLD
    g_Handlers[0xFD] = DispatchWrapper<OpStd>;        // STD
    RegisterJcc<0>();
    g_Handlers[0xCE] = DispatchWrapper<OpInto>;
    g_Handlers[0x131] = DispatchWrapper<OpRdtsc>;  // 0F 31
    g_Handlers[0x1A2] = DispatchWrapper<OpCpuid>;  // 0F A2
    RegisterCmov<0>();
    g_Handlers[0x11F] = DispatchWrapper<OpNop>;         // Multi-byte NOP (0F 1F)
    g_Handlers[0x1AE] = DispatchWrapper<OpGroup_0FAE>;  // 0F AE /r

    // Fused Handlers
    g_Handlers[OP_FUSED_CMP_RR_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_RR_JCC>;
    g_Handlers[OP_FUSED_CMP_RI_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_RI_JCC>;
    g_Handlers[OP_FUSED_CMP_MR_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_MR_JCC>;
    g_Handlers[OP_FUSED_CMP_RM_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_RM_JCC>;
    g_Handlers[OP_FUSED_CMP_RI8_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_RI8_JCC>;
    g_Handlers[OP_FUSED_CMP_AL_I8_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_AL_I8_JCC>;
    g_Handlers[OP_FUSED_CMP_I8I8_JCC] = OpFused_CmpJcc<OP_FUSED_CMP_I8I8_JCC>;

    g_Handlers[OP_FUSED_TEST_RR_JCC] = OpFused_TestJcc<OP_FUSED_TEST_RR_JCC>;
    g_Handlers[OP_FUSED_TEST_RM_JCC] = OpFused_TestJcc<OP_FUSED_TEST_RM_JCC>;
    g_Handlers[OP_FUSED_TEST_I8I8_RR_JCC] = OpFused_TestJcc<OP_FUSED_TEST_I8I8_RR_JCC>;
    g_Handlers[OP_FUSED_TEST_I8I8_RM_JCC] = OpFused_TestJcc<OP_FUSED_TEST_I8I8_RM_JCC>;
}

}  // namespace x86emu