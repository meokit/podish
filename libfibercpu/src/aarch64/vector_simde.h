#pragma once

#ifndef FIBERCPU_AARCH64_SIMDE
#if defined(__aarch64__) || defined(__arm__) || defined(_M_ARM64) || defined(_M_ARM64EC) || defined(_M_ARM)
#define FIBERCPU_AARCH64_SIMDE 0
#else
#define FIBERCPU_AARCH64_SIMDE 1
#endif
#endif

#if FIBERCPU_AARCH64_SIMDE
#include <bit>

#include <simde/arm/neon.h>

#include "aarch64.h"

namespace fiberish::aarch64::detail {

constexpr bool UseSimdeVectors = std::endian::native == std::endian::little;

template <unsigned width>
struct SimdeLanes;

// Use unsigned lanes for bitwise and wrapping operations. Only instructions
// whose result depends on signedness reinterpret the lanes as signed.
#define A64_SIMDE_LANES(bits, lanes)                        \
    using U = simde_uint##bits##x##lanes##_t;               \
    using S = simde_int##bits##x##lanes##_t;                \
    static constexpr auto add = simde_vaddq_u##bits;        \
    static constexpr auto sub = simde_vsubq_u##bits;        \
    static constexpr auto add_sat_u = simde_vqaddq_u##bits; \
    static constexpr auto add_sat_s = simde_vqaddq_s##bits; \
    static constexpr auto sub_sat_u = simde_vqsubq_u##bits; \
    static constexpr auto sub_sat_s = simde_vqsubq_s##bits; \
    static constexpr auto gt_u = simde_vcgtq_u##bits;       \
    static constexpr auto gt_s = simde_vcgtq_s##bits;       \
    static constexpr auto ge_u = simde_vcgeq_u##bits;       \
    static constexpr auto ge_s = simde_vcgeq_s##bits;       \
    static constexpr auto eq = simde_vceqq_u##bits;         \
    static constexpr auto test = simde_vtstq_u##bits;       \
    static constexpr auto bit_and = simde_vandq_u##bits;    \
    static constexpr auto dup = simde_vdupq_n_u##bits;      \
    static constexpr auto select = simde_vbslq_u##bits;     \
    static constexpr auto shl_u = simde_vshlq_u##bits;      \
    static constexpr auto shl_s = simde_vshlq_s##bits;      \
    static constexpr auto pair_add = simde_vpaddq_u##bits;  \
    static constexpr auto low = simde_vget_low_u##bits;     \
    static constexpr auto combine = simde_vcombine_u##bits; \
    static constexpr auto uzp1 = simde_vuzp1q_u##bits;      \
    static constexpr auto uzp2 = simde_vuzp2q_u##bits;      \
    static constexpr auto trn1 = simde_vtrn1q_u##bits;      \
    static constexpr auto trn2 = simde_vtrn2q_u##bits;      \
    static constexpr auto zip1 = simde_vzip1q_u##bits;      \
    static constexpr auto zip2 = simde_vzip2q_u##bits;

#define A64_SIMDE_SMALL_LANES(bits, lanes)                             \
    template <>                                                        \
    struct SimdeLanes<bits> {                                          \
        A64_SIMDE_LANES(bits, lanes)                                   \
        static constexpr auto uzp1_d = simde_vuzp1_u##bits;            \
        static constexpr auto uzp2_d = simde_vuzp2_u##bits;            \
        static constexpr auto trn1_d = simde_vtrn1_u##bits;            \
        static constexpr auto trn2_d = simde_vtrn2_u##bits;            \
        static constexpr auto zip1_d = simde_vzip1_u##bits;            \
        static constexpr auto zip2_d = simde_vzip2_u##bits;            \
        static constexpr auto mul = simde_vmulq_u##bits;               \
        static constexpr auto min_u = simde_vminq_u##bits;             \
        static constexpr auto min_s = simde_vminq_s##bits;             \
        static constexpr auto max_u = simde_vmaxq_u##bits;             \
        static constexpr auto max_s = simde_vmaxq_s##bits;             \
        static constexpr auto pair_min_u = simde_vpminq_u##bits;       \
        static constexpr auto pair_min_s = simde_vpminq_s##bits;       \
        static constexpr auto pair_max_u = simde_vpmaxq_u##bits;       \
        static constexpr auto pair_max_s = simde_vpmaxq_s##bits;       \
        static constexpr auto clz = simde_vclzq_u##bits;               \
        static constexpr auto cls = simde_vclsq_s##bits;               \
        static constexpr auto add_reduce = simde_vaddvq_u##bits;       \
        static constexpr auto min_reduce_u = simde_vminvq_u##bits;     \
        static constexpr auto min_reduce_s = simde_vminvq_s##bits;     \
        static constexpr auto max_reduce_u = simde_vmaxvq_u##bits;     \
        static constexpr auto max_reduce_s = simde_vmaxvq_s##bits;     \
        static constexpr auto add_long = simde_vaddlvq_u##bits;        \
        static constexpr auto add_signed_long = simde_vaddlvq_s##bits; \
    };

A64_SIMDE_SMALL_LANES(8, 16)
A64_SIMDE_SMALL_LANES(16, 8)
A64_SIMDE_SMALL_LANES(32, 4)
template <>
struct SimdeLanes<64> {
    A64_SIMDE_LANES(64, 2)
};

#undef A64_SIMDE_SMALL_LANES
#undef A64_SIMDE_LANES

// SIMDe handlers store the same bits as the scalar kernels. The shape mask
// also excludes inactive lanes when checking whether saturation set guest QC.
inline Vector128 SimdeActiveLanes(Vector128 value, bool q, bool scalar = false, unsigned width = 64) {
    return {scalar && width < 64 ? value.lo & ((uint64_t{1} << width) - 1) : value.lo, q && !scalar ? value.hi : 0};
}

inline void SimdeSetQC(State& state, Vector128 changed, bool q, bool scalar = false, unsigned width = 64) {
    const auto active = SimdeActiveLanes(changed, q, scalar, width);
    if (active.lo || active.hi) state.fpsr |= 1u << 27;
}

inline Vector128 SimdeLogical(Vector128 old, Vector128 left, Vector128 right, unsigned operation, bool q) {
    const auto a = std::bit_cast<simde_uint8x16_t>(left);
    const auto b = std::bit_cast<simde_uint8x16_t>(right);
    const auto d = std::bit_cast<simde_uint8x16_t>(old);
    simde_uint8x16_t result;
    switch (operation) {
        case 0:
            result = simde_vandq_u8(a, b);
            break;
        case 1:
            result = simde_vbicq_u8(a, b);
            break;
        case 2:
            result = simde_vorrq_u8(a, b);
            break;
        case 3:
            result = simde_vornq_u8(a, b);
            break;
        case 4:
            result = simde_veorq_u8(a, b);
            break;
        case 5:
            result = simde_vbslq_u8(d, a, b);
            break;
        case 6:
            result = simde_vbslq_u8(b, a, d);
            break;
        default:
            result = simde_vbslq_u8(b, d, a);
            break;
    }
    return SimdeActiveLanes(std::bit_cast<Vector128>(result), q);
}

template <unsigned width>
inline typename SimdeLanes<width>::U SimdeShift(typename SimdeLanes<width>::U source,
                                                typename SimdeLanes<width>::U counts, bool is_unsigned, bool rounding) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    using S = typename L::S;
    const auto shift = [source, is_unsigned](U amount) {
        const auto signed_amount = std::bit_cast<S>(amount);
        return is_unsigned ? L::shl_u(source, signed_amount)
                           : std::bit_cast<U>(L::shl_s(std::bit_cast<S>(source), signed_amount));
    };
    auto result = shift(counts);
    if (rounding) {
        // The low byte determines the direction. Take the round bit separately
        // because adding it to the input can overflow in the portable VRSHL.
        const auto right = L::test(counts, L::dup(0x80));
        const auto round_bit = shift(L::add(counts, L::dup(1)));
        result = L::add(result, L::bit_and(L::bit_and(right, round_bit), L::dup(1)));
    }
    return result;
}

template <unsigned width>
inline bool SimdeThree(State& state, unsigned rd, Vector128 old, Vector128 left, Vector128 right, unsigned operation,
                       bool q, bool u, bool scalar) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    using S = typename L::S;
    auto a = std::bit_cast<U>(left);
    auto b = std::bit_cast<U>(right);
    const auto d = std::bit_cast<U>(old);
    if (!q && (operation == 20 || operation == 21 || operation == 23)) {
        // A D-width pairwise result uses pairs from both inputs within its low half.
        a = b = L::combine(L::low(a), L::low(b));
    }
    const auto sa = std::bit_cast<S>(a);
    const auto sb = std::bit_cast<S>(b);
    U result;
    switch (operation) {
        case 1:
        case 5: {
            if (operation == 1)
                result = u ? L::add_sat_u(a, b) : std::bit_cast<U>(L::add_sat_s(sa, sb));
            else
                result = u ? L::sub_sat_u(a, b) : std::bit_cast<U>(L::sub_sat_s(sa, sb));
            const auto saturated = std::bit_cast<Vector128>(result);
            const auto wrapped = std::bit_cast<Vector128>(operation == 1 ? L::add(a, b) : L::sub(a, b));
            SimdeSetQC(state, {saturated.lo ^ wrapped.lo, saturated.hi ^ wrapped.hi}, q, scalar, width);
            break;
        }
        case 6:
            result = u ? L::gt_u(a, b) : L::gt_s(sa, sb);
            break;
        case 7:
            result = u ? L::ge_u(a, b) : L::ge_s(sa, sb);
            break;
        case 8:
        case 10:
            result = SimdeShift<width>(a, b, u, operation == 10);
            break;
        case 16:
            result = u ? L::sub(a, b) : L::add(a, b);
            break;
        case 17:
            result = u ? L::eq(a, b) : L::test(a, b);
            break;
        case 23:
            if (u) return false;
            result = L::pair_add(a, b);
            break;
        default:
            if constexpr (width < 64) {
                switch (operation) {
                    case 12:
                        result = u ? L::max_u(a, b) : std::bit_cast<U>(L::max_s(sa, sb));
                        break;
                    case 13:
                        result = u ? L::min_u(a, b) : std::bit_cast<U>(L::min_s(sa, sb));
                        break;
                    case 14:
                    case 15: {
                        const auto cmp = u ? L::gt_u(a, b) : L::gt_s(sa, sb);
                        result = L::select(cmp, L::sub(a, b), L::sub(b, a));
                        if (operation == 15) result = L::add(result, d);
                        break;
                    }
                    case 18:
                        result = u ? L::sub(d, L::mul(a, b)) : L::add(d, L::mul(a, b));
                        break;
                    case 19:
                        if (u) return false;
                        result = L::mul(a, b);
                        break;
                    case 20:
                        result = u ? L::pair_max_u(a, b) : std::bit_cast<U>(L::pair_max_s(sa, sb));
                        break;
                    case 21:
                        result = u ? L::pair_min_u(a, b) : std::bit_cast<U>(L::pair_min_s(sa, sb));
                        break;
                    default:
                        return false;
                }
            } else {
                return false;
            }
    }
    state.v[rd] = SimdeActiveLanes(std::bit_cast<Vector128>(result), q, scalar, width);
    return true;
}

template <unsigned width>
inline bool SimdeMisc(State& state, unsigned rd, Vector128 source, unsigned operation, bool q, bool u, bool scalar) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    using S = typename L::S;
    const auto a = std::bit_cast<U>(source);
    const auto sa = std::bit_cast<S>(a);
    const auto zero = L::dup(0);
    U result;
    switch (operation) {
        case 0:
            if constexpr (width == 8)
                result = u ? simde_vrev32q_u8(a) : simde_vrev64q_u8(a);
            else if constexpr (width == 16)
                result = u ? simde_vrev32q_u16(a) : simde_vrev64q_u16(a);
            else if constexpr (width == 32) {
                if (u) return false;
                result = simde_vrev64q_u32(a);
            } else
                return false;
            break;
        case 1:
            if constexpr (width == 8) {
                if (u) return false;
                result = simde_vrev16q_u8(a);
            } else
                return false;
            break;
        case 4:
            if constexpr (width < 64)
                result = u ? L::clz(a) : std::bit_cast<U>(L::cls(sa));
            else
                return false;
            break;
        case 5:
            if constexpr (width == 8)
                result = u ? simde_vmvnq_u8(a) : simde_vcntq_u8(a);
            else if constexpr (width == 16) {
                if (!u) return false;
                result = std::bit_cast<U>(simde_vrbitq_u8(std::bit_cast<simde_uint8x16_t>(source)));
            } else
                return false;
            break;
        case 8:
            result = u ? L::ge_s(sa, std::bit_cast<S>(zero)) : L::gt_s(sa, std::bit_cast<S>(zero));
            break;
        case 9:
            result = u ? L::ge_s(std::bit_cast<S>(zero), sa) : L::eq(a, zero);
            break;
        case 10:
            if (u) return false;
            result = L::gt_s(std::bit_cast<S>(zero), sa);
            break;
        case 11:
            result = u ? L::sub(zero, a) : L::select(L::gt_s(std::bit_cast<S>(zero), sa), L::sub(zero, a), a);
            break;
        default:
            return false;
    }
    state.v[rd] = SimdeActiveLanes(std::bit_cast<Vector128>(result), q, scalar, width);
    return true;
}

template <unsigned width>
inline bool SimdeReduction(State& state, unsigned rd, Vector128 source, unsigned operation, bool q, bool u) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    using S = typename L::S;
    // Duplicate the active half for min/max. Use zeros for the sums.
    auto a = std::bit_cast<U>(q ? source : Vector128{source.lo, operation == 10 || operation == 26 ? source.lo : 0});
    const auto sa = std::bit_cast<S>(a);
    uint64_t result;
    switch (operation) {
        case 3:
            result = u ? L::add_long(a) : uint64_t(L::add_signed_long(sa));
            break;
        case 10:
            result = u ? L::max_reduce_u(a) : uint64_t(L::max_reduce_s(sa));
            break;
        case 26:
            result = u ? L::min_reduce_u(a) : uint64_t(L::min_reduce_s(sa));
            break;
        case 27:
            if (u) return false;
            result = L::add_reduce(a);
            break;
        default:
            return false;
    }
    const auto result_width = operation == 3 ? width * 2 : width;
    state.v[rd] = SimdeActiveLanes({result, 0}, false, true, result_width);
    return true;
}

template <unsigned width>
struct SimdeWideLanes;

#define A64_SIMDE_WIDE_LANES(bits, wide, lanes)                           \
    template <>                                                           \
    struct SimdeWideLanes<bits> {                                         \
        using HalfU = simde_uint##bits##x##lanes##_t;                     \
        using HalfS = simde_int##bits##x##lanes##_t;                      \
        static constexpr auto high = simde_vget_high_u##bits;             \
        static constexpr auto extend_u = simde_vmovl_u##bits;             \
        static constexpr auto extend_s = simde_vmovl_s##bits;             \
        static constexpr auto multiply_u = simde_vmull_u##bits;           \
        static constexpr auto multiply_s = simde_vmull_s##bits;           \
        static constexpr auto narrow = simde_vmovn_u##wide;               \
        static constexpr auto narrow_sat_u = simde_vqmovn_u##wide;        \
        static constexpr auto narrow_sat_s = simde_vqmovn_s##wide;        \
        static constexpr auto narrow_signed_to_u = simde_vqmovun_s##wide; \
        static constexpr auto add_high = simde_vaddhn_u##wide;            \
        static constexpr auto rounded_add_high = simde_vraddhn_u##wide;   \
        static constexpr auto sub_high = simde_vsubhn_u##wide;            \
        static constexpr auto rounded_sub_high = simde_vrsubhn_u##wide;   \
    };
A64_SIMDE_WIDE_LANES(8, 16, 8)
A64_SIMDE_WIDE_LANES(16, 32, 4)
A64_SIMDE_WIDE_LANES(32, 64, 2)
#undef A64_SIMDE_WIDE_LANES

template <unsigned width>
inline Vector128 SimdeWiden(State& state, Vector128 old, Vector128 left, Vector128 right, unsigned operation, bool q,
                            bool u) {
    using L = SimdeLanes<width>;
    using W = SimdeLanes<width * 2>;
    using B = SimdeWideLanes<width>;
    using WideU = typename W::U;
    using WideS = typename W::S;
    const auto wide_left = std::bit_cast<WideU>(left);
    const auto wide_right = std::bit_cast<WideU>(right);
    if (operation == 4 || operation == 6) {
        const auto narrow = operation == 4
                                ? (u ? B::rounded_add_high(wide_left, wide_right) : B::add_high(wide_left, wide_right))
                                : (u ? B::rounded_sub_high(wide_left, wide_right) : B::sub_high(wide_left, wide_right));
        const auto bits = std::bit_cast<uint64_t>(narrow);
        return q ? Vector128{old.lo, bits} : Vector128{bits, 0};
    }

    const auto l = std::bit_cast<typename L::U>(left);
    const auto r = std::bit_cast<typename L::U>(right);
    const auto a = q ? B::high(l) : L::low(l);
    const auto b = q ? B::high(r) : L::low(r);
    const auto sa = std::bit_cast<typename B::HalfS>(a);
    const auto sb = std::bit_cast<typename B::HalfS>(b);
    const auto wa = u ? B::extend_u(a) : std::bit_cast<WideU>(B::extend_s(sa));
    const auto wb = u ? B::extend_u(b) : std::bit_cast<WideU>(B::extend_s(sb));
    const auto accum = std::bit_cast<WideU>(old);
    WideU result;
    switch (operation) {
        case 0:
            result = W::add(wa, wb);
            break;
        case 1:
            result = W::add(wide_left, wb);
            break;
        case 2:
            result = W::sub(wa, wb);
            break;
        case 3:
            result = W::sub(wide_left, wb);
            break;
        case 5:
        case 7: {
            const auto greater = u ? W::gt_u(wa, wb) : W::gt_s(std::bit_cast<WideS>(wa), std::bit_cast<WideS>(wb));
            result = W::select(greater, W::sub(wa, wb), W::sub(wb, wa));
            if (operation == 5) result = W::add(accum, result);
            break;
        }
        default: {
            result = u ? B::multiply_u(a, b) : std::bit_cast<WideU>(B::multiply_s(sa, sb));
            if (operation == 8)
                result = W::add(accum, result);
            else if (operation == 10)
                result = W::sub(accum, result);
            else if (operation != 12) {
                const auto signed_product = std::bit_cast<WideS>(result);
                const auto saturated = std::bit_cast<WideU>(W::add_sat_s(signed_product, signed_product));
                const auto sat_product = std::bit_cast<Vector128>(saturated);
                const auto wrap_product = std::bit_cast<Vector128>(W::add(result, result));
                Vector128 changed{sat_product.lo ^ wrap_product.lo, sat_product.hi ^ wrap_product.hi};
                result = saturated;
                if (operation != 13) {
                    const auto acc = std::bit_cast<WideS>(accum);
                    const auto term = std::bit_cast<WideS>(saturated);
                    const auto clipped = operation == 9 ? W::add_sat_s(acc, term) : W::sub_sat_s(acc, term);
                    const auto wrapped = operation == 9 ? W::add(accum, saturated) : W::sub(accum, saturated);
                    result = std::bit_cast<WideU>(clipped);
                    const auto sat_bits = std::bit_cast<Vector128>(result);
                    const auto wrap_bits = std::bit_cast<Vector128>(wrapped);
                    changed.lo |= sat_bits.lo ^ wrap_bits.lo;
                    changed.hi |= sat_bits.hi ^ wrap_bits.hi;
                }
                SimdeSetQC(state, changed, true);
            }
            break;
        }
    }
    return std::bit_cast<Vector128>(result);
}

template <unsigned width>
inline Vector128 SimdeNarrow(State& state, Vector128 old, Vector128 source, unsigned operation, bool q, bool u,
                             bool scalar) {
    using B = SimdeWideLanes<width>;
    using W = SimdeLanes<width * 2>;
    using NarrowU = typename B::HalfU;
    const auto a = std::bit_cast<typename W::U>(source);
    const auto sa = std::bit_cast<typename W::S>(source);
    NarrowU result;
    if (operation == 18 && !u) {
        result = B::narrow(a);
    } else {
        const bool signed_result = operation == 20 && !u;
        if (signed_result)
            result = std::bit_cast<NarrowU>(B::narrow_sat_s(sa));
        else
            result = operation == 18 ? B::narrow_signed_to_u(sa) : B::narrow_sat_u(a);
        const auto roundtrip = signed_result
                                   ? std::bit_cast<Vector128>(B::extend_s(std::bit_cast<typename B::HalfS>(result)))
                                   : std::bit_cast<Vector128>(B::extend_u(result));
        SimdeSetQC(state, {roundtrip.lo ^ source.lo, roundtrip.hi ^ source.hi}, true, scalar, width * 2);
    }
    const auto low = std::bit_cast<uint64_t>(result);
    if (q && !scalar) return {old.lo, low};
    return SimdeActiveLanes({low, 0}, false, scalar, width);
}

template <unsigned width>
inline typename SimdeLanes<width>::U SimdeShiftRight(typename SimdeLanes<width>::U source, unsigned shift,
                                                     bool is_unsigned, bool rounding) {
    using L = SimdeLanes<width>;
    return SimdeShift<width>(source, L::dup(0 - uint64_t(shift)), is_unsigned, rounding);
}

template <unsigned width>
inline bool SimdeShiftImmediate(State& state, unsigned rd, Vector128 old, Vector128 source, unsigned operation,
                                unsigned imm, bool q, bool u, bool scalar) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    using S = typename L::S;
    const auto a = std::bit_cast<U>(source);
    const auto d = std::bit_cast<U>(old);
    if constexpr (width < 64) {
        using B = SimdeWideLanes<width>;
        using W = SimdeLanes<2 * width>;
        using WideU = typename W::U;
        using WideS = typename W::S;
        if (operation == 16 || operation == 17) {
            if (u || scalar) return false;
            const auto shifted =
                SimdeShiftRight<2 * width>(std::bit_cast<WideU>(source), 2 * width - imm, true, operation == 17);
            const auto low = std::bit_cast<uint64_t>(B::narrow(shifted));
            state.v[rd] = q ? Vector128{old.lo, low} : Vector128{low, 0};
            return true;
        }
        if (operation == 20) {
            if (scalar) return false;
            const auto half = q ? B::high(a) : L::low(a);
            const auto wide =
                u ? B::extend_u(half) : std::bit_cast<WideU>(B::extend_s(std::bit_cast<typename B::HalfS>(half)));
            state.v[rd] = std::bit_cast<Vector128>(W::shl_u(wide, std::bit_cast<WideS>(W::dup(imm - width))));
            return true;
        }
    }
    if (scalar ? width != 64 : (width == 64 && !q)) return false;

    U result;
    switch (operation) {
        case 0:
        case 2:
        case 4:
        case 6:
            result = SimdeShiftRight<width>(a, 2 * width - imm, u, operation & 4);
            if (operation & 2) result = L::add(result, d);
            break;
        case 8: {
            if (!u) return false;
            const auto mask = L::dup((uint64_t{1} << (imm - width)) - 1);
            result = L::select(mask, SimdeShiftRight<width>(a, 2 * width - imm, true, false), d);
            break;
        }
        case 10: {
            const auto shift = imm - width;
            result = L::shl_u(a, std::bit_cast<S>(L::dup(shift)));
            if (u) result = L::select(L::dup((uint64_t{1} << shift) - 1), d, result);
            break;
        }
        default:
            return false;
    }
    state.v[rd] = SimdeActiveLanes(std::bit_cast<Vector128>(result), q, scalar, width);
    return true;
}

template <unsigned width>
inline Vector128 SimdeSplat(uint64_t value, bool q) {
    return SimdeActiveLanes(std::bit_cast<Vector128>(SimdeLanes<width>::dup(value)), q);
}

template <unsigned width>
inline Vector128 SimdePermute(Vector128 a, Vector128 b, unsigned opcode, bool q) {
    using L = SimdeLanes<width>;
    using U = typename L::U;
    const auto l = std::bit_cast<U>(a);
    const auto r = std::bit_cast<U>(b);
    if constexpr (width < 64) {
        if (!q) {
            const auto lo = L::low(l);
            const auto ro = L::low(r);
            auto result = lo;
            switch (opcode) {
                case 1:
                    result = L::uzp1_d(lo, ro);
                    break;
                case 2:
                    result = L::trn1_d(lo, ro);
                    break;
                case 3:
                    result = L::zip1_d(lo, ro);
                    break;
                case 5:
                    result = L::uzp2_d(lo, ro);
                    break;
                case 6:
                    result = L::trn2_d(lo, ro);
                    break;
                default:
                    result = L::zip2_d(lo, ro);
                    break;
            }
            return {std::bit_cast<uint64_t>(result), 0};
        }
    }
    U result;
    switch (opcode) {
        case 1:
            result = L::uzp1(l, r);
            break;
        case 2:
            result = L::trn1(l, r);
            break;
        case 3:
            result = L::zip1(l, r);
            break;
        case 5:
            result = L::uzp2(l, r);
            break;
        case 6:
            result = L::trn2(l, r);
            break;
        default:
            result = L::zip2(l, r);
            break;
    }
    return SimdeActiveLanes(std::bit_cast<Vector128>(result), q);
}

inline Vector128 SimdeExtract(Vector128 left, Vector128 right, unsigned index, bool q) {
    const auto start = simde_vdupq_n_u8(uint8_t(index));
    const auto lanes = std::bit_cast<simde_uint8x16_t>(Vector128{0x0706050403020100ull, 0x0f0e0d0c0b0a0908ull});
    const auto indices = simde_vaddq_u8(start, lanes);
    if (q) {
        const simde_uint8x16x2_t input{{std::bit_cast<simde_uint8x16_t>(left), std::bit_cast<simde_uint8x16_t>(right)}};
        return std::bit_cast<Vector128>(simde_vqtbl2q_u8(input, indices));
    }
    const auto input = std::bit_cast<simde_uint8x16_t>(Vector128{left.lo, right.lo});
    return {std::bit_cast<uint64_t>(simde_vqtbl1_u8(input, simde_vget_low_u8(indices))), 0};
}

inline Vector128 SimdeTable(const State& state, unsigned rd, unsigned rn, unsigned rm, unsigned length, bool q,
                            bool keep) {
    const auto at = [&](unsigned offset) { return std::bit_cast<simde_uint8x16_t>(state.v[(rn + offset) & 31]); };
    const auto index = std::bit_cast<simde_uint8x16_t>(state.v[rm]);
    const auto dest = std::bit_cast<simde_uint8x16_t>(state.v[rd]);
    simde_uint8x16_t result;
    switch (length) {
        case 1: {
            const auto table = at(0);
            result = keep ? simde_vqtbx1q_u8(dest, table, index) : simde_vqtbl1q_u8(table, index);
            break;
        }
        case 2: {
            const simde_uint8x16x2_t table{{at(0), at(1)}};
            result = keep ? simde_vqtbx2q_u8(dest, table, index) : simde_vqtbl2q_u8(table, index);
            break;
        }
        case 3: {
            const simde_uint8x16x3_t table{{at(0), at(1), at(2)}};
            result = keep ? simde_vqtbx3q_u8(dest, table, index) : simde_vqtbl3q_u8(table, index);
            break;
        }
        default: {
            const simde_uint8x16x4_t table{{at(0), at(1), at(2), at(3)}};
            result = keep ? simde_vqtbx4q_u8(dest, table, index) : simde_vqtbl4q_u8(table, index);
            break;
        }
    }
    return SimdeActiveLanes(std::bit_cast<Vector128>(result), q);
}

}  // namespace fiberish::aarch64::detail
#endif
