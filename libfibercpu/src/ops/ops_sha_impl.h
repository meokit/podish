#pragma once

#include <simde/x86/ssse3.h>

#include <array>
#include <bit>
#include <cstring>

#if defined(__ARM_FEATURE_SHA2)
#include <arm_neon.h>
#endif

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../state.h"

namespace fiberish::op {

using ShaWords = std::array<uint32_t, 4>;

FORCE_INLINE uint32_t Sha256SmallSigma0(uint32_t word) {
    return std::rotr(word, 7) ^ std::rotr(word, 18) ^ (word >> 3);
}

FORCE_INLINE uint32_t Sha256SmallSigma1(uint32_t word) {
    return std::rotr(word, 17) ^ std::rotr(word, 19) ^ (word >> 10);
}

FORCE_INLINE uint32_t ShaChoose(uint32_t x, uint32_t y, uint32_t z) { return z ^ (x & (y ^ z)); }

FORCE_INLINE uint32_t ShaMajority(uint32_t x, uint32_t y, uint32_t z) { return (x & y) | ((x | y) & z); }

FORCE_INLINE simde__m128 Sha256Msg1(simde__m128 destination, simde__m128 source) {
#if defined(__ARM_FEATURE_SHA2)
    return std::bit_cast<simde__m128>(
        vsha256su0q_u32(std::bit_cast<uint32x4_t>(destination), std::bit_cast<uint32x4_t>(source)));
#else
    const auto dst = std::bit_cast<ShaWords>(destination);
    const auto src = std::bit_cast<ShaWords>(source);
    return std::bit_cast<simde__m128>(ShaWords{dst[0] + Sha256SmallSigma0(dst[1]), dst[1] + Sha256SmallSigma0(dst[2]),
                                               dst[2] + Sha256SmallSigma0(dst[3]), dst[3] + Sha256SmallSigma0(src[0])});
#endif
}

FORCE_INLINE simde__m128 Sha256Msg2(simde__m128 destination, simde__m128 source) {
#if defined(__ARM_FEATURE_SHA2)
    // SHA256SU1 also adds four words from another vector. Zero those words.
    const auto high_words = vsetq_lane_u32(0, std::bit_cast<uint32x4_t>(source), 0);
    return std::bit_cast<simde__m128>(
        vsha256su1q_u32(std::bit_cast<uint32x4_t>(destination), vdupq_n_u32(0), high_words));
#else
    const auto dst = std::bit_cast<ShaWords>(destination);
    const auto src = std::bit_cast<ShaWords>(source);
    const uint32_t w0 = dst[0] + Sha256SmallSigma1(src[2]);
    const uint32_t w1 = dst[1] + Sha256SmallSigma1(src[3]);
    return std::bit_cast<simde__m128>(ShaWords{w0, w1, dst[2] + Sha256SmallSigma1(w0), dst[3] + Sha256SmallSigma1(w1)});
#endif
}

FORCE_INLINE simde__m128 Sha256Rnds2(simde__m128 destination, simde__m128 source, simde__m128 message) {
#if defined(__ARM_FEATURE_SHA2)
    // Intel orders the lanes F,E,B,A and H,G,D,C. ARM uses A,B,C,D and E,F,G,H.
    const auto abef = vrev64q_u32(std::bit_cast<uint32x4_t>(source));
    const auto cdgh = vrev64q_u32(std::bit_cast<uint32x4_t>(destination));
    const auto abcd = vcombine_u32(vget_high_u32(abef), vget_high_u32(cdgh));
    const auto efgh = vcombine_u32(vget_low_u32(abef), vget_low_u32(cdgh));
    const auto wk = std::bit_cast<uint32x4_t>(message);
    // After four ARM rounds, the upper lanes hold the needed results from
    // round two. The extra message words only affect the lower lanes.
    const auto ab = vget_high_u32(vsha256hq_u32(abcd, efgh, wk));
    const auto ef = vget_high_u32(vsha256h2q_u32(efgh, abcd, wk));
    return std::bit_cast<simde__m128>(vcombine_u32(vrev64_u32(ef), vrev64_u32(ab)));
#else
    const auto [h0, g0, d0, c0] = std::bit_cast<ShaWords>(destination);
    const auto [f0, e0, b0, a0] = std::bit_cast<ShaWords>(source);
    const auto wk = std::bit_cast<ShaWords>(message);
    uint32_t a = a0, b = b0, c = c0, d = d0;
    uint32_t e = e0, f = f0, g = g0, h = h0;
    for (unsigned round = 0; round < 2; ++round) {
        const uint32_t sigma0 = std::rotr(a, 2) ^ std::rotr(a, 13) ^ std::rotr(a, 22);
        const uint32_t sigma1 = std::rotr(e, 6) ^ std::rotr(e, 11) ^ std::rotr(e, 25);
        const uint32_t t1 = h + sigma1 + ShaChoose(e, f, g) + wk[round];
        const uint32_t next_a = t1 + sigma0 + ShaMajority(a, b, c);
        h = g;
        g = f;
        f = e;
        e = d + t1;
        d = c;
        c = b;
        b = a;
        a = next_a;
    }
    return std::bit_cast<simde__m128>(ShaWords{f, e, b, a});
#endif
}

FORCE_INLINE ShaWords Sha1Rnds4(ShaWords dst, const ShaWords& src, uint8_t control) {
    constexpr std::array<uint32_t, 4> constants{0x5a827999, 0x6ed9eba1, 0x8f1bbcdc, 0xca62c1d6};
    const unsigned phase = control & 3;
    uint32_t a = dst[3], b = dst[2], c = dst[1], d = dst[0], e = 0;
    for (unsigned round = 0; round < 4; ++round) {
        const uint32_t mix = phase == 0 ? ShaChoose(b, c, d) : phase == 2 ? ShaMajority(b, c, d) : b ^ c ^ d;
        const uint32_t next = std::rotl(a, 5) + mix + e + src[3 - round] + constants[phase];
        e = d;
        d = c;
        c = std::rotl(b, 30);
        b = a;
        a = next;
    }
    return {d, c, b, a};
}

enum class ShaOperation { Sha1Nexte, Sha1Msg1, Sha1Msg2, Sha1Rnds4, Sha256Msg1, Sha256Msg2, Sha256Rnds2 };

template <ShaOperation Operation>
FORCE_INLINE LogicFlow OpSha(LogicFuncParams) {
    const auto src = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, gpr, op, operand_modrm, utlb);
    if (!src) return LogicFlow::RestartMemoryOp;

    auto& dst = state->ctx.xmm[(operand_modrm >> 3) & 7];
    if constexpr (Operation == ShaOperation::Sha256Rnds2) {
        dst = Sha256Rnds2(dst, *src, state->ctx.xmm[0]);
    } else if constexpr (Operation == ShaOperation::Sha256Msg1) {
        dst = Sha256Msg1(dst, *src);
    } else if constexpr (Operation == ShaOperation::Sha256Msg2) {
        dst = Sha256Msg2(dst, *src);
    } else {
        const auto d = std::bit_cast<ShaWords>(dst);
        auto s = std::bit_cast<ShaWords>(*src);
        if constexpr (Operation == ShaOperation::Sha1Nexte) {
            s[3] += std::rotl(d[3], 30);
        } else if constexpr (Operation == ShaOperation::Sha1Msg1) {
            s = {d[0] ^ s[2], d[1] ^ s[3], d[2] ^ d[0], d[3] ^ d[1]};
        } else if constexpr (Operation == ShaOperation::Sha1Msg2) {
            const uint32_t w0 = std::rotl(d[3] ^ s[2], 1);
            s = {std::rotl(d[0] ^ w0, 1), std::rotl(d[1] ^ s[0], 1), std::rotl(d[2] ^ s[1], 1), w0};
        } else {
            s = Sha1Rnds4(d, s, imm);
        }
        dst = std::bit_cast<simde__m128>(s);
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_SHA_IMPL
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_SHA_IMPL

// OpenSSL's SHA-NI routines use these two SSSE3 forms for the message schedule.
FORCE_INLINE LogicFlow OpPshufb_Sse(LogicFuncParams) {
    const auto src = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src) return LogicFlow::RestartMemoryOp;
    auto& dst = state->ctx.xmm[(operand_modrm >> 3) & 7];
    dst = simde_mm_castsi128_ps(simde_mm_shuffle_epi8(simde_mm_castps_si128(dst), simde_mm_castps_si128(*src)));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPalignr_Sse(LogicFuncParams) {
    const auto src = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src) return LogicFlow::RestartMemoryOp;
    auto& dst = state->ctx.xmm[(operand_modrm >> 3) & 7];
    if (imm >= 32) {
        dst = simde_mm_setzero_ps();
        return LogicFlow::Continue;
    }
#if defined(SIMDE_ARM_NEON_A64V8_NATIVE)
    const uint8x16_t positions{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15};
    const uint8x16x2_t input{{std::bit_cast<uint8x16_t>(*src), std::bit_cast<uint8x16_t>(dst)}};
    dst = std::bit_cast<simde__m128>(vqtbl2q_u8(input, vaddq_u8(positions, vdupq_n_u8(imm))));
#else
    std::array<std::byte, 48> bytes{};
    std::memcpy(bytes.data(), &*src, sizeof(dst));
    std::memcpy(bytes.data() + sizeof(dst), &dst, sizeof(dst));
    std::memcpy(&dst, bytes.data() + imm, sizeof(dst));
#endif
    return LogicFlow::Continue;
}

}  // namespace fiberish::op
