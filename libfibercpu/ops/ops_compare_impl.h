#pragma once
// Comparison & Test
// Auto-generated specialization refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_compare.h"

namespace fiberish {

// ============================================================================
// Specializations for OpCmp_EvGv (39: CMP r/m16/32, r16/32)
// ============================================================================

template <Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpCmp_EvGv_Internal(LogicFuncParams) {
    // 39: CMP r/m16/32, r16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluCmp<uint16_t>(state, flags_cache, dest, src);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluCmp<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmp_GvEv (3B: CMP r16/32, r/m16/32)
// ============================================================================

template <Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpCmp_GvEv_Internal(LogicFuncParams) {
    // 3B: CMP r16/32, r/m16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        uint16_t dest = (uint16_t)GetReg(state, reg);
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;
        AluCmp<uint16_t>(state, flags_cache, dest, src);
    } else {
        uint32_t dest = GetReg(state, reg);
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;
        AluCmp<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpTest_EvGv (85: TEST r/m16/32, r16/32)
// ============================================================================

template <Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpTest_EvGv_Internal(LogicFuncParams) {
    // 85: TEST r/m16/32, r16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluAnd<uint16_t>(state, flags_cache, dest, src);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluAnd<uint32_t>(state, flags_cache, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmpxchg (0F B0/B1: CMPXCHG r/m, r)
// ============================================================================

template <bool IsByte, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpCmpxchg_Internal(LogicFuncParams) {
    // 0F B0: CMPXCHG r/m8, r8
    // 0F B1: CMPXCHG r/m, r

    if constexpr (IsByte) {
        // Byte version: always 8-bit
        auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint8_t dest = *dest_res;
        uint8_t al = state->ctx.regs[EAX] & 0xFF;
        uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
        AluCmp<uint8_t>(state, flags_cache, al, dest);
        if (al == dest) {
            if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
        } else {
            state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFFFF00) | dest;
        }
    } else {
        bool opsize;
        if constexpr (S == Specialized::Opsize16) {
            opsize = true;
        } else if constexpr (S == Specialized::Opsize32) {
            opsize = false;
        } else {
            opsize = op->prefixes.flags.opsize;
        }

        if (opsize) {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint16_t dest = *dest_res;
            uint16_t ax = state->ctx.regs[EAX] & 0xFFFF;
            uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint16_t>(state, flags_cache, ax, dest);
            if (ax == dest) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
            } else {
                state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFF0000) | dest;
            }
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint32_t dest = *dest_res;
            uint32_t eax = state->ctx.regs[EAX];
            uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint32_t>(state, flags_cache, eax, dest);
            if (eax == dest) {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
            } else {
                state->ctx.regs[EAX] = dest;
            }
        }
    }
    return LogicFlow::Continue;
}

template <uint8_t Cond>
FORCE_INLINE LogicFlow OpSetcc_Internal(LogicFuncParams) {
    // 0F 9x: SETcc r/m8
    uint8_t val = CheckCondition(flags_cache, Cond) ? 1 : 0;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) {
        return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

namespace op {

template <bool JumpOnEqual, bool IsRel8>
FORCE_INLINE LogicFlow OpFusedCmpEvGvJcc_Internal(LogicFuncParams) {
    auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;

    const DecodedOp* jcc_op = NextOp(op);
    const uint8_t reg = (op->modrm >> 3) & 7;
    const uint32_t src = GetReg(state, reg);
    const uint32_t res = AluSub<uint32_t, true>(state, flags_cache, *dest_res, src);
    const bool equal = res == 0;
    const bool taken = JumpOnEqual ? equal : !equal;

    if (taken) {
        *branch = jcc_op->next_eip + (IsRel8 ? static_cast<int32_t>(static_cast<int8_t>(GetImm(jcc_op) & 0xFF))
                                             : static_cast<int32_t>(GetImm(jcc_op)));
        return LogicFlow::ExitToNextOpBranch;
    }
    return LogicFlow::ContinueSkipOne;
}

template <bool JumpOnEqual, bool IsRel8>
FORCE_INLINE LogicFlow OpFusedCmpGvEvJcc_Internal(LogicFuncParams) {
    const uint8_t reg = (op->modrm >> 3) & 7;
    const uint32_t dest = GetReg(state, reg);
    auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    const DecodedOp* jcc_op = NextOp(op);
    const uint32_t res = AluSub<uint32_t, true>(state, flags_cache, dest, *src_res);
    const bool equal = res == 0;
    const bool taken = JumpOnEqual ? equal : !equal;

    if (taken) {
        *branch = jcc_op->next_eip + (IsRel8 ? static_cast<int32_t>(static_cast<int8_t>(GetImm(jcc_op) & 0xFF))
                                             : static_cast<int32_t>(GetImm(jcc_op)));
        return LogicFlow::ExitToNextOpBranch;
    }
    return LogicFlow::ContinueSkipOne;
}

FORCE_INLINE LogicFlow OpCmp_EvGv_16(LogicFuncParams) {
    return OpCmp_EvGv_Internal<Specialized::Opsize16>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmp_EvGv_32(LogicFuncParams) {
    return OpCmp_EvGv_Internal<Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmp_EvGv(LogicFuncParams) { return OpCmp_EvGv_Internal<Specialized::None>(LogicPassParams); }
FORCE_INLINE LogicFlow OpFusedCmp_EvGv_JE_Rel8(LogicFuncParams) {
    return OpFusedCmpEvGvJcc_Internal<true, true>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_EvGv_JNE_Rel8(LogicFuncParams) {
    return OpFusedCmpEvGvJcc_Internal<false, true>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_EvGv_JE_Rel32(LogicFuncParams) {
    return OpFusedCmpEvGvJcc_Internal<true, false>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_EvGv_JNE_Rel32(LogicFuncParams) {
    return OpFusedCmpEvGvJcc_Internal<false, false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCmp_GvEv_16(LogicFuncParams) {
    return OpCmp_GvEv_Internal<Specialized::Opsize16>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmp_GvEv_32(LogicFuncParams) {
    return OpCmp_GvEv_Internal<Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmp_GvEv(LogicFuncParams) { return OpCmp_GvEv_Internal<Specialized::None>(LogicPassParams); }
FORCE_INLINE LogicFlow OpFusedCmp_GvEv_JE_Rel8(LogicFuncParams) {
    return OpFusedCmpGvEvJcc_Internal<true, true>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_GvEv_JNE_Rel8(LogicFuncParams) {
    return OpFusedCmpGvEvJcc_Internal<false, true>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_GvEv_JE_Rel32(LogicFuncParams) {
    return OpFusedCmpGvEvJcc_Internal<true, false>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpFusedCmp_GvEv_JNE_Rel32(LogicFuncParams) {
    return OpFusedCmpGvEvJcc_Internal<false, false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpTest_EvGv_16(LogicFuncParams) {
    return OpTest_EvGv_Internal<Specialized::Opsize16>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpTest_EvGv_32(LogicFuncParams) {
    return OpTest_EvGv_Internal<Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpTest_EvGv(LogicFuncParams) { return OpTest_EvGv_Internal<Specialized::None>(LogicPassParams); }

FORCE_INLINE LogicFlow OpCmpxchg_EvGv_16(LogicFuncParams) {
    return OpCmpxchg_Internal<false, Specialized::Opsize16>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmpxchg_EvGv_32(LogicFuncParams) {
    return OpCmpxchg_Internal<false, Specialized::Opsize32>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpCmpxchg_EvGv(LogicFuncParams) { return OpCmpxchg_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpCmpxchg_Byte(LogicFuncParams) { return OpCmpxchg_Internal<true>(LogicPassParams); }

// ============================================================================
// Non-size-specific functions (unchanged)
// ============================================================================

FORCE_INLINE LogicFlow OpCmp_EbGb(LogicFuncParams) {
    // 38: CMP r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluCmp<uint8_t>(state, flags_cache, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmp_GbEb(LogicFuncParams) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;
    AluCmp<uint8_t>(state, flags_cache, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSetcc_0(LogicFuncParams) { return OpSetcc_Internal<0>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_1(LogicFuncParams) { return OpSetcc_Internal<1>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_2(LogicFuncParams) { return OpSetcc_Internal<2>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_3(LogicFuncParams) { return OpSetcc_Internal<3>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_4(LogicFuncParams) { return OpSetcc_Internal<4>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_5(LogicFuncParams) { return OpSetcc_Internal<5>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_6(LogicFuncParams) { return OpSetcc_Internal<6>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_7(LogicFuncParams) { return OpSetcc_Internal<7>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_8(LogicFuncParams) { return OpSetcc_Internal<8>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_9(LogicFuncParams) { return OpSetcc_Internal<9>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_10(LogicFuncParams) { return OpSetcc_Internal<10>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_11(LogicFuncParams) { return OpSetcc_Internal<11>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_12(LogicFuncParams) { return OpSetcc_Internal<12>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_13(LogicFuncParams) { return OpSetcc_Internal<13>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_14(LogicFuncParams) { return OpSetcc_Internal<14>(LogicPassParams); }
FORCE_INLINE LogicFlow OpSetcc_15(LogicFuncParams) { return OpSetcc_Internal<15>(LogicPassParams); }

}  // namespace op

}  // namespace fiberish
