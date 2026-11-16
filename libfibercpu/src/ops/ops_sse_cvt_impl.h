#pragma once
// SSE/SSE2 Type Conversions
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include <cstring>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_sse_cvt.h"

namespace fiberish {
namespace op {

// Conversions must use the guest's RC/DAZ and explicitly accumulate guest flags.
// Host casts/SIMDe conversions otherwise inherit host rounding, can invoke C++
// out-of-range conversion UB, and never update Context::mxcsr. Work from IEEE bits
// so even an SNaN is classified without performing a host floating-point operation.
template <int FractionBits, int ExponentBits>
FORCE_INLINE uint32_t SseFloatBitsToInt32(uint64_t bits, uint32_t mxcsr, bool truncate, uint32_t& exceptions) {
    constexpr uint64_t fraction_mask = (uint64_t{1} << FractionBits) - 1;
    constexpr uint32_t exponent_mask = (uint32_t{1} << ExponentBits) - 1;
    constexpr int bias = (1 << (ExponentBits - 1)) - 1;
    const bool negative = ((bits >> (FractionBits + ExponentBits)) & 1) != 0;
    const uint32_t exponent = static_cast<uint32_t>(bits >> FractionBits) & exponent_mask;
    uint64_t significand = bits & fraction_mask;
    if (exponent == exponent_mask) {
        exceptions |= 1;  // IE: any NaN or infinity is invalid for integer conversion.
        return 0x80000000u;
    }
    if (exponent == 0 && (significand == 0 || (mxcsr & 0x40))) return 0;  // Zero or DAZ.
    const int power = (exponent == 0 ? 1 : static_cast<int>(exponent)) - bias;
    if (power > 31) {
        exceptions |= 1;
        return 0x80000000u;
    }
    if (exponent != 0) significand |= uint64_t{1} << FractionBits;
    uint64_t magnitude;
    bool inexact = false, above_half = false, exactly_half = false;
    if (power >= FractionBits) {
        magnitude = significand << (power - FractionBits);
    } else {
        const int shift = FractionBits - power;
        if (shift >= 64) {
            magnitude = 0;
            inexact = significand != 0;
        } else {
            magnitude = significand >> shift;
            const uint64_t remainder = significand & ((uint64_t{1} << shift) - 1);
            const uint64_t halfway = uint64_t{1} << (shift - 1);
            inexact = remainder != 0;
            above_half = remainder > halfway;
            exactly_half = remainder == halfway;
        }
    }
    const uint32_t rounding = truncate ? 3 : ((mxcsr >> 13) & 3);
    const bool increment = rounding == 0 ? (above_half || (exactly_half && (magnitude & 1)))
        : rounding == 1 ? (inexact && negative) : rounding == 2 ? (inexact && !negative) : false;
    magnitude += increment;
    if (magnitude > (negative ? 0x80000000ull : 0x7FFFFFFFull)) {
        // An invalid conversion does not additionally report precision for this lane.
        exceptions |= 1;
        return 0x80000000u;
    }
    if (inexact) exceptions |= 0x20;  // PE, including nonzero subnormals when DAZ is clear.
    const uint32_t result = static_cast<uint32_t>(magnitude);
    return negative ? 0u - result : result;
}

FORCE_INLINE uint32_t SseInt32ToSingleBits(uint32_t bits, uint32_t mxcsr, uint32_t& exceptions) {
    const bool negative = (bits >> 31) != 0;
    const uint32_t magnitude = negative ? 0u - bits : bits;
    if (magnitude == 0) return 0;
    int power = 0;
    for (uint32_t scan = magnitude; scan > 1; scan >>= 1) ++power;
    uint32_t significand;
    if (power <= 23) {
        significand = magnitude << (23 - power);
    } else {
        const int shift = power - 23;
        significand = magnitude >> shift;
        const uint32_t remainder = magnitude & ((1u << shift) - 1);
        const uint32_t halfway = 1u << (shift - 1);
        const uint32_t rounding = (mxcsr >> 13) & 3;
        const bool increment = rounding == 0 ? (remainder > halfway || (remainder == halfway && (significand & 1)))
            : rounding == 1 ? (remainder != 0 && negative) : rounding == 2 ? (remainder != 0 && !negative) : false;
        if (remainder != 0) exceptions |= 0x20;
        significand += increment;
        if (significand == (1u << 24)) {
            significand >>= 1;
            ++power;
        }
    }
    return (negative ? 0x80000000u : 0) | (static_cast<uint32_t>(power + 127) << 23) | (significand & 0x7FFFFF);
}

FORCE_INLINE bool SseCommitConversionExceptions(EmuState* state, uint32_t exceptions) {
    const uint32_t masks = (state->ctx.mxcsr >> 7) & 0x3F;
    // Unmasked invalid has priority over precision from other packed lanes.
    if ((exceptions & 1) && !(masks & 1)) exceptions &= ~0x20u;
    state->ctx.mxcsr |= exceptions;
    if ((exceptions & ~masks) == 0) return true;
    state->fault_vector = 19;  // #XM: do not commit any destination lane/register.
    if (!state->hooks.on_interrupt(state, 19)) state->status = EmuStatus::Fault;
    return false;
}

FORCE_INLINE LogicFlow OpCvt_2A(LogicFuncParams) {
    // 0F 2A: CVTPI2PS (MMX) / CVTSI2SS (F3) / CVTSI2SD (F2)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    int32_t val = (int32_t)*val_res;

    if (op->prefixes.flags.repne) {
        // F2: CVTSI2SD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtsi32_sd(dest_pd, val);
        *dest_ptr = simde_mm_castpd_ps(res);
    } else if (op->prefixes.flags.rep) {
        // F3: CVTSI2SS
        uint32_t exceptions = 0;
        const uint32_t result = SseInt32ToSingleBits(*val_res, state->ctx.mxcsr, exceptions);
        if (!SseCommitConversionExceptions(state, exceptions)) return LogicFlow::ExitOnCurrentEIP;
        std::memcpy(dest_ptr, &result, sizeof(result));
    } else {
        state->fault_vector = 6;
        if (!state->hooks.on_invalid_opcode(state)) {
            state->status = EmuStatus::Fault;
        }
        return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

template <bool Truncate>
FORCE_INLINE LogicFlow OpCvtScalarToInt32(LogicFuncParams) {
    uint32_t addr = 0;
    if (op->modrm < 0xC0) addr = ComputeLinearAddress(state, op);
    uint32_t result = 0, exceptions = 0;
    if (op->prefixes.flags.repne) {  // F2: CVT[T]SD2SI
        uint64_t bits;
        if (op->modrm >= 0xC0) {
            std::memcpy(&bits, &state->ctx.xmm[op->modrm & 7], sizeof(bits));
        } else {
            auto value = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!value) return LogicFlow::RestartMemoryOp;
            bits = *value;
        }
        result = SseFloatBitsToInt32<52, 11>(bits, state->ctx.mxcsr, Truncate, exceptions);
    } else if (op->prefixes.flags.rep) {  // F3: CVT[T]SS2SI
        uint32_t bits;
        if (op->modrm >= 0xC0) {
            std::memcpy(&bits, &state->ctx.xmm[op->modrm & 7], sizeof(bits));
        } else {
            auto value = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!value) return LogicFlow::RestartMemoryOp;
            bits = *value;
        }
        result = SseFloatBitsToInt32<23, 8>(bits, state->ctx.mxcsr, Truncate, exceptions);
    } else {
        state->fault_vector = 6;
        if (!state->hooks.on_invalid_opcode(state)) {
            state->status = EmuStatus::Fault;
        }
        return LogicFlow::ExitOnCurrentEIP;
    }
    if (!SseCommitConversionExceptions(state, exceptions)) return LogicFlow::ExitOnCurrentEIP;
    SetReg(state, (op->modrm >> 3) & 7, result);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCvt_2C(LogicFuncParams) {
    return OpCvtScalarToInt32<true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCvt_2D(LogicFuncParams) {
    return OpCvtScalarToInt32<false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCvt_5A(LogicFuncParams) {
    // 0F 5A: CVTPS2PD / CVTPD2PS (66) / CVTSD2SS (F2) / CVTSS2SD (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (op->prefixes.flags.opsize) {
        // 66: CVTPD2PS (xmm/m128 -> xmm)
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128d src_pd = simde_mm_castps_pd(*src_res);
        *dest_ptr = simde_mm_cvtpd_ps(src_pd);

    } else if (op->prefixes.flags.repne) {
        // F2: CVTSD2SS (xmm/m64 -> xmm)
        simde__m128d src_pd;
        if ((op->modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src_pd = simde_mm_set_sd(*(double*)&(*val_res));
        }
        *dest_ptr = simde_mm_cvtsd_ss(*dest_ptr, src_pd);

    } else if (op->prefixes.flags.rep) {
        // F3: CVTSS2SD (xmm/m32 -> xmm)
        simde__m128 src_ss;
        if ((op->modrm >> 6) == 3) {
            src_ss = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src_ss = simde_mm_set_ss(*(float*)&(*val_res));
        }

        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtss_sd(dest_pd, src_ss);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else {
        // 0F: CVTPS2PD (xmm/m64 -> xmm)
        simde__m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            uint64_t val = *val_res;
            uint32_t v0 = (uint32_t)val;
            uint32_t v1 = (uint32_t)(val >> 32);
            src = simde_mm_set_ps(0.0f, 0.0f, *(float*)&v1, *(float*)&v0);
        }
        simde__m128d res = simde_mm_cvtps_pd(src);
        *dest_ptr = simde_mm_castpd_ps(res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCvt_5B(LogicFuncParams) {
    // 0F 5B: CVTDQ2PS / CVTPS2DQ (66) / CVTTPS2DQ (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128 src = *src_res;

    uint32_t input[4], result[4], exceptions = 0;
    std::memcpy(input, &src, sizeof(input));
    if (op->prefixes.flags.opsize || op->prefixes.flags.rep) {
        // 66: CVTPS2DQ; F3: CVTTPS2DQ. Accumulate before committing any lane.
        for (int lane = 0; lane < 4; ++lane)
            result[lane] = SseFloatBitsToInt32<23, 8>(input[lane], state->ctx.mxcsr,
                !op->prefixes.flags.opsize, exceptions);
    } else {
        // 0F: CVTDQ2PS
        for (int lane = 0; lane < 4; ++lane)
            result[lane] = SseInt32ToSingleBits(input[lane], state->ctx.mxcsr, exceptions);
    }
    if (!SseCommitConversionExceptions(state, exceptions)) return LogicFlow::ExitOnCurrentEIP;
    std::memcpy(dest_ptr, result, sizeof(result));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCvt_E6(LogicFuncParams) {
    // 0F E6: CVTPD2DQ (F2) / CVTDQ2PD (F3) / CVTTPD2DQ (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (op->prefixes.flags.opsize) {
        // 66: CVTTPD2DQ (Truncate xmm/m128 -> xmm)
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint64_t input[2];
        uint32_t result[4] = {}, exceptions = 0;
        std::memcpy(input, &*src_res, sizeof(input));
        for (int lane = 0; lane < 2; ++lane)
            result[lane] = SseFloatBitsToInt32<52, 11>(input[lane], state->ctx.mxcsr, true, exceptions);
        if (!SseCommitConversionExceptions(state, exceptions)) return LogicFlow::ExitOnCurrentEIP;
        std::memcpy(dest_ptr, result, sizeof(result));

    } else if (op->prefixes.flags.rep) {
        // F3: CVTDQ2PD (xmm/m64 -> xmm)
        simde__m128i isrc;
        if ((op->modrm >> 6) == 3) {
            // Register: cast float to int
            simde__m128 src = state->ctx.xmm[op->modrm & 7];
            isrc = simde_mm_castps_si128(src);
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            uint64_t val = *val_res;
            uint32_t v0 = (uint32_t)val;
            uint32_t v1 = (uint32_t)(val >> 32);
            isrc = simde_mm_set_epi32(0, 0, v1, v0);
        }

        simde__m128d res = simde_mm_cvtepi32_pd(isrc);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (op->prefixes.flags.repne) {
        // F2: CVTPD2DQ (xmm/m128 -> xmm)
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint64_t input[2];
        uint32_t result[4] = {}, exceptions = 0;
        std::memcpy(input, &*src_res, sizeof(input));
        for (int lane = 0; lane < 2; ++lane)
            result[lane] = SseFloatBitsToInt32<52, 11>(input[lane], state->ctx.mxcsr, false, exceptions);
        if (!SseCommitConversionExceptions(state, exceptions)) return LogicFlow::ExitOnCurrentEIP;
        std::memcpy(dest_ptr, result, sizeof(result));
    } else {
        state->fault_vector = 6;
        if (!state->hooks.on_invalid_opcode(state)) {
            state->status = EmuStatus::Fault;
        }
        return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
