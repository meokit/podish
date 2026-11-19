#pragma once
// Comparison & Test
// Auto-generated specialization refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

// ============================================================================
// Specializations for OpCmp_EvGv (39: CMP r/m16/32, r16/32)
// ============================================================================

template <typename T, AddrModeSpec Form>
FORCE_INLINE MemResult<T> ReadCompareRm(EmuState* state, GprCache& gpr, DecodedOp* op, mem::MicroTLB* utlb) {
    if constexpr (Form == AddrModeSpec::ModReg) return static_cast<T>(gpr.GetReg(state, op->modrm & 7));
    return ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
}

template <OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmp_EvGv_Internal(LogicFuncParams) {
    // 39: CMP r/m16/32, r16/32
    const bool opsize = OperandIs16<Size>(op);

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        uint16_t dest;
        if constexpr (Form == AddrModeSpec::ModReg) {
            dest = (uint16_t)gpr.GetReg(state, op->modrm & 7);
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }
        uint16_t src = (uint16_t)gpr.GetReg(state, reg);
        AluCmp<uint16_t>(state, flags_cache, dest, src);
    } else {
        uint32_t dest;
        if constexpr (Form == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, op->modrm & 7);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }
        uint32_t src = gpr.GetReg(state, reg);
        AluCmp<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmp_GvEv (3B: CMP r16/32, r/m16/32)
// ============================================================================

template <OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmp_GvEv_Internal(LogicFuncParams) {
    // 3B: CMP r16/32, r/m16/32
    const bool opsize = OperandIs16<Size>(op);

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        uint16_t dest = (uint16_t)gpr.GetReg(state, reg);
        auto src_res = ReadCompareRm<uint16_t, Form>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;
        AluCmp<uint16_t>(state, flags_cache, dest, src);
    } else {
        uint32_t dest = gpr.GetReg(state, reg);
        auto src_res = ReadCompareRm<uint32_t, Form>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;
        AluCmp<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpTest_EvGv (85: TEST r/m16/32, r16/32)
// ============================================================================

template <OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpTest_EvGv_Internal(LogicFuncParams) {
    // 85: TEST r/m16/32, r16/32
    const bool opsize = OperandIs16<Size>(op);

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        uint16_t dest;
        if constexpr (Form == AddrModeSpec::ModReg) {
            dest = (uint16_t)gpr.GetReg(state, op->modrm & 7);
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }
        uint16_t src = (uint16_t)gpr.GetReg(state, reg);
        AluAnd<uint16_t>(state, flags_cache, dest, src);
    } else {
        uint32_t dest;
        if constexpr (Form == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, op->modrm & 7);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }
        uint32_t src = gpr.GetReg(state, reg);
        AluAnd<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmpxchg (0F B0/B1: CMPXCHG r/m, r)
// ============================================================================

template <typename T, AddrModeSpec Form, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpCmpxchg_T(LogicFuncParams) {
    const auto destination = ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
    if (!destination) return LogicFlow::RestartMemoryOp;
    const T accumulator = ReadRegGeneric<T>(state, gpr, EAX);
    const T source = ReadRegGeneric<T>(state, gpr, (op->modrm >> 3) & 7);
    const bool equal = accumulator == *destination;

    // The unequal case must still perform a write cycle. A failed write may
    // fault or invalidate the block, so publish flags and the accumulator last.
    if (!WriteModRM<T, OpOnTLBMiss::Blocking, 1, Form>(state, gpr, op, equal ? source : *destination, utlb))
        return LogicFlow::ExitOnCurrentEIP;
    AluSub<T, Flags != FlagsSpec::Dead>(state, flags_cache, accumulator, *destination);
    if (!equal) WriteRegGeneric<T>(state, gpr, EAX, *destination);
    return LogicFlow::Continue;
}

template <bool IsByte, OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmpxchg_Internal(LogicFuncParams) {
    if constexpr (IsByte) {
        return OpCmpxchg_T<uint8_t, Form, Flags>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op)) return OpCmpxchg_T<uint16_t, Form, Flags>(LogicPassParams);
        return OpCmpxchg_T<uint32_t, Form, Flags>(LogicPassParams);
    }
}

template <uint8_t Cond, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSetcc_Internal(LogicFuncParams) {
    const uint8_t val = CheckCondition(flags_cache, Cond) ? 1 : 0;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

enum class ByteCompareDestination { Rm, Register };

template <ByteCompareDestination Destination, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmp_Byte_Internal(LogicFuncParams) {
    const uint8_t reg = (op->modrm >> 3) & 7;
    if constexpr (Destination == ByteCompareDestination::Rm) {
        const auto dest = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
        if (!dest) return LogicFlow::RestartMemoryOp;
        AluSub<uint8_t, Flags != FlagsSpec::Dead>(state, flags_cache, *dest, gpr.GetReg8(state, reg));
    } else {
        const uint8_t dest = gpr.GetReg8(state, reg);
        const auto src = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
        if (!src) return LogicFlow::RestartMemoryOp;
        AluSub<uint8_t, Flags != FlagsSpec::Dead>(state, flags_cache, dest, *src);
    }
    return LogicFlow::Continue;
}

namespace op {

template <uint8_t FixedReg>
FORCE_INLINE LogicFlow OpCmp_EvGv_32_ModReg_SameReg_Internal(LogicFuncParams) {
    uint32_t value = gpr.GetReg(state, FixedReg);
    AluCmp<uint32_t>(state, flags_cache, value, value);
    return LogicFlow::Continue;
}

template <uint8_t FixedReg>
FORCE_INLINE LogicFlow OpTest_EvGv_32_ModReg_SameReg_Internal(LogicFuncParams) {
    uint32_t value = gpr.GetReg(state, FixedReg);
    AluAnd<uint32_t>(state, flags_cache, value, value);
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_COMPARE_IMPL_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_COMPARE_IMPL_0

// ============================================================================
// Non-size-specific functions (unchanged)
// ============================================================================

FORCE_INLINE LogicFlow OpCmp_EbGb(LogicFuncParams) {
    return OpCmp_Byte_Internal<ByteCompareDestination::Rm>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCmp_GbEb(LogicFuncParams) {
    return OpCmp_Byte_Internal<ByteCompareDestination::Register>(LogicPassParams);
}

#define FIBERCPU_BIND_OPS_COMPARE_IMPL_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_COMPARE_IMPL_1

#define FIBERCPU_BIND_INTEGER_COMPARE
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_INTEGER_COMPARE

}  // namespace op

}  // namespace fiberish
