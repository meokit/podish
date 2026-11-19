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
    return ReadModRM<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
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
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
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
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
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
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
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
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
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

template <bool IsByte, OpsizeSpec Size = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmpxchg_Internal(LogicFuncParams) {
    // 0F B0: CMPXCHG r/m8, r8
    // 0F B1: CMPXCHG r/m, r

    if constexpr (IsByte) {
        // Byte version: always 8-bit
        auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint8_t dest = *dest_res;
        uint8_t al = gpr.GetReg(state, EAX) & 0xFF;
        uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
        // CMPXCHG performs a write cycle even on an unequal comparison. Do not
        // commit flags/accumulator until that cycle succeeds (including SMC).
        if (!WriteModRM<uint8_t, OpOnTLBMiss::Blocking>(state, gpr, op, al == dest ? src : dest, utlb))
            return LogicFlow::ExitOnCurrentEIP;
        AluCmp<uint8_t>(state, flags_cache, al, dest);
        if (al != dest) {
            gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFFFF00) | dest);
        }
    } else {
        const bool opsize = OperandIs16<Size>(op);

        if (opsize) {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint16_t dest = *dest_res;
            uint16_t ax = gpr.GetReg(state, EAX) & 0xFFFF;
            uint16_t src = (uint16_t)gpr.GetReg(state, (op->modrm >> 3) & 7);
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, op, ax == dest ? src : dest, utlb))
                return LogicFlow::ExitOnCurrentEIP;
            AluCmp<uint16_t>(state, flags_cache, ax, dest);
            if (ax != dest) {
                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | dest);
            }
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint32_t dest = *dest_res;
            uint32_t eax = gpr.GetReg(state, EAX);
            uint32_t src = gpr.GetReg(state, (op->modrm >> 3) & 7);
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, op, eax == dest ? src : dest, utlb))
                return LogicFlow::ExitOnCurrentEIP;
            AluCmp<uint32_t>(state, flags_cache, eax, dest);
            if (eax != dest) {
                gpr.SetReg(state, EAX, dest);
            }
        }
    }
    return LogicFlow::Continue;
}

template <uint8_t Cond>
FORCE_INLINE LogicFlow OpSetcc_Internal(LogicFuncParams) {
    // 0F 9x: SETcc r/m8
    uint8_t val = CheckCondition(flags_cache, Cond) ? 1 : 0;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, gpr, op, val, utlb)) {
        return LogicFlow::RetryMemoryOp;
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
    // 38: CMP r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;
    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    AluCmp<uint8_t>(state, flags_cache, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmp_GbEb(LogicFuncParams) {
    // 3A: CMP r8, r/m8
    uint8_t dest = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;
    AluCmp<uint8_t>(state, flags_cache, dest, src);
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_COMPARE_IMPL_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_COMPARE_IMPL_1

}  // namespace op

}  // namespace fiberish
