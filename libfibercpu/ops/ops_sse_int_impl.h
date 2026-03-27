#pragma once
// SSE2 Integer Operations
// Auto-generated from ops.cpp refactoring

#include "ops_sse_int.h"

#include <simde/x86/sse2.h>

#include <cstring>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

namespace op {

FORCE_INLINE void DecodeShuffleControl(uint8_t imm8, uint8_t selectors[4]) {
    selectors[0] = (imm8 >> 0) & 0x3;
    selectors[1] = (imm8 >> 2) & 0x3;
    selectors[2] = (imm8 >> 4) & 0x3;
    selectors[3] = (imm8 >> 6) & 0x3;
}

// Shifts
template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsllw_Sse_Internal(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128i count;
    if constexpr (!IsGroup) {  // PSLLW xmm, xmm/m128
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    } else {  // Group 0F 71 /6
        count = simde_mm_set_epi64x(0, imm);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sll_epi16(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPslld_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F F2
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sll_epi32(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsllq_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 14 /6
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F F3
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sll_epi64(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsraw_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 12 /4
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F E1
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sra_epi16(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsrad_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13 /4
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F E2
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sra_epi32(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsrlw_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 12 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F D1
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi16(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsrld_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F D2
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi32(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
FORCE_INLINE LogicFlow OpPsrlq_Sse_Internal(LogicFuncParams) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 14 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, imm);
    } else {  // 0F D3
        dst_idx = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi64(dst, count));
    return LogicFlow::Continue;
}

// Logical
FORCE_INLINE LogicFlow OpPand_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_and_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPandn_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_andnot_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPor_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_or_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPxor_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_xor_si128(dst, src));
    return LogicFlow::Continue;
}

// Arithmetic
FORCE_INLINE LogicFlow OpPaddb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmuludq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mul_epu32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmaddwd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_madd_epi16(dst, src));
    return LogicFlow::Continue;
}

// Saturated Arithmetic
FORCE_INLINE LogicFlow OpPaddusb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddusw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epu16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddsb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddsw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubusb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubusw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epu16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubsb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubsw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epi16(dst, src));
    return LogicFlow::Continue;
}

// Multiplications
FORCE_INLINE LogicFlow OpPmullw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mullo_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmulhw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mulhi_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmulhuw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mulhi_epu16(dst, src));
    return LogicFlow::Continue;
}

// Average
FORCE_INLINE LogicFlow OpPavgb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_avg_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPavgw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_avg_epu16(dst, src));
    return LogicFlow::Continue;
}

// Sum of Absolute Differences
FORCE_INLINE LogicFlow OpPsadbw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sad_epu8(dst, src));
    return LogicFlow::Continue;
}

// Comparison
FORCE_INLINE LogicFlow OpPcmpeqb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpeqw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpeqd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi32(dst, src));
    return LogicFlow::Continue;
}

// Max/Min
FORCE_INLINE LogicFlow OpPmaxub_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPminub_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmaxsw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPminsw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsllw_Sse(LogicFuncParams) { return OpPsllw_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPslld_Sse(LogicFuncParams) { return OpPslld_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsllq_Sse(LogicFuncParams) { return OpPsllq_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsraw_Sse(LogicFuncParams) { return OpPsraw_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrad_Sse(LogicFuncParams) { return OpPsrad_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrlw_Sse(LogicFuncParams) { return OpPsrlw_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrld_Sse(LogicFuncParams) { return OpPsrld_Sse_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrlq_Sse(LogicFuncParams) { return OpPsrlq_Sse_Internal<false>(LogicPassParams); }

FORCE_INLINE LogicFlow OpPsllw_Sse_Group(LogicFuncParams) { return OpPsllw_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPslld_Sse_Group(LogicFuncParams) { return OpPslld_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsllq_Sse_Group(LogicFuncParams) { return OpPsllq_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsraw_Sse_Group(LogicFuncParams) { return OpPsraw_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrad_Sse_Group(LogicFuncParams) { return OpPsrad_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrlw_Sse_Group(LogicFuncParams) { return OpPsrlw_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrld_Sse_Group(LogicFuncParams) { return OpPsrld_Sse_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpPsrlq_Sse_Group(LogicFuncParams) { return OpPsrlq_Sse_Internal<true>(LogicPassParams); }

FORCE_INLINE LogicFlow OpPslldq_Sse(LogicFuncParams) {
    // 0F 73 /7: PSLLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    simde__m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm8 = static_cast<uint8_t>(imm);

    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);

    if (imm8 >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift left (move to higher index)
        for (int i = 15; i >= 0; --i) {
            if (i >= imm8)
                bytes[i] = bytes[i - imm8];
            else
                bytes[i] = 0;
        }
    }
    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsrldq_Sse(LogicFuncParams) {
    // 0F 73 /3: PSRLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    simde__m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm8 = static_cast<uint8_t>(imm);

    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);

    if (imm8 >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift right (move to lower index)
        for (int i = 0; i < 16; ++i) {
            if (i + imm8 < 16)
                bytes[i] = bytes[i + imm8];
            else
                bytes[i] = 0;
        }
    }

    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
    return LogicFlow::Continue;
}

// Shuffle/Pack/Unpack
FORCE_INLINE LogicFlow OpPshufd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm_u8 = (uint8_t)imm;
    uint8_t selectors[4];
    DecodeShuffleControl(imm_u8, selectors);

    int32_t src_lanes[4];
    std::memcpy(src_lanes, &src, sizeof(src_lanes));

    int32_t res[4];
    res[0] = src_lanes[selectors[0]];
    res[1] = src_lanes[selectors[1]];
    res[2] = src_lanes[selectors[2]];
    res[3] = src_lanes[selectors[3]];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_setr_epi32(res[0], res[1], res[2], res[3]));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPshufhw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm_u8 = (uint8_t)imm;
    uint8_t selectors[4];
    DecodeShuffleControl(imm_u8, selectors);

    int16_t src_words[8];
    std::memcpy(src_words, &src, sizeof(src_words));
    int16_t res[8];
    // Low words (0-3) copied
    res[0] = src_words[0];
    res[1] = src_words[1];
    res[2] = src_words[2];
    res[3] = src_words[3];
    // High words (4-7) shuffled
    res[4] = src_words[4 + selectors[0]];
    res[5] = src_words[4 + selectors[1]];
    res[6] = src_words[4 + selectors[2]];
    res[7] = src_words[4 + selectors[3]];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const simde__m128i*)res));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPshuflw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm_u8 = (uint8_t)imm;
    uint8_t selectors[4];
    DecodeShuffleControl(imm_u8, selectors);

    int16_t src_words[8];
    std::memcpy(src_words, &src, sizeof(src_words));
    int16_t res[8];
    // Low words (0-3) shuffled
    res[0] = src_words[selectors[0]];
    res[1] = src_words[selectors[1]];
    res[2] = src_words[selectors[2]];
    res[3] = src_words[selectors[3]];
    // High words (4-7) copied
    res[4] = src_words[4];
    res[5] = src_words[5];
    res[6] = src_words[6];
    res[7] = src_words[7];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const simde__m128i*)res));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhbw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhwd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhdq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhqdq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklbw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklwd_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckldq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklqdq_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPacksswb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPackssdw_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPackuswb_Sse(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packus_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPextrw_Sse(LogicFuncParams) {
    // PEXTRW reg32, xmm, imm8 (0F C5)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);
    uint8_t imm_u8 = (uint8_t)imm & 7;

    uint16_t val = 0;
    switch (imm_u8) {
        case 0:
            val = simde_mm_extract_epi16(src, 0);
            break;
        case 1:
            val = simde_mm_extract_epi16(src, 1);
            break;
        case 2:
            val = simde_mm_extract_epi16(src, 2);
            break;
        case 3:
            val = simde_mm_extract_epi16(src, 3);
            break;
        case 4:
            val = simde_mm_extract_epi16(src, 4);
            break;
        case 5:
            val = simde_mm_extract_epi16(src, 5);
            break;
        case 6:
            val = simde_mm_extract_epi16(src, 6);
            break;
        case 7:
            val = simde_mm_extract_epi16(src, 7);
            break;
    }
    state->ctx.regs[reg] = (uint32_t)val;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPinsrw_Sse(LogicFuncParams) {
    // PINSRW xmm, r32/m16, imm8 (0F C4)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t imm_u8 = (uint8_t)imm & 7;

    int val;  // simde expects int
    if (op->modrm >= 0xC0) {
        val = (int)(uint16_t)state->ctx.regs[op->modrm & 7];
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        val = (int)*val_res;
    }

    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    switch (imm_u8) {
        case 0:
            dst = simde_mm_insert_epi16(dst, val, 0);
            break;
        case 1:
            dst = simde_mm_insert_epi16(dst, val, 1);
            break;
        case 2:
            dst = simde_mm_insert_epi16(dst, val, 2);
            break;
        case 3:
            dst = simde_mm_insert_epi16(dst, val, 3);
            break;
        case 4:
            dst = simde_mm_insert_epi16(dst, val, 4);
            break;
        case 5:
            dst = simde_mm_insert_epi16(dst, val, 5);
            break;
        case 6:
            dst = simde_mm_insert_epi16(dst, val, 6);
            break;
        case 7:
            dst = simde_mm_insert_epi16(dst, val, 7);
            break;
    }
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(dst);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmovmskb_Sse(LogicFuncParams) {
    // PMOVMSKB reg32, xmm (0F D7)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);

    int mask = simde_mm_movemask_epi8(src);
    state->ctx.regs[reg] = (uint32_t)mask;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup_Pshuf(LogicFuncParams) {
    // 0F 70
    if (op->prefixes.flags.opsize) {  // 66: PSHUFD
        return OpPshufd_Sse(LogicPassParams);
    } else if (op->prefixes.flags.repne) {  // F2: PSHUFLW
        return OpPshuflw_Sse(LogicPassParams);
    } else if (op->prefixes.flags.rep) {  // F3: PSHUFHW
        return OpPshufhw_Sse(LogicPassParams);
    } else {
        // PSHUFW (MMX) - Not implemented
        // OpUd2(state, op);
        if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        return LogicFlow::Continue;
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_W(LogicFuncParams) {
    // 0F 71 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrlw_Sse_Group(LogicPassParams);
        case 4:
            return OpPsraw_Sse_Group(LogicPassParams);
        case 6:
            return OpPsllw_Sse_Group(LogicPassParams);
        default:
            return LogicFlow::Continue;  // #UD
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_D(LogicFuncParams) {
    // 0F 72 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrld_Sse_Group(LogicPassParams);
        case 4:
            return OpPsrad_Sse_Group(LogicPassParams);
        case 6:
            return OpPslld_Sse_Group(LogicPassParams);
        default:
            return LogicFlow::Continue;
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_Q(LogicFuncParams) {
    // 0F 73 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrlq_Sse_Group(LogicPassParams);
        case 3:
            return OpPsrldq_Sse(LogicPassParams);
        case 6:
            return OpPsllq_Sse_Group(LogicPassParams);
        case 7:
            return OpPslldq_Sse(LogicPassParams);
        default:
            return LogicFlow::Continue;
    }
}

}  // namespace op

}  // namespace fiberish
