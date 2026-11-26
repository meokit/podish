#pragma once

#include "floating.h"
#include "vector_simde.h"

#if defined(_MSC_VER)
#define A64_VECTOR_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_VECTOR_INLINE inline __attribute__((always_inline))
#else
#define A64_VECTOR_INLINE inline
#endif

namespace fiberish::aarch64::detail {

inline uint64_t Element(Vector128 v, unsigned width, unsigned index) {
    const unsigned bit = index * width;
    return ((bit >= 64 ? v.hi : v.lo) >> (bit & 63)) & Ones(width);
}

inline void SetElement(Vector128& v, unsigned width, unsigned index, uint64_t value) {
    const unsigned bit = index * width;
    auto& half = bit >= 64 ? v.hi : v.lo;
    const unsigned shift = bit & 63;
    half = (half & ~(Ones(width) << shift)) | ((value & Ones(width)) << shift);
}

inline Vector128 Splat(uint64_t element, unsigned width, bool q) {
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (width) {
            case 8:
                return SimdeSplat<8>(element, q);
            case 16:
                return SimdeSplat<16>(element, q);
            case 32:
                return SimdeSplat<32>(element, q);
            default:
                return SimdeSplat<64>(element, q);
        }
    }
#endif
    const auto word = Replicate(element & Ones(width), width, 64);
    return {word, q ? word : 0};
}

inline uint64_t ShiftRightElement(uint64_t value, unsigned width, unsigned shift, bool is_unsigned, bool rounding) {
    const bool negative = !is_unsigned && (value >> (width - 1));
    const auto rounded = rounding && (shift > width ? negative : shift && ((value >> (shift - 1)) & 1));
    if (shift >= width) return (negative ? UINT64_MAX : 0) + rounded;
    return (negative ? uint64_t(Signed(value, width) >> shift) : value >> shift) + rounded;
}

inline uint64_t SelectMinMax(uint64_t a, uint64_t b, unsigned width, bool is_unsigned, bool max) {
    const bool greater = is_unsigned ? a > b : Signed(a, width) > Signed(b, width);
    return (max == greater) ? a : b;
}

inline uint64_t Saturate(State& state, __int128_t value, unsigned width, bool is_unsigned) {
    const auto sign = __int128_t{1} << (width - 1);
    const auto lower = is_unsigned ? 0 : -sign;
    const auto upper = is_unsigned ? __int128_t(Ones(width)) : sign - 1;
    if (value < lower || value > upper) state.fpsr |= 1u << 27;
    return uint64_t(std::clamp(value, lower, upper));
}

inline uint64_t VectorLogical(uint64_t old, uint64_t a, uint64_t b, unsigned op) {
    switch (op) {
        case 0:
            return a & b;
        case 1:
            return a & ~b;
        case 2:
            return a | b;
        case 3:
            return a | ~b;
        case 4:
            return a ^ b;
        case 5:
            return (a & old) | (b & ~old);
        case 6:
            return (a & b) | (old & ~b);
        default:
            return (a & ~b) | (old & b);
    }
}

template <bool floating, bool jit = false>
A64_VECTOR_INLINE bool VectorThree(State& state, const DecodedInstruction& op, bool q, bool u, bool scalar,
                                   unsigned size) {
    const auto old = state.v[op.rd];
    const auto left = state.v[op.rn];
    const auto right = state.v[op.rm];
    const auto operation = Field(op.word, 11, 5);
    const bool size_high = size & 2;
    unsigned width = 8u << size;
    if constexpr (floating) {
        width = (size & 1) ? 64 : 32;
        if (!scalar && width == 64 && !q) return false;
        FPOp fp;
        bool pairwise = false;
        switch (operation) {
            case 24:
                if (scalar) return false;
                fp = size_high ? FPOp::MinNumber : FPOp::MaxNumber;
                pairwise = u;
                break;
            case 25:
                if (u || scalar) return false;
                fp = FPOp::Fused;
                break;
            case 26:
                if (scalar || (u && size_high)) return false;  // FABD handled below.
                fp = size_high ? FPOp::Sub : FPOp::Add;
                pairwise = u;
                break;
            case 27:
                if (!u || size_high || scalar) return false;
                fp = FPOp::Mul;
                break;
            case 28: {
                if (!u && size_high) return false;
                const auto f = FPBits(width);
                Vector128 result;
                const unsigned lanes = scalar ? 1 : (q ? 128 : 64) / width;
                for (unsigned i = 0; i < lanes; ++i) {
                    const auto a = FPInput(state, Element(left, width, i), f);
                    const auto b = FPInput(state, Element(right, width, i), f);
                    bool predicate = false;
                    if (FPNaN(a, f) || FPNaN(b, f)) {
                        if (u || FPSignalingNaN(a, f) || FPSignalingNaN(b, f)) state.fpsr |= FPInvalid;
                    } else {
                        const auto cmp = FPCompare(a, b, f);
                        predicate = u ? (size_high ? cmp > 0 : cmp >= 0) : cmp == 0;
                    }
                    SetElement(result, width, i, predicate ? UINT64_MAX : 0);
                }
                state.v[op.rd] = result;
                return true;
            }
            case 30:
                if (scalar) return false;
                fp = size_high ? FPOp::Min : FPOp::Max;
                pairwise = u;
                break;
            case 31:
                if (!u || size_high || scalar) return false;
                fp = FPOp::Div;
                break;
            default:
                return false;
        }
        const unsigned lanes = (q ? 128 : 64) / width;
        const unsigned half = lanes / 2;
        Vector128 result;
        for (unsigned i = 0; i < lanes; ++i) {
            auto a = Element(left, width, i);
            auto b = Element(right, width, i);
            if (pairwise) {
                const auto source = i < half ? left : right;
                a = Element(source, width, 2 * (i % half));
                b = Element(source, width, 2 * (i % half) + 1);
            }
            if (fp == FPOp::Fused && size_high) a ^= FPBits(width).sign;
            SetElement(result, width, i,
                       BoundFPArithmetic<jit>(state, width, fp, a, b, Element(old, width, i), Nearest, false));
        }
        state.v[op.rd] = result;
        return true;
    }
    if (operation == 3 && !scalar) {
        const auto logic = size | (u ? 4 : 0);
#if FIBERCPU_AARCH64_SIMDE
        if constexpr (UseSimdeVectors) {
            state.v[op.rd] = SimdeLogical(old, left, right, logic, q);
            return true;
        }
#endif
        state.v[op.rd] = {VectorLogical(old.lo, left.lo, right.lo, logic),
                          q ? VectorLogical(old.hi, left.hi, right.hi, logic) : 0};
        return true;
    }

    if (scalar) {
        const bool any_size = operation == 1 || operation == 5;
        const bool only_d =
            operation == 6 || operation == 7 || operation == 8 || operation == 10 || operation == 16 || operation == 17;
        if (!any_size && !(only_d && size == 3)) return false;
    } else if (size == 3) {
        if (!q) return false;
        if ((operation >= 12 && operation <= 15) || (operation >= 18 && operation <= 21)) return false;
    }

#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (size) {
            case 0:
                return SimdeThree<8>(state, op.rd, old, left, right, operation, q, u, scalar);
            case 1:
                return SimdeThree<16>(state, op.rd, old, left, right, operation, q, u, scalar);
            case 2:
                return SimdeThree<32>(state, op.rd, old, left, right, operation, q, u, scalar);
            default:
                return SimdeThree<64>(state, op.rd, old, left, right, operation, q, u, scalar);
        }
    }
#endif

    const bool pairwise = operation == 20 || operation == 21 || operation == 23;
    const unsigned lanes = scalar ? 1 : (q ? 128 : 64) / width;
    const unsigned half = lanes / 2;
    Vector128 result;
    for (unsigned i = 0; i < lanes; ++i) {
        auto a = Element(left, width, i);
        auto b = Element(right, width, i);
        if (pairwise) {
            const auto source = i < half ? left : right;
            a = Element(source, width, 2 * (i % half));
            b = Element(source, width, 2 * (i % half) + 1);
        }
        uint64_t value;
        switch (operation) {
            case 1:
            case 5: {
                const auto aa = u ? __int128_t(a) : __int128_t(Signed(a, width));
                const auto bb = u ? __int128_t(b) : __int128_t(Signed(b, width));
                value = Saturate(state, operation == 1 ? aa + bb : aa - bb, width, u);
                break;
            }
            case 6:
                value = (u ? a > b : Signed(a, width) > Signed(b, width)) ? UINT64_MAX : 0;
                break;
            case 7:
                value = (u ? a >= b : Signed(a, width) >= Signed(b, width)) ? UINT64_MAX : 0;
                break;
            case 8:
            case 10: {
                const auto amount = int(Signed(b, 8));
                value = amount >= 0 ? (unsigned(amount) >= width ? 0 : a << amount)
                                    : ShiftRightElement(a, width, -amount, u, operation == 10);
                break;
            }
            case 12:
            case 13:
            case 20:
            case 21:
                value = SelectMinMax(a, b, width, u, !(operation & 1));
                break;
            case 14:
            case 15: {
                const auto greatest = SelectMinMax(a, b, width, u, true);
                value = greatest == a ? a - b : b - a;
                if (operation == 15) value += Element(old, width, i);
                break;
            }
            case 16:
                value = u ? a - b : a + b;
                break;
            case 17:
                value = (u ? a == b : (a & b) != 0) ? UINT64_MAX : 0;
                break;
            case 18:
                value = u ? Element(old, width, i) - a * b : Element(old, width, i) + a * b;
                break;
            case 19:
                if (u) return false;
                value = a * b;
                break;
            case 23:
                if (u) return false;
                value = a + b;
                break;
            default:
                return false;
        }
        SetElement(result, width, i, value);
    }
    state.v[op.rd] = result;
    return true;
}

A64_VECTOR_INLINE bool VectorWiden(State& state, const DecodedInstruction& op, bool q, bool u, unsigned size) {
    const unsigned opcode = Field(op.word, 12, 4);
    if (size == 3 || opcode >= 14 || (u && (opcode == 9 || opcode == 11 || opcode == 13))) return false;
    if (size == 0 && (opcode == 9 || opcode == 11 || opcode == 13)) return false;

    const unsigned width = 8u << size;
    const unsigned wide = width * 2;
    const unsigned lanes = 64 / width;
    const unsigned upper = q ? lanes : 0;
    const auto left = state.v[op.rn];
    const auto right = state.v[op.rm];
    const auto old = state.v[op.rd];
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (size) {
            case 0:
                state.v[op.rd] = SimdeWiden<8>(state, old, left, right, opcode, q, u);
                break;
            case 1:
                state.v[op.rd] = SimdeWiden<16>(state, old, left, right, opcode, q, u);
                break;
            default:
                state.v[op.rd] = SimdeWiden<32>(state, old, left, right, opcode, q, u);
                break;
        }
        return true;
    }
#endif
    Vector128 result;
    if (opcode == 4 || opcode == 6) {
        if (q) result.lo = old.lo;
        for (unsigned i = 0; i < lanes; ++i) {
            const auto a = Element(left, wide, i);
            const auto b = Element(right, wide, i);
            const auto rounded = (opcode == 4 ? a + b : a - b) + (u ? uint64_t{1} << (width - 1) : 0);
            SetElement(result, width, upper + i, rounded >> width);
        }
    } else {
        for (unsigned i = 0; i < lanes; ++i) {
            const auto a = Element(left, width, upper + i);
            const auto b = Element(right, width, upper + i);
            const auto aa = u ? __int128_t(a) : __int128_t(Signed(a, width));
            const auto bb = u ? __int128_t(b) : __int128_t(Signed(b, width));
            const auto accum = Element(old, wide, i);
            uint64_t value;
            switch (opcode) {
                case 0:
                    value = uint64_t(aa + bb);
                    break;
                case 1:
                    value = Element(left, wide, i) + uint64_t(bb);
                    break;
                case 2:
                    value = uint64_t(aa - bb);
                    break;
                case 3:
                    value = Element(left, wide, i) - uint64_t(bb);
                    break;
                case 5:
                    value = accum + uint64_t(aa > bb ? aa - bb : bb - aa);
                    break;
                case 7:
                    value = uint64_t(aa > bb ? aa - bb : bb - aa);
                    break;
                case 8:
                    value = accum + uint64_t(aa * bb);
                    break;
                case 10:
                    value = accum - uint64_t(aa * bb);
                    break;
                case 12:
                    value = uint64_t(aa * bb);
                    break;
                default: {
                    const auto product = Signed(Saturate(state, 2 * aa * bb, wide, false), wide);
                    if (opcode == 13)
                        value = uint64_t(product);
                    else {
                        const __int128_t term = opcode == 9 ? __int128_t(product) : -__int128_t(product);
                        value = Saturate(state, Signed(accum, wide) + term, wide, false);
                    }
                    break;
                }
            }
            SetElement(result, wide, i, value);
        }
    }
    state.v[op.rd] = result;
    return true;
}

template <bool floating, bool jit = false>
A64_VECTOR_INLINE bool VectorMisc(State& state, const DecodedInstruction& op, bool q, bool u, bool scalar,
                                  unsigned size) {
    const unsigned operation = Field(op.word, 12, 5);
    const auto source = state.v[op.rn];
    unsigned width = 8u << size;
    Vector128 result;
    if constexpr (floating) {
        width = (size & 1) ? 64 : 32;
        if (width == 64 && !q && !scalar) return false;
        const unsigned lanes = scalar ? 1 : (q ? 128 : 64) / width;
        for (unsigned i = 0; i < lanes; ++i) {
            auto value = Element(source, width, i);
            if (operation == 15 && size >= 2 && !scalar) {
                if (u)
                    value ^= FPBits(width).sign;
                else
                    value &= ~FPBits(width).sign;
            } else if (operation == 31 && size >= 2 && u && !scalar) {
                value = BoundFPArithmetic<jit>(state, width, FPOp::Sqrt, value, 0, 0, Nearest, false);
            } else if (operation == 27 && size >= 2) {
                value = FPToInt(state, width, value, width, u, 0, TowardZero);
            } else if (operation == 29 && size < 2) {
                value = BoundIntToFP<jit>(state, width, value, width, u, 0);
            } else {
                return false;
            }
            SetElement(result, width, i, value);
        }
        state.v[op.rd] = result;
        return true;
    }
    if (operation == 18 || operation == 20) {
        if (size == 3 || (scalar && operation == 18 && !u)) return false;
#if FIBERCPU_AARCH64_SIMDE
        if constexpr (UseSimdeVectors) {
            const auto old = state.v[op.rd];
            switch (size) {
                case 0:
                    state.v[op.rd] = SimdeNarrow<8>(state, old, source, operation, q, u, scalar);
                    break;
                case 1:
                    state.v[op.rd] = SimdeNarrow<16>(state, old, source, operation, q, u, scalar);
                    break;
                default:
                    state.v[op.rd] = SimdeNarrow<32>(state, old, source, operation, q, u, scalar);
                    break;
            }
            return true;
        }
#endif
        if (q && !scalar) result.lo = state.v[op.rd].lo;
        const unsigned lanes = scalar ? 1 : 64 / width;
        const unsigned start = q && !scalar ? lanes : 0;
        for (unsigned i = 0; i < lanes; ++i) {
            auto value = Element(source, width * 2, i);
            if (operation == 20 || u) {
                const auto extended = operation == 20 && u ? __int128_t(value) : __int128_t(Signed(value, width * 2));
                value = Saturate(state, extended, width, u);
            }
            SetElement(result, width, start + i, value);
        }
        state.v[op.rd] = result;
        return true;
    }

    if (scalar ? (size != 3 || operation < 8 || operation > 11) : (size == 3 && !q)) return false;
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (size) {
            case 0:
                return SimdeMisc<8>(state, op.rd, source, operation, q, u, scalar);
            case 1:
                return SimdeMisc<16>(state, op.rd, source, operation, q, u, scalar);
            case 2:
                return SimdeMisc<32>(state, op.rd, source, operation, q, u, scalar);
            default:
                return SimdeMisc<64>(state, op.rd, source, operation, q, u, scalar);
        }
    }
#endif

    const unsigned lanes = scalar ? 1 : (q ? 128 : 64) / width;
    for (unsigned i = 0; i < lanes; ++i) {
        const auto a = Element(source, width, i);
        uint64_t value;
        switch (operation) {
            case 0:
            case 1: {
                if (operation == 1 && u) return false;
                const unsigned granule = operation == 1 ? 16 : u ? 32 : 64;
                if (width >= granule) return false;
                value = Element(source, width, i ^ ((granule / width) - 1));
                break;
            }
            case 4:
                if (size > 2) return false;
                if (!u && (a >> (width - 1)))
                    value = a ^ Ones(width);
                else
                    value = a;
                value = std::countl_zero(value) - (64 - width) - unsigned(!u);
                break;
            case 5:
                if (size == 0)
                    value = u ? (~a & UINT8_MAX) : std::popcount(a);
                else if (size == 1 && u) {
                    // RBIT is encoded with size 01 but operates on byte lanes.
                    const auto reverse_bytes = [](uint64_t bits) { return std::byteswap(ReverseBits(bits, 64)); };
                    state.v[op.rd] = {reverse_bytes(source.lo), q ? reverse_bytes(source.hi) : 0};
                    return true;
                } else
                    return false;
                break;
            case 8:
                value = (u ? Signed(a, width) >= 0 : Signed(a, width) > 0) ? UINT64_MAX : 0;
                break;
            case 9:
                value = (u ? Signed(a, width) <= 0 : a == 0) ? UINT64_MAX : 0;
                break;
            case 10:
                if (u) return false;
                value = Signed(a, width) < 0 ? UINT64_MAX : 0;
                break;
            case 11:
                value = (u || Signed(a, width) < 0) ? uint64_t{0} - a : a;
                break;
            default:
                return false;
        }
        SetElement(result, width, i, value);
    }
    state.v[op.rd] = result;
    return true;
}

A64_VECTOR_INLINE bool VectorReduction(State& state, const DecodedInstruction& op, bool q, bool u, unsigned size) {
    const unsigned width = 8u << size;
    const auto operation = Field(op.word, 12, 5);
    if (size == 3 || (size == 2 && !q)) return false;
    const auto source = state.v[op.rn];
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (size) {
            case 0:
                return SimdeReduction<8>(state, op.rd, source, operation, q, u);
            case 1:
                return SimdeReduction<16>(state, op.rd, source, operation, q, u);
            default:
                return SimdeReduction<32>(state, op.rd, source, operation, q, u);
        }
    }
#endif
    uint64_t value = Element(source, width, 0);
    if (operation == 3 || operation == 27) {
        if (operation == 27 && u) return false;
        value = 0;
        for (unsigned i = 0; i < (q ? 128u : 64u) / width; ++i) {
            const auto element = Element(source, width, i);
            value += (operation == 3 && !u) ? uint64_t(Signed(element, width)) : element;
        }
        value &= Ones(operation == 3 ? width * 2 : width);
    } else if (operation == 10 || operation == 26) {
        for (unsigned i = 1; i < (q ? 128u : 64u) / width; ++i)
            value = SelectMinMax(value, Element(source, width, i), width, u, operation == 10);
    } else {
        return false;
    }
    state.v[op.rd] = {value, 0};
    return true;
}

A64_VECTOR_INLINE bool VectorModifiedImmediate(State& state, const DecodedInstruction& op, bool q, bool u) {
    if (Field(op.word, 11, 1)) return false;
    const auto cmode = Field(op.word, 12, 4);
    uint64_t bits = (Field(op.word, 16, 3) << 5) | Field(op.word, 5, 5);
    unsigned width;
    if (cmode < 8) {
        width = 32;
        bits <<= (cmode >> 1) * 8;
    } else if (cmode < 12) {
        width = 16;
        bits <<= ((cmode >> 1) & 1) * 8;
    } else if (cmode < 14) {
        width = 32;
        const unsigned shift = (cmode - 11) * 8;
        bits = (bits << shift) | Ones(shift);
    } else if (cmode == 14) {
        width = u ? 64 : 8;
        if (u) {
            uint64_t expanded = 0;
            for (unsigned i = 0; i < 8; ++i)
                if ((bits >> i) & 1) expanded |= uint64_t{255} << (i * 8);
            bits = expanded;
        }
    } else {
        width = u ? 64 : 32;
        if (u && !q) return false;
        bits = FPImmediate(width, bits);
    }

    if (cmode < 12 && (cmode & 1)) {
        const auto mask = Splat(bits, width, q);
        auto& dest = state.v[op.rd];
        dest.lo = u ? dest.lo & ~mask.lo : dest.lo | mask.lo;
        dest.hi = q ? (u ? dest.hi & ~mask.hi : dest.hi | mask.hi) : 0;
    } else {
        if (u && cmode < 14) bits ^= Ones(width);
        state.v[op.rd] = Splat(bits, width, q);
    }
    return true;
}

A64_VECTOR_INLINE bool VectorShiftImmediate(State& state, const DecodedInstruction& op, bool q, bool u, bool scalar) {
    const auto imm = Field(op.word, 16, 7);
    const auto width = std::bit_floor(imm);
    const auto operation = Field(op.word, 11, 5);
    const auto old = state.v[op.rd];
    const auto source = state.v[op.rn];
    Vector128 result;
    if (width < 8) return false;
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (width) {
            case 8:
                return SimdeShiftImmediate<8>(state, op.rd, old, source, operation, imm, q, u, scalar);
            case 16:
                return SimdeShiftImmediate<16>(state, op.rd, old, source, operation, imm, q, u, scalar);
            case 32:
                return SimdeShiftImmediate<32>(state, op.rd, old, source, operation, imm, q, u, scalar);
            default:
                return SimdeShiftImmediate<64>(state, op.rd, old, source, operation, imm, q, u, scalar);
        }
    }
#endif
    if (operation == 16 || operation == 17) {
        if (width == 64 || u || scalar) return false;
        const unsigned half = 64 / width;
        if (q) result.lo = old.lo;
        for (unsigned i = 0; i < half; ++i) {
            const auto a = Element(source, width * 2, i);
            SetElement(result, width, i + (q ? half : 0),
                       ShiftRightElement(a, width * 2, 2 * width - imm, true, operation == 17));
        }
    } else if (operation == 20) {
        if (width == 64 || scalar) return false;
        const unsigned half = 64 / width;
        for (unsigned i = 0; i < half; ++i) {
            const auto a = Element(source, width, i + (q ? half : 0));
            SetElement(result, width * 2, i, (u ? a : SignExtend(a, width)) << (imm - width));
        }
    } else {
        if (scalar ? width != 64 : (width == 64 && !q)) return false;
        const unsigned lanes = scalar ? 1 : (q ? 128 : 64) / width;
        for (unsigned i = 0; i < lanes; ++i) {
            const auto a = Element(source, width, i);
            const auto previous = Element(old, width, i);
            uint64_t value;
            switch (operation) {
                case 0:
                case 2:
                case 4:
                case 6:
                    value = ShiftRightElement(a, width, 2 * width - imm, u, (operation & 4) != 0);
                    if (operation & 2) value += previous;
                    break;
                case 8: {
                    if (!u) return false;
                    const unsigned shift = 2 * width - imm;
                    value = ShiftRightElement(a, width, shift, true, false) | (previous & ~Ones(width - shift));
                    break;
                }
                case 10: {
                    const unsigned shift = imm - width;
                    value = (a << shift) | (u ? previous & Ones(shift) : 0);
                    break;
                }
                default:
                    return false;
            }
            SetElement(result, width, i, value);
        }
    }
    state.v[op.rd] = result;
    return true;
}

template <class Registers>
A64_VECTOR_INLINE bool VectorCopy(State& state, const DecodedInstruction& op, bool q, bool u, bool scalar,
                                  const Registers& registers) {
    const auto imm5 = Field(op.word, 16, 5);
    const auto imm4 = Field(op.word, 11, 4);
    if (!imm5 || !(imm5 & 15)) return false;
    const unsigned size = std::countr_zero(imm5);
    const unsigned width = 8u << size;
    const unsigned index = imm5 >> (size + 1);
    const auto source = state.v[op.rn];
    if (scalar) {
        if (u || imm4) return false;
        state.v[op.rd] = {Element(source, width, index), 0};
    } else if (u) {
        if (!q || (imm4 & Ones(size))) return false;
        SetElement(state.v[op.rd], width, index, Element(source, width, imm4 >> size));
    } else {
        switch (imm4) {
            case 0:
                if (!q && width == 64) return false;
                state.v[op.rd] = Splat(Element(source, width, index), width, q);
                break;
            case 1:
                if (index || (!q && width == 64)) return false;
                state.v[op.rd] = Splat(registers.Read(op.rn, width), width, q);
                break;
            case 3:
                if (!q) return false;
                SetElement(state.v[op.rd], width, index, registers.Read(op.rn, width));
                break;
            case 5:
                if (width >= (q ? 64u : 32u)) return false;
                registers.Write(op.rd, q ? 64 : 32, SignExtend(Element(source, width, index), width));
                break;
            case 7:
                if (q != (width == 64)) return false;
                registers.Write(op.rd, q ? 64 : 32, Element(source, width, index));
                break;
            default:
                return false;
        }
    }
    return true;
}

A64_VECTOR_INLINE bool VectorPermute(State& state, const DecodedInstruction& op, bool q, unsigned size) {
    const unsigned width = 8u << size;
    const unsigned lanes = (q ? 128 : 64) / width;
    const unsigned opcode = Field(op.word, 12, 3);
    if (lanes < 2 || opcode == 0 || opcode == 4) return false;
    const auto a = state.v[op.rn], b = state.v[op.rm];
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        switch (size) {
            case 0:
                state.v[op.rd] = SimdePermute<8>(a, b, opcode, q);
                break;
            case 1:
                state.v[op.rd] = SimdePermute<16>(a, b, opcode, q);
                break;
            case 2:
                state.v[op.rd] = SimdePermute<32>(a, b, opcode, q);
                break;
            default:
                state.v[op.rd] = SimdePermute<64>(a, b, opcode, q);
                break;
        }
        return true;
    }
#endif
    const bool second = opcode & 4;
    const unsigned half = lanes / 2;
    Vector128 result;
    for (unsigned i = 0; i < half; ++i) {
        if ((opcode & 3) == 1) {
            SetElement(result, width, i, Element(a, width, i * 2 + second));
            SetElement(result, width, i + half, Element(b, width, i * 2 + second));
        } else {
            const unsigned index = (opcode & 3) == 2 ? i * 2 + second : i + (second ? half : 0);
            SetElement(result, width, i * 2, Element(a, width, index));
            SetElement(result, width, i * 2 + 1, Element(b, width, index));
        }
    }
    state.v[op.rd] = result;
    return true;
}

A64_VECTOR_INLINE bool VectorExtract(State& state, const DecodedInstruction& op, bool q) {
    const auto first = state.v[op.rn], second = state.v[op.rm];
    const unsigned bytes = q ? 16 : 8;
    const unsigned index = Field(op.word, 11, 4);
    if (index >= bytes) return false;
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        state.v[op.rd] = SimdeExtract(first, second, index, q);
        return true;
    }
#endif
    Vector128 result;
    for (unsigned i = 0; i < bytes; ++i) {
        const auto j = i + index;
        SetElement(result, 8, i, j < bytes ? Element(first, 8, j) : Element(second, 8, j - bytes));
    }
    state.v[op.rd] = result;
    return true;
}

A64_VECTOR_INLINE void VectorTable(State& state, const DecodedInstruction& op, bool q) {
    const auto indexes = state.v[op.rm];
    const unsigned length = 1 + Field(op.word, 13, 2);
    const bool keep = Field(op.word, 12, 1);
#if FIBERCPU_AARCH64_SIMDE
    if constexpr (UseSimdeVectors) {
        state.v[op.rd] = SimdeTable(state, op.rd, op.rn, op.rm, length, q, keep);
        return;
    }
#endif
    Vector128 result;
    for (unsigned i = 0; i < (q ? 16u : 8u); ++i) {
        const auto index = unsigned(Element(indexes, 8, i));
        const auto value = index < 16 * length ? Element(state.v[(op.rn + index / 16) & 31], 8, index % 16)
                           : keep              ? Element(state.v[op.rd], 8, i)
                                               : 0;
        SetElement(result, 8, i, value);
    }
    state.v[op.rd] = result;
}

template <bool hot, class Registers>
inline StepResult VectorStructure(State& state, const Memory* memory, const DecodedInstruction& op,
                                  const Registers& registers, const mem::SoftTlb* data_tlb, uint64_t& utlb_tags,
                                  uintptr_t& utlb_addend) {
    const bool q = Field(op.word, 30, 1);
    const uint64_t pc = hot ? 0 : state.pc;
    const bool postindex = Field(op.word, 23, 1);
    const bool load = Field(op.word, 22, 1);
    const bool single = Field(op.word, 24, 1);
    const unsigned size = Field(op.word, 10, 2);
    if ((!postindex && op.rm != 0) || (!single && Field(op.word, 21, 1))) return Undefined(pc);

    unsigned width = 8u << size;
    unsigned count;
    unsigned interleave = 1;
    unsigned index = 0;
    bool replicate = false;
    if (single) {
        const unsigned opcode = Field(op.word, 13, 3);
        const bool s = Field(op.word, 12, 1);
        count = 1 + Field(op.word, 21, 1) + (opcode & 1) * 2;
        switch (opcode >> 1) {
            case 0:
                width = 8;
                index = (q ? 8 : 0) | (s ? 4 : 0) | size;
                break;
            case 1:
                if (size & 1) return Undefined(pc);
                width = 16;
                index = (q ? 4 : 0) | (s ? 2 : 0) | (size >> 1);
                break;
            case 2:
                if ((size & 2) || (size && s)) return Undefined(pc);
                width = size ? 64 : 32;
                index = size ? unsigned(q) : (q ? 2 : 0) | unsigned(s);
                break;
            default:
                if (s || !load) return Undefined(pc);
                replicate = true;
                break;
        }
    } else {
        switch (Field(op.word, 12, 4)) {
            case 0:
                count = interleave = 4;
                break;
            case 2:
                count = 4;
                break;
            case 4:
                count = interleave = 3;
                break;
            case 6:
                count = 3;
                break;
            case 7:
                count = 1;
                break;
            case 8:
                count = interleave = 2;
                break;
            case 10:
                count = 2;
                break;
            default:
                return Undefined(pc);
        }
        if (!q && width == 64 && interleave > 1) return Undefined(pc);
    }

    const auto address = registers.Read(op.rn, 64, true);
    const auto access = load ? Access::Read : Access::Write;
    if (op.rn == 31 && (address & 15)) return Alignment(address, access);

    const unsigned elements = single ? 1 : (q ? 128 : 64) / width;
    const unsigned bytes = width / 8;
    const unsigned total = count * elements * bytes;
    std::array<uint8_t, 64> storage;
    auto* data = storage.data();
    if constexpr (hot) {
        const bool hit = load ? ProbeDataTlb<false>(state, address, total, data_tlb, utlb_tags, utlb_addend, data)
                              : ProbeDataTlb<true>(state, address, total, data_tlb, utlb_tags, utlb_addend, data);
        if (!hit) return {Stop::Replay};
    } else if (load && !ReadMemory(*memory, address, data, total)) {
        return Fault(address, access);
    }
    unsigned at = 0;
    for (unsigned group = 0; group < count; group += interleave) {
        if (load && !single) {
            for (unsigned s = 0; s < interleave; ++s) state.v[(op.rd + group + s) & 31] = {};
        }
        for (unsigned e = 0; e < elements; ++e) {
            for (unsigned s = 0; s < interleave; ++s) {
                auto& reg = state.v[(op.rd + group + s) & 31];
                const unsigned lane = single ? index : e;
                if (load) {
                    const auto value = ReadElement(data + at, width);
                    if (replicate)
                        reg = Splat(value, width, q);
                    else
                        SetElement(reg, width, lane, value);
                } else {
                    WriteElement(data + at, width, Element(reg, width, lane));
                }
                at += bytes;
            }
        }
    }
    if (!load) {
        if constexpr (hot)
            ClearOverlappingExclusive(state, address, total);
        else if (!WriteMemory(state, *memory, address, data, total))
            return Fault(address, access);
    }
    if (postindex) {
        const auto offset = op.rm == 31 ? total : registers.Read(op.rm, 64);
        registers.Write(op.rn, 64, address + offset, true);
    }
    return {};
}

template <VectorFamily family, bool jit = false, class Registers>
inline bool VectorRegisterOp(State& state, const DecodedInstruction& op, const Registers& registers) {
    const auto word = op.word;
    const bool q = Field(word, 30, 1);
    const bool u = Field(word, 29, 1);
    const bool scalar = Field(word, 28, 1);
    const auto size = Field(word, 22, 2);

    bool valid = false;
    if constexpr (family == VectorFamily::Three || family == VectorFamily::ThreeFloat) {
        valid = VectorThree<family == VectorFamily::ThreeFloat, jit>(state, op, q, u, scalar, size);
    } else if constexpr (family == VectorFamily::Widen) {
        valid = VectorWiden(state, op, q, u, size);
    } else if constexpr (family == VectorFamily::Misc || family == VectorFamily::MiscFloat) {
        valid = VectorMisc<family == VectorFamily::MiscFloat, jit>(state, op, q, u, scalar, size);
    } else if constexpr (family == VectorFamily::Reduction) {
        valid = VectorReduction(state, op, q, u, size);
    } else if constexpr (family == VectorFamily::PairAdd) {
        const auto a = state.v[op.rn];
        state.v[op.rd] = {a.lo + a.hi, 0};
        valid = true;
    } else if constexpr (family == VectorFamily::PairFloatAdd) {
        const unsigned width = (size & 1) ? 64 : 32;
        const auto a = state.v[op.rn];
        state.v[op.rd] = {BoundFPArithmetic<jit>(state, width, FPOp::Add, Element(a, width, 0), Element(a, width, 1), 0,
                                                 Nearest, false),
                          0};
        valid = true;
    } else if constexpr (family == VectorFamily::ModifiedImmediate) {
        valid = VectorModifiedImmediate(state, op, q, u);
    } else if constexpr (family == VectorFamily::ShiftImmediate) {
        valid = VectorShiftImmediate(state, op, q, u, scalar);
    } else if constexpr (family == VectorFamily::Copy) {
        valid = VectorCopy(state, op, q, u, scalar, registers);
    } else if constexpr (family == VectorFamily::Extract) {
        valid = VectorExtract(state, op, q);
    } else if constexpr (family == VectorFamily::Table) {
        VectorTable(state, op, q);
        valid = true;
    } else if constexpr (family == VectorFamily::Permute) {
        valid = VectorPermute(state, op, q, size);
    }
    return valid;
}

template <VectorFamily family>
inline StepResult Vector(State& state, const Memory& memory, const DecodedInstruction& op) {
    if constexpr (family == VectorFamily::Structure) {
        uint64_t tags = 0;
        uintptr_t addend = 0;
        return VectorStructure<false>(state, &memory, op, ScalarRegisters{state}, nullptr, tags, addend);
    } else {
        return VectorRegisterOp<family>(state, op, ScalarRegisters{state}) ? StepResult{} : Undefined(state.pc);
    }
}

}  // namespace fiberish::aarch64::detail

#undef A64_VECTOR_INLINE
