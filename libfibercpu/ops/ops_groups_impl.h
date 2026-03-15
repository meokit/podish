#pragma once
// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../ops_helpers_template.h"
#include "../state.h"
#include "ops_groups.h"

namespace fiberish {

// =========================================================================================
// Group 1: 0x80 (Eb,Ib), 0x81 (Ev,Iz), 0x82 (Eb,Ib - alias), 0x83 (Ev,Ib)
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow OpGroup1_EbIb_Internal(LogicFuncParams) {
    // 80: Arith r/m8, imm8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = (uint8_t)imm;
    return Helper_Group1<uint8_t, UpdateFlags, FixedSubOp>(state, op, flags_cache, dest, src, utlb);
}

// Fixed Size Templates for Ev operations
template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF, bool IsImm8 = false>
FORCE_INLINE LogicFlow OpGroup1_Ev_T_Internal(LogicFuncParams) {
    // 81: Arith r/m, imm32 (IsImm8=false)
    // 83: Arith r/m, imm8 (IsImm8=true)
    T dest;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest = *res;
    }

    T src;
    if constexpr (IsImm8) {             // 0x83
        src = (T)(int16_t)(int8_t)imm;  // Sign extend byte to T
    } else {                            // 0x81
        src = (T)imm;
    }

    return Helper_Group1<T, UpdateFlags, FixedSubOp>(state, op, flags_cache, dest, src, utlb);
}

FORCE_INLINE MemResult<uint32_t> ReadFusedCmpEvOperand32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    const uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        return GetReg(state, op->modrm & 7);
    }

    const auto* fused = GetFusedCmpEvIbJccRel8Data(op);
    const uint32_t ea_desc = fused->ea_desc;
    const uintptr_t regs_base = reinterpret_cast<uintptr_t>(state->ctx.regs);
    uint32_t base = *reinterpret_cast<const uint32_t*>(regs_base + memdesc::BaseOffset(ea_desc));
    uint32_t index = *reinterpret_cast<const uint32_t*>(regs_base + memdesc::IndexOffset(ea_desc));
    uint32_t addr = base + (index << memdesc::Scale(ea_desc)) + static_cast<int32_t>(fused->mem_disp8);
    const uint8_t seg = memdesc::Segment(ea_desc);
    if (seg >= 5) {
        addr += state->ctx.seg_base[seg];
    }
    return ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
}

template <bool JumpOnEqual>
FORCE_INLINE LogicFlow OpFusedCmpEvIbJccRel8_Internal(LogicFuncParams) {
    auto dest_res = ReadFusedCmpEvOperand32(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;

    const auto* fused = GetFusedCmpEvIbJccRel8Data(op);
    const uint32_t cmp_val = static_cast<uint32_t>(static_cast<int32_t>(static_cast<int8_t>(fused->imm8)));
    const uint32_t res = AluSub<uint32_t, true>(state, flags_cache, *dest_res, cmp_val);
    const bool equal = res == 0;
    const bool taken = JumpOnEqual ? equal : !equal;

    if (taken) {
        *branch = op->next_eip + static_cast<int32_t>(fused->branch_disp8);
        return LogicFlow::ExitToBranch;
    }
    return LogicFlow::Continue;
}

// =========================================================================================
// Group 3: 0xF6 (Eb), 0xF7 (Ev)
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow OpGroup3_Eb_Internal(LogicFuncParams) {
    // F6
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;

    return Helper_Group3<uint8_t, UpdateFlags, FixedSubOp>(state, op, flags_cache, val, utlb, imm);
}

template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow OpGroup3_Ev_T_Internal(LogicFuncParams) {
    // F7
    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val = *res;
    }

    return Helper_Group3<T, UpdateFlags, FixedSubOp>(state, op, flags_cache, val, utlb, imm);
}

// =========================================================================================
// Group 4: 0xFE (Eb) - INC/DEC
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow OpGroup4_Eb_Internal(LogicFuncParams) {
    // FE
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;

    return Helper_Group4<uint8_t, UpdateFlags, FixedSubOp>(state, op, flags_cache, val, utlb);
}

// =========================================================================================
// Group 5: 0xFF (Ev)
// =========================================================================================

template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow OpGroup5_Ev_T_Internal(LogicFuncParams) {
    // FF
    // RMW Instructions!
    // Must use Blocking memory op to avoid looping
    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    }

    return Helper_Group5<T, UpdateFlags, FixedSubOp>(state, op, flags_cache, val, utlb, branch);
}

template <typename T, bool UpdateFlags, uint8_t FixedSubOp>
FORCE_INLINE LogicFlow OpGroup5_Ev_Control_Internal(LogicFuncParams) {
    static_assert(FixedSubOp == 2 || FixedSubOp == 4 || FixedSubOp == 6);

    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    }

    if constexpr (FixedSubOp == 2) {  // CALL Ev (Near Indirect)
        if constexpr (sizeof(T) == 2) {
            uint32_t esp = GetReg(state, ESP) - 2;
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp, static_cast<uint16_t>(op->next_eip), utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            SetReg(state, ESP, esp);
            *branch = val & 0xFFFF;
        } else {
            uint32_t esp = GetReg(state, ESP) - 4;
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp, op->next_eip, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            SetReg(state, ESP, esp);
            *branch = val;
        }
    } else if constexpr (FixedSubOp == 4) {  // JMP Ev (Near Indirect)
        if constexpr (sizeof(T) == 2)
            *branch = val & 0xFFFF;
        else
            *branch = val;
    } else {  // PUSH Ev
        if constexpr (sizeof(T) == 2) {
            uint32_t esp = GetReg(state, ESP) - 2;
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp, static_cast<uint16_t>(val), utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            SetReg(state, ESP, esp);
        } else {
            uint32_t esp = GetReg(state, ESP) - 4;
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp, val, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            SetReg(state, ESP, esp);
        }
    }

    return LogicFlow::Continue;
}

template <typename T>
FORCE_INLINE LogicFlow OpXadd_T_Internal(LogicFuncParams) {
    // Restart on read fail
    T dest_val = 0;
    if constexpr (sizeof(T) == 1) {
        auto res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    } else if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    T src_val = 0;
    if constexpr (sizeof(T) == 1)
        src_val = (T)GetReg8(state, reg);
    else if constexpr (sizeof(T) == 2)
        src_val = (T)GetReg(state, reg) & 0xFFFF;
    else
        src_val = (T)GetReg(state, reg);

    T res = AluAdd(state, flags_cache, dest_val, src_val);

    // Swap: Original Dest -> Src Reg
    if constexpr (sizeof(T) == 1) {
        uint32_t* rptr = GetRegPtr(state, reg & 3);
        uint32_t curr = *rptr;
        if (reg < 4)
            curr = (curr & 0xFFFFFF00) | (dest_val & 0xFF);
        else
            curr = (curr & 0xFFFF00FF) | ((dest_val & 0xFF) << 8);
        *rptr = curr;
    } else if constexpr (sizeof(T) == 2) {
        uint32_t* rptr = GetRegPtr(state, reg);
        *rptr = (*rptr & 0xFFFF0000) | (dest_val & 0xFFFF);
    } else {
        SetReg(state, reg, dest_val);
    }

    // Result -> Dest Memory/Reg (Retry on fail)
    if constexpr (sizeof(T) == 1) {
        if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else if constexpr (sizeof(T) == 2) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpFusedCmpEvIb_JE_Rel8(LogicFuncParams) {
    return OpFusedCmpEvIbJccRel8_Internal<true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpFusedCmpEvIb_JNE_Rel8(LogicFuncParams) {
    return OpFusedCmpEvIbJccRel8_Internal<false>(LogicPassParams);
}

// Wrappers for Dispatch (Generic fallback)
FORCE_INLINE LogicFlow OpGroup1_EvIz_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup1_Ev_T_Internal<uint16_t, true, 0xFF, false>(LogicPassParams);
    else
        return OpGroup1_Ev_T_Internal<uint32_t, true, 0xFF, false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup1_EvIb_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup1_Ev_T_Internal<uint16_t, true, 0xFF, true>(LogicPassParams);
    else
        return OpGroup1_Ev_T_Internal<uint32_t, true, 0xFF, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup3_Ev_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup3_Ev_T_Internal<uint16_t, true>(LogicPassParams);
    else
        return OpGroup3_Ev_T_Internal<uint32_t, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup5_Ev_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup5_Ev_T_Internal<uint16_t, true>(LogicPassParams);
    else
        return OpGroup5_Ev_T_Internal<uint32_t, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup4_Eb_Generic(LogicFuncParams) { return OpGroup4_Eb_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup1_EbIb_Generic(LogicFuncParams) { return OpGroup1_EbIb_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup3_Eb_Generic(LogicFuncParams) { return OpGroup3_Eb_Internal<true>(LogicPassParams); }

// Implements wrappers: e.g. OpGroup1_EbIb_0_Flags, OpGroup1_EbIb_0_NoFlags
#define IMPL_G1_EB(subop, name)                                       \
    FORCE_INLINE LogicFlow name##_Flags(LogicFuncParams) {            \
        return OpGroup1_EbIb_Internal<true, subop>(LogicPassParams);  \
    }                                                                 \
    FORCE_INLINE LogicFlow name##_NoFlags(LogicFuncParams) {          \
        return OpGroup1_EbIb_Internal<false, subop>(LogicPassParams); \
    }

IMPL_G1_EB(0, OpGroup1_EbIb_Add)
IMPL_G1_EB(1, OpGroup1_EbIb_Or)
IMPL_G1_EB(2, OpGroup1_EbIb_Adc)
IMPL_G1_EB(3, OpGroup1_EbIb_Sbb)
IMPL_G1_EB(4, OpGroup1_EbIb_And)
IMPL_G1_EB(5, OpGroup1_EbIb_Sub)
IMPL_G1_EB(6, OpGroup1_EbIb_Xor)
IMPL_G1_EB(7, OpGroup1_EbIb_Cmp)

// Implements wrappers: e.g. OpGroup1_EvIz_T_Add_32_Flags
// Param `func` is OpGroup1_EvIz_T_Internal or OpGroup1_EvIb_T_Internal (templated)
#define IMPL_EV_SPEC(subop, name, funcName, isImm8)                       \
    FORCE_INLINE LogicFlow name##_32_Flags(LogicFuncParams) {             \
        return funcName<uint32_t, true, subop, isImm8>(LogicPassParams);  \
    }                                                                     \
    FORCE_INLINE LogicFlow name##_32_NoFlags(LogicFuncParams) {           \
        return funcName<uint32_t, false, subop, isImm8>(LogicPassParams); \
    }                                                                     \
    FORCE_INLINE LogicFlow name##_16_Flags(LogicFuncParams) {             \
        return funcName<uint16_t, true, subop, isImm8>(LogicPassParams);  \
    }                                                                     \
    FORCE_INLINE LogicFlow name##_16_NoFlags(LogicFuncParams) {           \
        return funcName<uint16_t, false, subop, isImm8>(LogicPassParams); \
    }

// Group 1 Iz (0x81) - IsImm8=false
IMPL_EV_SPEC(0, OpGroup1_EvIz_Add, OpGroup1_Ev_T_Internal, false)
IMPL_EV_SPEC(5, OpGroup1_EvIz_Sub, OpGroup1_Ev_T_Internal, false)
IMPL_EV_SPEC(7, OpGroup1_EvIz_Cmp, OpGroup1_Ev_T_Internal, false)

// Group 1 Ib (0x83) - IsImm8=true
IMPL_EV_SPEC(0, OpGroup1_EvIb_Add, OpGroup1_Ev_T_Internal, true)
IMPL_EV_SPEC(5, OpGroup1_EvIb_Sub, OpGroup1_Ev_T_Internal, true)
IMPL_EV_SPEC(7, OpGroup1_EvIb_Cmp, OpGroup1_Ev_T_Internal, true)

#undef IMPL_EV_SPEC

// For Group 3 and 5, which use distinct templates without IsImm8
#define IMPL_EV_SPEC_SIMPLE(subop, name, funcName)                \
    FORCE_INLINE LogicFlow name##_32_Flags(LogicFuncParams) {     \
        return funcName<uint32_t, true, subop>(LogicPassParams);  \
    }                                                             \
    FORCE_INLINE LogicFlow name##_32_NoFlags(LogicFuncParams) {   \
        return funcName<uint32_t, false, subop>(LogicPassParams); \
    }                                                             \
    FORCE_INLINE LogicFlow name##_16_Flags(LogicFuncParams) {     \
        return funcName<uint16_t, true, subop>(LogicPassParams);  \
    }                                                             \
    FORCE_INLINE LogicFlow name##_16_NoFlags(LogicFuncParams) {   \
        return funcName<uint16_t, false, subop>(LogicPassParams); \
    }

// Group 3
IMPL_EV_SPEC_SIMPLE(2, OpGroup3_Ev_Not, OpGroup3_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(3, OpGroup3_Ev_Neg, OpGroup3_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(4, OpGroup3_Ev_Mul, OpGroup3_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(5, OpGroup3_Ev_Imul, OpGroup3_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(6, OpGroup3_Ev_Div, OpGroup3_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(7, OpGroup3_Ev_Idiv, OpGroup3_Ev_T_Internal)

// Group 5
IMPL_EV_SPEC_SIMPLE(0, OpGroup5_Ev_Inc, OpGroup5_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(1, OpGroup5_Ev_Dec, OpGroup5_Ev_T_Internal)
IMPL_EV_SPEC_SIMPLE(2, OpGroup5_Ev_Call, OpGroup5_Ev_Control_Internal)
IMPL_EV_SPEC_SIMPLE(4, OpGroup5_Ev_Jmp, OpGroup5_Ev_Control_Internal)
IMPL_EV_SPEC_SIMPLE(6, OpGroup5_Ev_Push, OpGroup5_Ev_Control_Internal)

// Group 3 Eb
#define IMPL_G3_EB(subop, name)                                     \
    FORCE_INLINE LogicFlow name##_Flags(LogicFuncParams) {          \
        return OpGroup3_Eb_Internal<true, subop>(LogicPassParams);  \
    }                                                               \
    FORCE_INLINE LogicFlow name##_NoFlags(LogicFuncParams) {        \
        return OpGroup3_Eb_Internal<false, subop>(LogicPassParams); \
    }

IMPL_G3_EB(2, OpGroup3_Eb_Not)
IMPL_G3_EB(3, OpGroup3_Eb_Neg)
IMPL_G3_EB(4, OpGroup3_Eb_Mul)
IMPL_G3_EB(5, OpGroup3_Eb_Imul)
IMPL_G3_EB(6, OpGroup3_Eb_Div)
IMPL_G3_EB(7, OpGroup3_Eb_Idiv)

// Group 4 Eb
#define IMPL_G4_EB(subop, name)                                     \
    FORCE_INLINE LogicFlow name##_Flags(LogicFuncParams) {          \
        return OpGroup4_Eb_Internal<true, subop>(LogicPassParams);  \
    }                                                               \
    FORCE_INLINE LogicFlow name##_NoFlags(LogicFuncParams) {        \
        return OpGroup4_Eb_Internal<false, subop>(LogicPassParams); \
    }

IMPL_G4_EB(0, OpGroup4_Eb_Inc)
IMPL_G4_EB(1, OpGroup4_Eb_Dec)

// Misc Ops (unchanged)
FORCE_INLINE LogicFlow OpPrefetch(LogicFuncParams) {
    // 0F 18 /r: PREFETCHh (T0, T1, T2, NTA)
    // These are hints for the processor about future memory access.
    // In our emulator, they are pure NOPs. They never fault even if address is invalid.
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCdq(LogicFuncParams) {
    uint32_t eax = GetReg(state, EAX);
    uint32_t edx = ((int32_t)eax < 0) ? 0xFFFFFFFF : 0;
    SetReg(state, EDX, edx);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCwde(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {
        int8_t val = (int8_t)GetReg(state, EAX);
        uint32_t current = GetReg(state, EAX);
        uint32_t res = (current & 0xFFFF0000) | (uint16_t)(int16_t)val;
        SetReg(state, EAX, res);
    } else {
        int16_t val = (int16_t)GetReg(state, EAX);
        SetReg(state, EAX, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpUd2_Groups(LogicFuncParams) {
    state->fault_vector = 6;
    if (!state->hooks.on_invalid_opcode(state)) {
        state->status = EmuStatus::Fault;
    }
    // Always exit to sync EIP to the start of this instruction.
    // If handled correctly by C#, status might be set to Yield or EIP changed.
    return LogicFlow::ExitOnCurrentEIP;
}

FORCE_INLINE LogicFlow OpGroup9(LogicFuncParams) {
    uint8_t sub = (op->modrm >> 3) & 7;
    if (sub == 1) {  // CMPXCHG8B
        uint32_t addr = ComputeLinearAddress(state, op);
        auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!mem_res) return LogicFlow::RestartMemoryOp;  // Restart on read fail
        uint64_t mem_val = *mem_res;

        uint32_t eax = GetReg(state, EAX);
        uint32_t edx = GetReg(state, EDX);
        uint64_t edx_eax = ((uint64_t)edx << 32) | eax;
        if (mem_val == edx_eax) {
            SetFlagBits(flags_cache, ZF_MASK);
            uint32_t ebx = GetReg(state, EBX);
            uint32_t ecx = GetReg(state, ECX);
            uint64_t ecx_ebx = ((uint64_t)ecx << 32) | ebx;

            // Retry on write fail
            if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, ecx_ebx, utlb, op))
                return LogicFlow::RetryMemoryOp;
        } else {
            ClearFlagBits(flags_cache, ZF_MASK);
            SetReg(state, EAX, (uint32_t)mem_val);
            SetReg(state, EDX, (uint32_t)(mem_val >> 32));
        }
    } else {
        return OpUd2_Groups(LogicPassParams);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXadd_Byte(LogicFuncParams) { return OpXadd_T_Internal<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpXadd_Word(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpXadd_T_Internal<uint16_t>(LogicPassParams);
    else
        return OpXadd_T_Internal<uint32_t>(LogicPassParams);
}

}  // namespace op

// =========================================================================================
// Registration
// =========================================================================================

}  // namespace fiberish
