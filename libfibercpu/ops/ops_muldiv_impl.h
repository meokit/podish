#pragma once
// Multiplication & Division
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_muldiv.h"

namespace fiberish {

template <typename T>
FORCE_INLINE LogicFlow OpImul_GvEv_T_Internal(LogicFuncParams) {
    // 0F AF: IMUL r16/32, r/m16/32
    // Dest: Register (Gv)
    // Src:  r/m (Ev)
    uint8_t reg = (op->modrm >> 3) & 7;

    T val1;
    if constexpr (sizeof(T) == 2)
        val1 = (T)GetReg(state, reg);
    else
        val1 = (T)GetReg(state, reg);

    T val2;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val2 = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val2 = *res;
    }

    if constexpr (sizeof(T) == 2) {
        int32_t res = (int32_t)(int16_t)val1 * (int32_t)(int16_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);

        if (res != (int32_t)(int16_t)res)
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        else
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    } else {
        int64_t res = (int64_t)(int32_t)val1 * (int64_t)(int32_t)val2;
        SetReg(state, reg, (uint32_t)res);

        if (res != (int64_t)(int32_t)res)
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        else
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
    return LogicFlow::Continue;
}

template <typename T, bool IsImm8>
FORCE_INLINE LogicFlow OpImul_GvEvI_T_Internal(LogicFuncParams) {
    // 69: IMUL r, r/m, imm16/32
    // 6B: IMUL r, r/m, imm8
    uint8_t reg = (op->modrm >> 3) & 7;

    T val1;  // r/m value
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val1 = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val1 = *res;
    }

    T val2;  // Immediate
    if constexpr (IsImm8) {
        val2 = (T)(int16_t)(int8_t)imm;  // Sign-extend imm8
    } else {
        val2 = (T)imm;
    }

    if constexpr (sizeof(T) == 2) {
        int32_t res = (int32_t)(int16_t)val1 * (int32_t)(int16_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);

        if (res != (int32_t)(int16_t)res)
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        else
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    } else {
        int64_t res = (int64_t)(int32_t)val1 * (int64_t)(int32_t)val2;
        SetReg(state, reg, (uint32_t)res);

        if (res != (int64_t)(int32_t)res)
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        else
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpImul_GvEv(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpImul_GvEv_T_Internal<uint16_t>(LogicPassParams);
    else
        return OpImul_GvEv_T_Internal<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpImul_GvEvIz(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpImul_GvEvI_T_Internal<uint16_t, false>(LogicPassParams);
    else
        return OpImul_GvEvI_T_Internal<uint32_t, false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpImul_GvEvIb(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpImul_GvEvI_T_Internal<uint16_t, true>(LogicPassParams);
    else
        return OpImul_GvEvI_T_Internal<uint32_t, true>(LogicPassParams);
}

}  // namespace op

// =========================================================================================
// Registration
// =========================================================================================

}  // namespace fiberish
