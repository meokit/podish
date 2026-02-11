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

// Shifts
template <bool IsGroup>
static FORCE_INLINE LogicFlow OpPsllw_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128i count;
    if constexpr (!IsGroup) {  // PSLLW xmm, xmm/m128
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        count = simde_mm_castps_si128(*src_res);
    } else {  // Group 0F 71 /6
        count = simde_mm_set_epi64x(0, op->imm);
    }
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sll_epi16(dst, count));
    return LogicFlow::Continue;
}

template <bool IsGroup>
static FORCE_INLINE LogicFlow OpPslld_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsllq_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 14 /6
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsraw_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 12 /4
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsrad_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13 /4
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsrlw_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 12 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsrld_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 13 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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
static FORCE_INLINE LogicFlow OpPsrlq_Sse_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t dst_idx;
    simde__m128i count;
    if constexpr (IsGroup) {  // Group 14 /2
        dst_idx = op->modrm & 7;
        count = simde_mm_set_epi64x(0, op->imm);
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

namespace op {

// Logical
FORCE_INLINE LogicFlow OpPand_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_and_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPandn_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_andnot_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPor_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_or_si128(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPxor_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_xor_si128(dst, src));
    return LogicFlow::Continue;
}

// Arithmetic
FORCE_INLINE LogicFlow OpPaddb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmuludq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mul_epu32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmaddwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_madd_epi16(dst, src));
    return LogicFlow::Continue;
}

// Saturated Arithmetic
FORCE_INLINE LogicFlow OpPaddusb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddusw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epu16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddsb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPaddsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_adds_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubusb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubusw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epu16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubsb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsubsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_subs_epi16(dst, src));
    return LogicFlow::Continue;
}

// Multiplications
FORCE_INLINE LogicFlow OpPmullw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mullo_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmulhw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mulhi_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmulhuw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mulhi_epu16(dst, src));
    return LogicFlow::Continue;
}

// Average
FORCE_INLINE LogicFlow OpPavgb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_avg_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPavgw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_avg_epu16(dst, src));
    return LogicFlow::Continue;
}

// Sum of Absolute Differences
FORCE_INLINE LogicFlow OpPsadbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sad_epu8(dst, src));
    return LogicFlow::Continue;
}

// Comparison
FORCE_INLINE LogicFlow OpPcmpeqb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpeqw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpeqd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPcmpgtd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi32(dst, src));
    return LogicFlow::Continue;
}

// Max/Min
FORCE_INLINE LogicFlow OpPmaxub_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPminub_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epu8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPmaxsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPminsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsllw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsllw_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPslld_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPslld_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsllq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsllq_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsraw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsraw_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrad_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrad_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrlw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrlw_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrld_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrld_Sse_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrlq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrlq_Sse_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpPsllw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsllw_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPslld_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPslld_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsllq_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsllq_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsraw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsraw_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrad_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrad_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrlw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrlw_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrld_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrld_Sse_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpPsrlq_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpPsrlq_Sse_Internal<true>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpPslldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 73 /7: PSLLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    simde__m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm = (uint8_t)op->imm;

    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);

    if (imm >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift left (move to higher index)
        for (int i = 15; i >= 0; --i) {
            if (i >= imm)
                bytes[i] = bytes[i - imm];
            else
                bytes[i] = 0;
        }
    }
    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPsrldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 73 /3: PSRLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    simde__m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm = (uint8_t)op->imm;

    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);

    if (imm >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift right (move to lower index)
        for (int i = 0; i < 16; ++i) {
            if (i + imm < 16)
                bytes[i] = bytes[i + imm];
            else
                bytes[i] = 0;
        }
    }

    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
    return LogicFlow::Continue;
}

// Shuffle/Pack/Unpack
FORCE_INLINE LogicFlow OpPshufd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm = (uint8_t)op->imm;

    int32_t* s = (int32_t*)&src;
    int32_t res[4];
    res[0] = s[(imm >> 0) & 3];
    res[1] = s[(imm >> 2) & 3];
    res[2] = s[(imm >> 4) & 3];
    res[3] = s[(imm >> 6) & 3];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_setr_epi32(res[0], res[1], res[2], res[3]));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPshufhw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm = (uint8_t)op->imm;

    int16_t* s = (int16_t*)&src;
    int16_t res[8];
    // Low words (0-3) copied
    res[0] = s[0];
    res[1] = s[1];
    res[2] = s[2];
    res[3] = s[3];
    // High words (4-7) shuffled
    res[4] = s[4 + ((imm >> 0) & 3)];
    res[5] = s[4 + ((imm >> 2) & 3)];
    res[6] = s[4 + ((imm >> 4) & 3)];
    res[7] = s[4 + ((imm >> 6) & 3)];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const simde__m128i*)res));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPshuflw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i src = simde_mm_castps_si128(*src_res);
    uint8_t imm = (uint8_t)op->imm;

    int16_t* s = (int16_t*)&src;
    int16_t res[8];
    // Low words (0-3) shuffled
    res[0] = s[((imm >> 0) & 3)];
    res[1] = s[((imm >> 2) & 3)];
    res[2] = s[((imm >> 4) & 3)];
    res[3] = s[((imm >> 6) & 3)];
    // High words (4-7) copied
    res[4] = s[4];
    res[5] = s[5];
    res[6] = s[6];
    res[7] = s[7];

    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const simde__m128i*)res));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckhqdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi8(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpckldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPunpcklqdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi64(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPacksswb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPackssdw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi32(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPackuswb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t reg = (op->modrm >> 3) & 7;
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    simde__m128i src = simde_mm_castps_si128(*src_res);
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packus_epi16(dst, src));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPextrw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PEXTRW reg32, xmm, imm8 (0F C5)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);
    uint8_t imm = (uint8_t)op->imm & 7;

    uint16_t val = 0;
    switch (imm) {
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

FORCE_INLINE LogicFlow OpPinsrw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PINSRW xmm, r32/m16, imm8 (0F C4)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t imm = (uint8_t)op->imm & 7;

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
    switch (imm) {
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

FORCE_INLINE LogicFlow OpPmovmskb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PMOVMSKB reg32, xmm (0F D7)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);

    int mask = simde_mm_movemask_epi8(src);
    state->ctx.regs[reg] = (uint32_t)mask;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup_Pshuf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 70
    if (op->prefixes.flags.opsize) {  // 66: PSHUFD
        return OpPshufd_Sse(state, op, utlb);
    } else if (op->prefixes.flags.repne) {  // F2: PSHUFLW
        return OpPshuflw_Sse(state, op, utlb);
    } else if (op->prefixes.flags.rep) {  // F3: PSHUFHW
        return OpPshufhw_Sse(state, op, utlb);
    } else {
        // PSHUFW (MMX) - Not implemented
        // OpUd2(state, op);
        if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        return LogicFlow::Continue;
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_W(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 71 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrlw_Sse_Group(state, op, utlb);
        case 4:
            return OpPsraw_Sse_Group(state, op, utlb);
        case 6:
            return OpPsllw_Sse_Group(state, op, utlb);
        default:
            return LogicFlow::Continue;  // #UD
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_D(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 72 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrld_Sse_Group(state, op, utlb);
        case 4:
            return OpPsrad_Sse_Group(state, op, utlb);
        case 6:
            return OpPslld_Sse_Group(state, op, utlb);
        default:
            return LogicFlow::Continue;
    }
}

FORCE_INLINE LogicFlow OpGroup_Sse_Shift_Imm_Q(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 73 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2:
            return OpPsrlq_Sse_Group(state, op, utlb);
        case 3:
            return OpPsrldq_Sse(state, op, utlb);
        case 6:
            return OpPsllq_Sse_Group(state, op, utlb);
        case 7:
            return OpPslldq_Sse(state, op, utlb);
        default:
            return LogicFlow::Continue;
    }
}

}  // namespace op

void RegisterSseIntOps() {
    using namespace op;
    g_Handlers[0x1DB] = DispatchWrapper<OpPand_Sse>;
    g_Handlers[0x1DF] = DispatchWrapper<OpPandn_Sse>;
    g_Handlers[0x1EB] = DispatchWrapper<OpPor_Sse>;
    g_Handlers[0x1EF] = DispatchWrapper<OpPxor_Sse>;
    g_Handlers[0x1FC] = DispatchWrapper<OpPaddb_Sse>;
    g_Handlers[0x1FD] = DispatchWrapper<OpPaddw_Sse>;
    g_Handlers[0x1FE] = DispatchWrapper<OpPaddd_Sse>;
    g_Handlers[0x1D4] = DispatchWrapper<OpPaddq_Sse>;
    g_Handlers[0x1F8] = DispatchWrapper<OpPsubb_Sse>;
    g_Handlers[0x1F9] = DispatchWrapper<OpPsubw_Sse>;
    g_Handlers[0x1FA] = DispatchWrapper<OpPsubd_Sse>;
    g_Handlers[0x1FB] = DispatchWrapper<OpPsubq_Sse>;
    g_Handlers[0x1F4] = DispatchWrapper<OpPmuludq_Sse>;
    g_Handlers[0x1F5] = DispatchWrapper<OpPmaddwd_Sse>;
    g_Handlers[0x174] = DispatchWrapper<OpPcmpeqb_Sse>;
    g_Handlers[0x175] = DispatchWrapper<OpPcmpeqw_Sse>;
    g_Handlers[0x176] = DispatchWrapper<OpPcmpeqd_Sse>;
    g_Handlers[0x164] = DispatchWrapper<OpPcmpgtb_Sse>;
    g_Handlers[0x165] = DispatchWrapper<OpPcmpgtw_Sse>;
    g_Handlers[0x166] = DispatchWrapper<OpPcmpgtd_Sse>;
    g_Handlers[0x1DE] = DispatchWrapper<OpPmaxub_Sse>;
    g_Handlers[0x1DA] = DispatchWrapper<OpPminub_Sse>;
    g_Handlers[0x1EE] = DispatchWrapper<OpPmaxsw_Sse>;
    g_Handlers[0x1EA] = DispatchWrapper<OpPminsw_Sse>;
    g_Handlers[0x1F1] = DispatchWrapper<OpPsllw_Sse>;
    g_Handlers[0x1F2] = DispatchWrapper<OpPslld_Sse>;
    g_Handlers[0x1F3] = DispatchWrapper<OpPsllq_Sse>;
    g_Handlers[0x1E1] = DispatchWrapper<OpPsraw_Sse>;
    g_Handlers[0x1E2] = DispatchWrapper<OpPsrad_Sse>;
    g_Handlers[0x1D1] = DispatchWrapper<OpPsrlw_Sse>;
    g_Handlers[0x1D2] = DispatchWrapper<OpPsrld_Sse>;
    g_Handlers[0x1D3] = DispatchWrapper<OpPsrlq_Sse>;
    g_Handlers[0x170] = DispatchWrapper<OpGroup_Pshuf>;
    g_Handlers[0x171] = DispatchWrapper<OpGroup_Sse_Shift_Imm_W>;
    g_Handlers[0x172] = DispatchWrapper<OpGroup_Sse_Shift_Imm_D>;
    g_Handlers[0x173] = DispatchWrapper<OpGroup_Sse_Shift_Imm_Q>;
    g_Handlers[0x168] = DispatchWrapper<OpPunpckhbw_Sse>;
    g_Handlers[0x169] = DispatchWrapper<OpPunpckhwd_Sse>;
    g_Handlers[0x16A] = DispatchWrapper<OpPunpckhdq_Sse>;
    g_Handlers[0x16D] = DispatchWrapper<OpPunpckhqdq_Sse>;
    g_Handlers[0x160] = DispatchWrapper<OpPunpcklbw_Sse>;
    g_Handlers[0x161] = DispatchWrapper<OpPunpcklwd_Sse>;
    g_Handlers[0x162] = DispatchWrapper<OpPunpckldq_Sse>;
    g_Handlers[0x16C] = DispatchWrapper<OpPunpcklqdq_Sse>;
    g_Handlers[0x163] = DispatchWrapper<OpPacksswb_Sse>;
    g_Handlers[0x16B] = DispatchWrapper<OpPackssdw_Sse>;
    g_Handlers[0x167] = DispatchWrapper<OpPackuswb_Sse>;
    g_Handlers[0x1D5] = DispatchWrapper<OpPmullw_Sse>;
    g_Handlers[0x1E5] = DispatchWrapper<OpPmulhw_Sse>;
    g_Handlers[0x1E4] = DispatchWrapper<OpPmulhuw_Sse>;
    g_Handlers[0x1DC] = DispatchWrapper<OpPaddusb_Sse>;
    g_Handlers[0x1DD] = DispatchWrapper<OpPaddusw_Sse>;
    g_Handlers[0x1EC] = DispatchWrapper<OpPaddsb_Sse>;
    g_Handlers[0x1ED] = DispatchWrapper<OpPaddsw_Sse>;
    g_Handlers[0x1D8] = DispatchWrapper<OpPsubusb_Sse>;
    g_Handlers[0x1D9] = DispatchWrapper<OpPsubusw_Sse>;
    g_Handlers[0x1E8] = DispatchWrapper<OpPsubsb_Sse>;
    g_Handlers[0x1E9] = DispatchWrapper<OpPsubsw_Sse>;
    g_Handlers[0x1E0] = DispatchWrapper<OpPavgb_Sse>;
    g_Handlers[0x1E3] = DispatchWrapper<OpPavgw_Sse>;
    g_Handlers[0x1F6] = DispatchWrapper<OpPsadbw_Sse>;
    g_Handlers[0x1C5] = DispatchWrapper<OpPextrw_Sse>;
    g_Handlers[0x1C4] = DispatchWrapper<OpPinsrw_Sse>;
    g_Handlers[0x1D7] = DispatchWrapper<OpPmovmskb_Sse>;
}

}  // namespace fiberish
