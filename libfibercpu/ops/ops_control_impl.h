#pragma once
// Control Flow
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>
#include <chrono>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_control.h"

namespace fiberish {

template <bool IsRel8>
FORCE_INLINE LogicFlow OpJmp_Rel_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // E9: JMP rel32, EB: JMP rel8
    int32_t offset;
    if (IsRel8) {
        // 8-bit relative jump
        offset = (int32_t)(int8_t)(op->imm & 0xFF);
    } else {
        // 32-bit relative jump (E9)
        offset = (int32_t)op->imm;
    }
    op->branch_target = op->next_eip + offset;
    return LogicFlow::Continue;
}

template <uint8_t Cond, bool IsRel8>
FORCE_INLINE LogicFlow OpJcc_Rel_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    if (CheckConditionFixed<Cond>(state)) {
        int32_t offset;
        if constexpr (IsRel8) {
            offset = (int32_t)(int8_t)(op->imm & 0xFF);
        } else {
            offset = (int32_t)op->imm;
        }
        op->branch_target = op->next_eip + offset;
    }
    return LogicFlow::Continue;
}

// CMOV Implementation
template <uint8_t Cond, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpCmov_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 4x: CMOVcc r16, r/m16 OR CMOVcc r32, r/m32
    // If condition is FALSE, NOP (no memory read).

    if (CheckConditionFixed<Cond>(state)) {
        uint8_t reg = (op->modrm >> 3) & 7;

        if constexpr (S == Specialized::ModReg) {
            // Fast path: Register operand
            uint8_t rm = op->modrm & 7;
            if (op->prefixes.flags.opsize) {
                // 16-bit
                uint16_t val = (uint16_t)(GetReg(state, rm) & 0xFFFF);
                uint32_t current = state->ctx.regs[reg];
                state->ctx.regs[reg] = (current & 0xFFFF0000) | val;
            } else {
                // 32-bit
                uint32_t val = GetReg(state, rm);
                state->ctx.regs[reg] = val;
            }
        } else {
            if (op->prefixes.flags.opsize) {
                // 16-bit
                auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!val_res) return LogicFlow::RestartMemoryOp;
                uint16_t val = *val_res;
                uint32_t current = state->ctx.regs[reg];
                state->ctx.regs[reg] = (current & 0xFFFF0000) | val;
            } else {
                // 32-bit
                auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!val_res) return LogicFlow::RestartMemoryOp;
                uint32_t val = *val_res;
                state->ctx.regs[reg] = val;
            }
        }
    }
    return LogicFlow::Continue;
}

// Helper for interrupts
inline void RaiseInterrupt(EmuState* state, uint8_t vector, DecodedOp* op) {
    // Sync EIP
    state->ctx.eip = op->next_eip;

    // Check if hook handles it
    bool handled = false;
    if (state->interrupt_handlers[vector]) {
        handled = state->interrupt_handlers[vector](state, vector, state->interrupt_userdata[vector]);
    }

    // For now, we just fault if not handled, as we don't fully emulate IDT in user mode runner.
    if (!handled) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector;
    }
}

namespace op {

FORCE_INLINE LogicFlow OpJmp_Rel8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpJmp_Rel_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpJmp_Rel32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpJmp_Rel_Internal<false>(state, op, utlb);
}

// Named wrappers for Jcc specializations
#define JCC_WRAPPERS(cond, name)                                                               \
    FORCE_INLINE LogicFlow OpJcc_##name##_Rel8(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {  \
        return OpJcc_Rel_Internal<cond, true>(s, o, u);                                        \
    }                                                                                          \
    FORCE_INLINE LogicFlow OpJcc_##name##_Rel32(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        return OpJcc_Rel_Internal<cond, false>(s, o, u);                                       \
    }

JCC_WRAPPERS(0, O)
JCC_WRAPPERS(1, NO)
JCC_WRAPPERS(2, B)
JCC_WRAPPERS(3, AE)
JCC_WRAPPERS(4, E)
JCC_WRAPPERS(5, NE)
JCC_WRAPPERS(6, BE)
JCC_WRAPPERS(7, A)
JCC_WRAPPERS(8, S)
JCC_WRAPPERS(9, NS)
JCC_WRAPPERS(10, P)
JCC_WRAPPERS(11, NP)
JCC_WRAPPERS(12, L)
JCC_WRAPPERS(13, GE)
JCC_WRAPPERS(14, LE)
JCC_WRAPPERS(15, G)
#undef JCC_WRAPPERS

FORCE_INLINE LogicFlow OpCall_Rel(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // E8: CALL rel32
    // Push Return Address
    // If Push fails, we Restart. SP is guaranteed untouched by Push on failure.
    // Use Push<T, true> to request Restart explicitly on TLB Miss.
    if (!Push<uint32_t, true>(state, op->next_eip, utlb, op)) return LogicFlow::RestartMemoryOp;

    // Jump relative to Next Insn
    op->branch_target = op->next_eip + (int32_t)op->imm;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRet(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C3: RET
    // Use Pop<T, true> to request Restart explicitly on TLB Miss.
    auto val_res = Pop<uint32_t, true>(state, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    op->branch_target = *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRet_Imm16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C2: RET imm16
    auto val_res = Pop<uint32_t, true>(state, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    op->branch_target = *val_res;

    // Pop imm16 bytes from stack
    uint32_t esp = GetReg(state, ESP);
    esp += (uint16_t)op->imm;
    SetReg(state, ESP, esp);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPushf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9C: PUSHF/PUSHFD
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, (uint16_t)state->ctx.eflags, utlb, op)) return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, state->ctx.eflags & 0x00FCFFFF, utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPopf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9D: POPF/POPFD
    if (op->prefixes.flags.opsize) {
        // POPF (16-bit)
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint16_t val = *val_res;

        uint32_t mask = state->ctx.eflags_mask & 0xFFFF;
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        state->ctx.eflags = new_flags;
    } else {
        // POPFD (32-bit)
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint32_t val = *val_res;

        uint32_t mask = state->ctx.eflags_mask;
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        state->ctx.eflags = new_flags;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F9: STC
    state->ctx.eflags |= CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpClc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F8: CLC
    state->ctx.eflags &= ~CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F5: CMC (Complement Carry)
    state->ctx.eflags ^= CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStd(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FD: STD (Set Direction Flag)
    state->ctx.eflags |= 0x400;  // DF Mask
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCld(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FC: CLD (Clear Direction Flag)
    state->ctx.eflags &= ~0x400;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSti(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FB: STI (Set Interrupt Flag)
    // Privileged Instruction. In User Mode (CPL=3, IOPL=0), this faults.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;    // #GP
    return LogicFlow::Continue;  // Or ExitOnCurrentEIP? Standard flow handles Status check
}

FORCE_INLINE LogicFlow OpCli(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FA: CLI (Clear Interrupt Flag)
    // Privileged Instruction.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCpuid(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRdtsc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 31: RDTSC
    uint64_t tsc = 0;
    if (state->tsc_mode == 1) {
        // Real-time mode
        auto now = std::chrono::steady_clock::now();
        auto elapsed = std::chrono::duration_cast<std::chrono::nanoseconds>(now - state->tsc_start_time).count();
        // tsc = offset + (elapsed_ns * frequency) / 1,000,000,000
        unsigned __int128 val = (unsigned __int128)elapsed * state->tsc_frequency;
        tsc = state->tsc_offset + (uint64_t)(val / 1000000000ULL);
    } else {
        // Fixed increment mode
        tsc = state->tsc_offset + state->tsc_fixed_counter;
        state->tsc_fixed_counter += 1000;  // Mock increment per call
    }

    uint32_t low = (uint32_t)tsc;
    uint32_t high = (uint32_t)(tsc >> 32);

    SetReg(state, EAX, low);
    SetReg(state, EDX, high);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpWait(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9B: WAIT/FWAIT n
    // Check pending FPU exceptions?
    // For now NOP.
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup_0FAE(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AE /r
    uint8_t sub = (op->modrm >> 3) & 7;
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    switch (sub) {
        case 2:  // LDMXCSR m32
            if (mod != 3) {
                uint32_t addr = ComputeLinearAddress(state, op);
                auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::RestartMemoryOp;
                state->ctx.mxcsr = *val_res;
            }
            break;
        case 3:  // STMXCSR m32
            if (mod != 3) {
                uint32_t addr = ComputeLinearAddress(state, op);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, state->ctx.mxcsr, utlb, op))
                    return LogicFlow::RetryMemoryOp;
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
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpNop(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // NOP - Do nothing
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpHlt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // HLT
    // TODO: Don't allow on userspace?
    state->status = EmuStatus::Stopped;
    return LogicFlow::ExitOnCurrentEIP;
}

FORCE_INLINE LogicFlow OpInt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CD ib: INT n
    uint8_t vector = (uint8_t)op->imm;
    utlb->invalidate();

    // Set EIP to next instruction (return address)
    state->ctx.eip = op->next_eip;

    RaiseInterrupt(state, vector, op);

    // If EIP changed (e.g. signal handler, sigreturn), exit block
    if (state->ctx.eip != op->next_eip || state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpInt3(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CC: INT 3
    utlb->invalidate();
    RaiseInterrupt(state, 3, op);
    if (state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpInto(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // CE: INTO
    if (state->ctx.eflags & OF_MASK) {
        utlb->invalidate();
        RaiseInterrupt(state, 4, op);
    }
    if (state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

// Named wrappers for Cmov specializations
#define CMOV_WRAPPERS(cond, name)                                                                \
    FORCE_INLINE LogicFlow OpCmov_##name(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {          \
        return OpCmov_Internal<cond, Specialized::None>(s, o, u);                                \
    }                                                                                            \
    FORCE_INLINE LogicFlow OpCmov_##name##_ModReg(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        return OpCmov_Internal<cond, Specialized::ModReg>(s, o, u);                              \
    }

CMOV_WRAPPERS(0, O)
CMOV_WRAPPERS(1, NO)
CMOV_WRAPPERS(2, B)
CMOV_WRAPPERS(3, AE)
CMOV_WRAPPERS(4, E)
CMOV_WRAPPERS(5, NE)
CMOV_WRAPPERS(6, BE)
CMOV_WRAPPERS(7, A)
CMOV_WRAPPERS(8, S)
CMOV_WRAPPERS(9, NS)
CMOV_WRAPPERS(10, P)
CMOV_WRAPPERS(11, NP)
CMOV_WRAPPERS(12, L)
CMOV_WRAPPERS(13, GE)
CMOV_WRAPPERS(14, LE)
CMOV_WRAPPERS(15, G)
#undef CMOV_WRAPPERS

}  // namespace op

static HandlerFunc g_JccRel8Wrappers[] = {
    DispatchWrapper<op::OpJcc_O_Rel8>,  DispatchWrapper<op::OpJcc_NO_Rel8>, DispatchWrapper<op::OpJcc_B_Rel8>,
    DispatchWrapper<op::OpJcc_AE_Rel8>, DispatchWrapper<op::OpJcc_E_Rel8>,  DispatchWrapper<op::OpJcc_NE_Rel8>,
    DispatchWrapper<op::OpJcc_BE_Rel8>, DispatchWrapper<op::OpJcc_A_Rel8>,  DispatchWrapper<op::OpJcc_S_Rel8>,
    DispatchWrapper<op::OpJcc_NS_Rel8>, DispatchWrapper<op::OpJcc_P_Rel8>,  DispatchWrapper<op::OpJcc_NP_Rel8>,
    DispatchWrapper<op::OpJcc_L_Rel8>,  DispatchWrapper<op::OpJcc_GE_Rel8>, DispatchWrapper<op::OpJcc_LE_Rel8>,
    DispatchWrapper<op::OpJcc_G_Rel8>};

static HandlerFunc g_JccRel32Wrappers[] = {
    DispatchWrapper<op::OpJcc_O_Rel32>,  DispatchWrapper<op::OpJcc_NO_Rel32>, DispatchWrapper<op::OpJcc_B_Rel32>,
    DispatchWrapper<op::OpJcc_AE_Rel32>, DispatchWrapper<op::OpJcc_E_Rel32>,  DispatchWrapper<op::OpJcc_NE_Rel32>,
    DispatchWrapper<op::OpJcc_BE_Rel32>, DispatchWrapper<op::OpJcc_A_Rel32>,  DispatchWrapper<op::OpJcc_S_Rel32>,
    DispatchWrapper<op::OpJcc_NS_Rel32>, DispatchWrapper<op::OpJcc_P_Rel32>,  DispatchWrapper<op::OpJcc_NP_Rel32>,
    DispatchWrapper<op::OpJcc_L_Rel32>,  DispatchWrapper<op::OpJcc_GE_Rel32>, DispatchWrapper<op::OpJcc_LE_Rel32>,
    DispatchWrapper<op::OpJcc_G_Rel32>};

static HandlerFunc g_CmovWrappers[] = {
    DispatchWrapper<op::OpCmov_O>,  DispatchWrapper<op::OpCmov_NO>, DispatchWrapper<op::OpCmov_B>,
    DispatchWrapper<op::OpCmov_AE>, DispatchWrapper<op::OpCmov_E>,  DispatchWrapper<op::OpCmov_NE>,
    DispatchWrapper<op::OpCmov_BE>, DispatchWrapper<op::OpCmov_A>,  DispatchWrapper<op::OpCmov_S>,
    DispatchWrapper<op::OpCmov_NS>, DispatchWrapper<op::OpCmov_P>,  DispatchWrapper<op::OpCmov_NP>,
    DispatchWrapper<op::OpCmov_L>,  DispatchWrapper<op::OpCmov_GE>, DispatchWrapper<op::OpCmov_LE>,
    DispatchWrapper<op::OpCmov_G>};

}  // namespace fiberish
