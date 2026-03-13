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

#ifdef FIBERCPU_ENABLE_JCC_PROFILE
FORCE_INLINE void RecordConditionalBranchDecision(EmuState* state, const DecodedOp* op, bool taken) {
    auto& counters = state->jcc_profile_counts[reinterpret_cast<uintptr_t>(op->handler)];
    if (taken) {
        counters.taken++;
    } else {
        counters.not_taken++;
    }
}
#else
FORCE_INLINE void RecordConditionalBranchDecision(EmuState*, const DecodedOp*, bool) {}
#endif

template <bool IsRel8>
FORCE_INLINE LogicFlow OpJmp_Rel_Internal(LogicFuncParams) {
    // E9: JMP rel32, EB: JMP rel8
    int32_t offset;
    if (IsRel8) {
        // 8-bit relative jump
        offset = (int32_t)(int8_t)(imm & 0xFF);
    } else {
        // 32-bit relative jump (E9)
        offset = (int32_t)imm;
    }
    *branch = op->next_eip + offset;
    return LogicFlow::ExitToBranch;
}

template <uint8_t Cond, bool IsRel8>
FORCE_INLINE LogicFlow OpJcc_Rel_Internal(LogicFuncParams) {
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    if (CheckConditionFixed<Cond>(state)) {
        RecordConditionalBranchDecision(state, op, true);
        int32_t offset;
        if constexpr (IsRel8) {
            offset = (int32_t)(int8_t)(imm & 0xFF);
        } else {
            offset = (int32_t)imm;
        }
        *branch = op->next_eip + offset;
        return LogicFlow::ExitToBranch;
    }
    RecordConditionalBranchDecision(state, op, false);
    return LogicFlow::Continue;
}

// CMOV Implementation
template <uint8_t Cond, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpCmov_Internal(LogicFuncParams) {
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

// Helper for interrupts (Traps/Interrupts - EIP points to next instruction)
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

FORCE_INLINE LogicFlow OpJmp_Rel8(LogicFuncParams) { return OpJmp_Rel_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpJmp_Rel32(LogicFuncParams) { return OpJmp_Rel_Internal<false>(LogicPassParams); }

FORCE_INLINE LogicFlow OpJecxz(LogicFuncParams) {
    // E3: JECXZ/JCXZ rel8
    bool jump = false;
    if (op->prefixes.flags.addrsize) {
        // 16-bit address size: CX
        jump = (GetReg(state, ECX) & 0xFFFF) == 0;
    } else {
        // 32-bit address size: ECX
        jump = GetReg(state, ECX) == 0;
    }

    if (jump) {
        RecordConditionalBranchDecision(state, op, true);
        int32_t offset = (int32_t)(int8_t)(imm & 0xFF);
        *branch = op->next_eip + offset;
        return LogicFlow::ExitToBranch;
    }
    RecordConditionalBranchDecision(state, op, false);
    return LogicFlow::Continue;
}

template <bool CheckZF, bool ExpectedZF>
FORCE_INLINE LogicFlow OpLoop_Internal(LogicFuncParams) {
    uint32_t count;
    if (op->prefixes.flags.addrsize) {
        count = GetReg(state, ECX) & 0xFFFF;
        count = (count - 1) & 0xFFFF;
        uint32_t current = GetReg(state, ECX);
        SetReg(state, ECX, (current & 0xFFFF0000u) | count);
    } else {
        count = GetReg(state, ECX) - 1;
        SetReg(state, ECX, count);
    }

    bool jump = count != 0;
    if constexpr (CheckZF) {
        jump = jump && (((state->ctx.eflags & ZF_MASK) != 0) == ExpectedZF);
    }

    if (jump) {
        RecordConditionalBranchDecision(state, op, true);
        const int32_t offset = static_cast<int8_t>(imm & 0xFF);
        *branch = op->next_eip + offset;
        return LogicFlow::ExitToBranch;
    }
    RecordConditionalBranchDecision(state, op, false);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLoopne(LogicFuncParams) { return OpLoop_Internal<true, false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpLoope(LogicFuncParams) { return OpLoop_Internal<true, true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpLoop(LogicFuncParams) { return OpLoop_Internal<false, false>(LogicPassParams); }

// Named wrappers for Jcc specializations
#define JCC_WRAPPERS(cond, name)                                   \
    FORCE_INLINE LogicFlow OpJcc_##name##_Rel8(LogicFuncParams) {  \
        return OpJcc_Rel_Internal<cond, true>(LogicPassParams);    \
    }                                                              \
    FORCE_INLINE LogicFlow OpJcc_##name##_Rel32(LogicFuncParams) { \
        return OpJcc_Rel_Internal<cond, false>(LogicPassParams);   \
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

FORCE_INLINE LogicFlow OpCall_Rel(LogicFuncParams) {
    // E8: CALL rel32
    // Push Return Address
    // If Push fails, we Restart. SP is guaranteed untouched by Push on failure.
    // Use Push<T, true> to request Restart explicitly on TLB Miss.
    if (!Push<uint32_t, true>(state, op->next_eip, utlb, op)) return LogicFlow::RestartMemoryOp;

    // Jump relative to Next Insn
    *branch = op->next_eip + (int32_t)imm;
    return LogicFlow::ExitToBranch;
}

FORCE_INLINE LogicFlow OpRet(LogicFuncParams) {
    // C3: RET
    // Use Pop<T, true> to request Restart explicitly on TLB Miss.
    auto val_res = Pop<uint32_t, true>(state, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    *branch = *val_res;
    return LogicFlow::ExitToBranch;
}

FORCE_INLINE LogicFlow OpRet_Imm16(LogicFuncParams) {
    // C2: RET imm16
    auto val_res = Pop<uint32_t, true>(state, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    *branch = *val_res;

    // Pop imm16 bytes from stack
    uint32_t esp = GetReg(state, ESP);
    esp += (uint16_t)imm;
    SetReg(state, ESP, esp);
    return LogicFlow::ExitToBranch;
}

FORCE_INLINE LogicFlow OpPushf(LogicFuncParams) {
    // 9C: PUSHF/PUSHFD
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, (uint16_t)state->ctx.eflags, utlb, op)) return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, state->ctx.eflags & 0x00FCFFFF, utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPopf(LogicFuncParams) {
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

FORCE_INLINE LogicFlow OpStc(LogicFuncParams) {
    // F9: STC
    state->ctx.eflags |= CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpClc(LogicFuncParams) {
    // F8: CLC
    state->ctx.eflags &= ~CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmc(LogicFuncParams) {
    // F5: CMC (Complement Carry)
    state->ctx.eflags ^= CF_MASK;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStd(LogicFuncParams) {
    // FD: STD (Set Direction Flag)
    state->ctx.eflags |= 0x400;  // DF Mask
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCld(LogicFuncParams) {
    // FC: CLD (Clear Direction Flag)
    state->ctx.eflags &= ~0x400;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSti(LogicFuncParams) {
    // FB: STI (Set Interrupt Flag)
    // Privileged Instruction. In User Mode (CPL=3, IOPL=0), this faults.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;    // #GP
    return LogicFlow::Continue;  // Or ExitOnCurrentEIP? Standard flow handles Status check
}

FORCE_INLINE LogicFlow OpCli(LogicFuncParams) {
    // FA: CLI (Clear Interrupt Flag)
    // Privileged Instruction.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCpuid(LogicFuncParams) {
    // 0F A2: CPUID
    uint32_t leaf = GetReg(state, EAX);
    uint32_t ecx_in = GetReg(state, ECX);

    uint32_t eax = 0, ebx = 0, ecx = 0, edx = 0;

    switch (leaf) {
        case 0x00000000:
            // Vendor: "GenuineIntel". Advertise leaves up to 7.
            eax = 0x00000007;
            ebx = 0x756E6547;  // "Genu"
            edx = 0x49656E69;  // "ineI"
            ecx = 0x6C65746E;  // "ntel"
            break;
        case 0x00000001:
            // Family 6 model-style id (legacy i686-like baseline).
            // Keep capabilities conservative and aligned to implemented instruction families.
            eax = 0x00000680;
            ebx = 0;
            ecx = 0;
            edx = 0;
            // EDX bits
            edx |= (1u << 0);   // FPU
            edx |= (1u << 4);   // TSC
            edx |= (1u << 8);   // CMPXCHG8B
            edx |= (1u << 15);  // CMOV
            edx |= (1u << 19);  // CLFLUSH (supported as no-op in 0F AE /7)
            edx |= (1u << 23);  // MMX
            edx |= (1u << 25);  // SSE
            edx |= (1u << 26);  // SSE2
            break;
        case 0x00000007:
            // Structured extended features. We currently expose no additional feature bits.
            if (ecx_in == 0) {
                eax = 0;
                ebx = 0;
                ecx = 0;
                edx = 0;
            }
            break;
        case 0x80000000:
            eax = 0x80000001;
            break;
        case 0x80000001:
            // No extra extended features advertised.
            eax = 0;
            ebx = 0;
            ecx = 0;
            edx = 0;
            break;
        default:
            // Unknown leaf -> zeros
            break;
    }

    SetReg(state, EAX, eax);
    SetReg(state, EBX, ebx);
    SetReg(state, ECX, ecx);
    SetReg(state, EDX, edx);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRdtsc(LogicFuncParams) {
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

FORCE_INLINE LogicFlow OpWait(LogicFuncParams) {
    // 9B: WAIT/FWAIT n
    // Check pending FPU exceptions?
    // For now NOP.
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup_0FAE(LogicFuncParams) {
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

FORCE_INLINE LogicFlow OpNop(LogicFuncParams) {
    // NOP - Do nothing
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpHlt(LogicFuncParams) {
    // HLT
    LogMsgState(state, 4, "[ABORT] USER ABORT DETECTED: HLT instruction executed at EIP: 0x%08X",
                op->next_eip - op->len);
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
    return LogicFlow::ExitOnCurrentEIP;
}

FORCE_INLINE LogicFlow OpInt(LogicFuncParams) {
    // CD ib: INT n
    uint8_t vector = (uint8_t)imm;
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

FORCE_INLINE LogicFlow OpInt3(LogicFuncParams) {
    // CC: INT 3
    utlb->invalidate();
    RaiseInterrupt(state, 3, op);
    if (state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpInto(LogicFuncParams) {
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
#define CMOV_WRAPPERS(cond, name)                                           \
    FORCE_INLINE LogicFlow OpCmov_##name(LogicFuncParams) {                 \
        return OpCmov_Internal<cond, Specialized::None>(LogicPassParams);   \
    }                                                                       \
    FORCE_INLINE LogicFlow OpCmov_##name##_ModReg(LogicFuncParams) {        \
        return OpCmov_Internal<cond, Specialized::ModReg>(LogicPassParams); \
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

FORCE_INLINE LogicFlow OpBound(LogicFuncParams) {
    // 62: BOUND r16/32, m16&16 / m32&32
    // Check if value (r) is within bounds [m_low, m_high]
    // Signed check!

    // Bounds are [Lower, Upper] at effective address
    uint32_t addr = ComputeLinearAddress(state, op);

    if (op->prefixes.flags.opsize) {
        // 16-bit
        int16_t val = (int16_t)GetReg(state, (op->modrm >> 3) & 7);

        auto low_res = ReadMem<int16_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!low_res) return LogicFlow::RestartMemoryOp;
        int16_t lower = *low_res;

        auto high_res = ReadMem<int16_t, OpOnTLBMiss::Restart>(state, addr + 2, utlb, op);
        if (!high_res) return LogicFlow::RestartMemoryOp;
        int16_t upper = *high_res;

        if (val < lower || val > upper) {
            utlb->invalidate();
            state->status = EmuStatus::Fault;
            state->fault_vector = 5;  // #BR
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        // 32-bit
        int32_t val = (int32_t)GetReg(state, (op->modrm >> 3) & 7);

        auto low_res = ReadMem<int32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!low_res) return LogicFlow::RestartMemoryOp;
        int32_t lower = *low_res;

        auto high_res = ReadMem<int32_t, OpOnTLBMiss::Restart>(state, addr + 4, utlb, op);
        if (!high_res) return LogicFlow::RestartMemoryOp;
        int32_t upper = *high_res;

        if (val < lower || val > upper) {
            utlb->invalidate();
            state->status = EmuStatus::Fault;
            state->fault_vector = 5;  // #BR
            return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

}  // namespace op
}  // namespace fiberish
