#pragma once

#include <cmath>
#include <initializer_list>

#include "memory.h"
#include "register_decoder.h"

#if FIBERCPU_AARCH64_SOFTFLOAT
#include "softfloat_jit_helpers.h"
extern "C" {
#include "../../3rdparty/softfloat/softfloat.h"
}
#else
#include <cfenv>
#if defined(__clang__)
#pragma STDC FENV_ACCESS ON
#endif
#endif

namespace fiberish::aarch64::detail {

enum FPRound : unsigned { Nearest, PlusInf, MinusInf, TowardZero, NearestAway };
enum : uint32_t {
    FPInvalid = 1,
    FPDivZero = 2,
    FPOverflow = 4,
    FPUnderflow = 8,
    FPInexact = 16,
    FPInputDenormal = 128
};
enum class FPOp : uint8_t { Mul, Div, Add, Sub, Max, Min, MaxNumber, MinNumber, NegMul, Sqrt, Fused, Round };

struct FPLayout {
    unsigned fraction_bits;
    unsigned exponent_bits;
    int bias;
    uint64_t sign;
    uint64_t exponent;
    uint64_t fraction;
    uint64_t quiet;
};

A64_FORCE_INLINE constexpr FPLayout FPBits(unsigned width) {
    const unsigned fraction = width == 16 ? 10 : width == 32 ? 23 : 52;
    const unsigned exponent = width - fraction - 1;
    return {fraction,
            exponent,
            int(Ones(exponent - 1)),
            uint64_t{1} << (width - 1),
            Ones(exponent) << fraction,
            Ones(fraction),
            uint64_t{1} << (fraction - 1)};
}

A64_FORCE_INLINE bool FPNaN(uint64_t bits, FPLayout f) {
    return (bits & f.exponent) == f.exponent && (bits & f.fraction) != 0;
}

A64_FORCE_INLINE bool FPSignalingNaN(uint64_t bits, FPLayout f) { return FPNaN(bits, f) && !(bits & f.quiet); }

A64_FORCE_INLINE uint64_t FPInput(State& state, uint64_t bits, FPLayout f) {
    if ((state.fpcr & (1u << 24)) && !(bits & f.exponent) && (bits & f.fraction)) {
        state.fpsr |= FPInputDenormal;
        return bits & f.sign;
    }
    return bits;
}

inline uint64_t FPResult(State& state, uint64_t bits, FPLayout f) {
    if (FPNaN(bits, f)) return f.exponent | f.quiet;
    bool tiny = !(bits & f.exponent) && (bits & f.fraction);
#if FIBERCPU_AARCH64_SOFTFLOAT
    tiny |= (softfloat_exceptionFlags & softfloat_flag_underflow) != 0;
#endif
    if ((state.fpcr & (1u << 24)) && tiny) {
        state.fpsr |= FPUnderflow;
        return bits & f.sign;
    }
    return bits;
}

inline uint64_t FPQuietNaN(State& state, uint64_t bits, FPLayout f) {
    if (state.fpcr & (1u << 25)) return f.exponent | f.quiet;
    return bits | f.quiet;
}

inline bool FPHandleNaNs(State& state, FPLayout f, std::initializer_list<uint64_t> inputs, uint64_t& result) {
    for (const auto bits : inputs) {
        if (FPSignalingNaN(bits, f)) {
            state.fpsr |= FPInvalid;
            result = FPQuietNaN(state, bits, f);
            return true;
        }
    }
    for (const auto bits : inputs) {
        if (FPNaN(bits, f)) {
            result = FPQuietNaN(state, bits, f);
            return true;
        }
    }
    return false;
}

A64_FORCE_INLINE int FPCompare(uint64_t left, uint64_t right, FPLayout f) {
    if (left == right || ((left | right) & ~f.sign) == 0) return 0;
    const bool negative = left & f.sign;
    if ((left ^ right) & f.sign) return negative ? -1 : 1;
    return negative == (left > right) ? -1 : 1;
}

#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline uint32_t FPCompareFlags(State& state, uint64_t left, uint64_t right, unsigned width, bool signaling) {
    const auto f = FPBits(width);
    left = FPInput(state, left, f);
    right = FPInput(state, right, f);
    if (FPNaN(left, f) || FPNaN(right, f)) {
        if (signaling || FPSignalingNaN(left, f) || FPSignalingNaN(right, f)) state.fpsr |= FPInvalid;
        return Carry | Overflow;
    }
    const auto cmp = FPCompare(left, right, f);
    return cmp < 0 ? Negative : cmp == 0 ? Zero | Carry : Carry;
}

#if FIBERCPU_AARCH64_SOFTFLOAT

// Wasm has no dynamic rounding mode or floating-point exception flags.
class FPEnvironment {
    State& state;
    uint_fast8_t saved_round = softfloat_roundingMode;
    uint_fast8_t saved_tininess = softfloat_detectTininess;
    uint_fast8_t saved_flags = softfloat_exceptionFlags;

public:
    FPEnvironment(State& state, FPRound mode) : state(state) {
        constexpr std::array<uint_fast8_t, 5> Modes{softfloat_round_near_even, softfloat_round_max, softfloat_round_min,
                                                    softfloat_round_minMag, softfloat_round_near_maxMag};
        softfloat_roundingMode = Modes[mode];
        softfloat_detectTininess = softfloat_tininess_beforeRounding;
        softfloat_exceptionFlags = 0;
    }
    ~FPEnvironment() {
        const auto flags = softfloat_exceptionFlags;
        const bool flushed = (state.fpcr & (1u << 24)) && (flags & softfloat_flag_underflow);
        state.fpsr |= ((flags & softfloat_flag_invalid) ? FPInvalid : 0u) |
                      ((flags & softfloat_flag_infinite) ? FPDivZero : 0u) |
                      ((flags & softfloat_flag_overflow) ? FPOverflow : 0u) |
                      ((flags & softfloat_flag_underflow) ? FPUnderflow : 0u) |
                      ((flags & softfloat_flag_inexact) && !flushed ? FPInexact : 0u);
        softfloat_roundingMode = saved_round;
        softfloat_detectTininess = saved_tininess;
        softfloat_exceptionFlags = saved_flags;
    }
};

inline uint64_t FPCompute(unsigned width, FPOp op, uint64_t a, uint64_t b, uint64_t c, FPRound, bool exact) {
    if (width == 32) {
        const float32_t left{uint32_t(a)}, right{uint32_t(b)}, addend{uint32_t(c)};
        switch (op) {
            case FPOp::Mul:
            case FPOp::NegMul:
                return f32_mul(left, right).v;
            case FPOp::Div:
                return f32_div(left, right).v;
            case FPOp::Add:
                return f32_add(left, right).v;
            case FPOp::Sub:
                return f32_sub(left, right).v;
            case FPOp::Sqrt:
                return f32_sqrt(left).v;
            case FPOp::Fused:
                return f32_mulAdd(left, right, addend).v;
            default:
                return f32_roundToInt(left, softfloat_roundingMode, exact).v;
        }
    }
    const float64_t left{a}, right{b}, addend{c};
    switch (op) {
        case FPOp::Mul:
        case FPOp::NegMul:
            return f64_mul(left, right).v;
        case FPOp::Div:
            return f64_div(left, right).v;
        case FPOp::Add:
            return f64_add(left, right).v;
        case FPOp::Sub:
            return f64_sub(left, right).v;
        case FPOp::Sqrt:
            return f64_sqrt(left).v;
        case FPOp::Fused:
            return f64_mulAdd(left, right, addend).v;
        default:
            return f64_roundToInt(left, softfloat_roundingMode, exact).v;
    }
}

#else

class FPEnvironment {
    State& state;
    std::fenv_t saved{};

public:
    FPEnvironment(State& state, FPRound mode) : state(state) {
        constexpr std::array<int, 5> Modes{FE_TONEAREST, FE_UPWARD, FE_DOWNWARD, FE_TOWARDZERO, FE_TONEAREST};
        std::feholdexcept(&saved);
        std::fesetround(Modes[mode]);
#if defined(__aarch64__) && !defined(_MSC_VER)
        uint64_t control;
        asm volatile("mrs %0, fpcr" : "=r"(control));
        control = (control & ~(uint64_t{1} << 24)) | (state.fpcr & (1u << 24));
        asm volatile("msr fpcr, %0" ::"r"(control) : "memory");
#endif
    }
    ~FPEnvironment() {
        const auto flags = std::fetestexcept(FE_ALL_EXCEPT);
        const bool flushed = (state.fpcr & (1u << 24)) && (flags & FE_UNDERFLOW);
        state.fpsr |= ((flags & FE_INVALID) ? FPInvalid : 0u) | ((flags & FE_DIVBYZERO) ? FPDivZero : 0u) |
                      ((flags & FE_OVERFLOW) ? FPOverflow : 0u) | ((flags & FE_UNDERFLOW) ? FPUnderflow : 0u) |
                      ((flags & FE_INEXACT) && !flushed ? FPInexact : 0u);
        std::fesetenv(&saved);
    }
};

template <class F, class U>
inline uint64_t NativeFPCompute(FPOp op, U a, U b, U c, FPRound round, bool exact) {
    const auto left = std::bit_cast<F>(a), right = std::bit_cast<F>(b), addend = std::bit_cast<F>(c);
    F result;
    switch (op) {
        case FPOp::Mul:
        case FPOp::NegMul:
            result = left * right;
            break;
        case FPOp::Div:
            result = left / right;
            break;
        case FPOp::Add:
            result = left + right;
            break;
        case FPOp::Sub:
            result = left - right;
            break;
        case FPOp::Sqrt:
            result = std::sqrt(left);
            break;
        case FPOp::Fused:
            result = std::fma(left, right, addend);
            break;
        default:
            result = round == NearestAway ? std::round(left) : exact ? std::rint(left) : std::nearbyint(left);
            break;
    }
    return std::bit_cast<U>(result);
}

inline uint64_t FPCompute(unsigned width, FPOp op, uint64_t a, uint64_t b, uint64_t c, FPRound round, bool exact) {
    return width == 32 ? NativeFPCompute<float>(op, uint32_t(a), uint32_t(b), uint32_t(c), round, exact)
                       : NativeFPCompute<double>(op, a, b, c, round, exact);
}

#endif

A64_FORCE_INLINE FPRound FPRounding(const State& state) { return FPRound((state.fpcr >> 22) & 3); }

inline uint64_t FPArithmetic(State& state, unsigned width, FPOp op, uint64_t a, uint64_t b = 0, uint64_t c = 0,
                             FPRound direction = Nearest, bool exact = false) {
    const auto f = FPBits(width);
    a = FPInput(state, a, f);
    b = FPInput(state, b, f);
    uint64_t result;
    if (op >= FPOp::Max && op <= FPOp::MinNumber) {
        if (op >= FPOp::MaxNumber) {
            if (FPNaN(a, f) && !FPSignalingNaN(a, f) && !FPNaN(b, f)) return b;
            if (FPNaN(b, f) && !FPSignalingNaN(b, f) && !FPNaN(a, f)) return a;
        }
        if (FPHandleNaNs(state, f, {a, b}, result)) return result;
        const bool maximum = op == FPOp::Max || op == FPOp::MaxNumber;
        const auto cmp = FPCompare(a, b, f);
        if (cmp == 0) return maximum ? a & b : a | b;
        return maximum == (cmp > 0) ? a : b;
    }
    if (op == FPOp::Fused) {
        c = FPInput(state, c, f);
        // Fused operations give the addend first priority when propagating NaNs.
        const bool invalid_product =
            (((a & ~f.sign) == f.exponent) && !(b & ~f.sign)) || (((b & ~f.sign) == f.exponent) && !(a & ~f.sign));
        if (invalid_product && FPNaN(c, f) && !FPSignalingNaN(c, f)) {
            state.fpsr |= FPInvalid;
            return f.exponent | f.quiet;
        }
        if (FPHandleNaNs(state, f, {c, a, b}, result)) return result;
    } else if (FPHandleNaNs(state, f, {a, b}, result)) {
        return op == FPOp::NegMul ? result ^ f.sign : result;
    }
    const auto rounding = op == FPOp::Round ? direction : FPRounding(state);
    const FPEnvironment environment(state, rounding);
    result = FPCompute(width, op, a, b, c, rounding, exact);
    result = FPResult(state, result, f);
    return op == FPOp::NegMul ? result ^ f.sign : result;
}

inline uint64_t NarrowHalf(State& state, uint64_t bits, FPLayout from, bool alternative) {
    const bool negative = bits & from.sign;
    const uint64_t sign = negative ? 0x8000 : 0;
    if (!(bits & ~from.sign)) return sign;

    const auto encoded_exponent = unsigned((bits & from.exponent) >> from.fraction_bits);
    int exponent = int(encoded_exponent ? encoded_exponent : 1) - from.bias;
    const auto significand = (bits & from.fraction) | (encoded_exponent ? uint64_t{1} << from.fraction_bits : 0);
    const auto rounding = FPRounding(state);
    const bool away = rounding == (negative ? MinusInf : PlusInf);
    const bool tiny = exponent < -14;
    const unsigned shift = from.fraction_bits - 10 + (tiny ? -14 - exponent : 0);
    auto significand16 = shift < 64 ? significand >> shift : 0;
    const auto remainder = shift < 64 ? significand & Ones(shift) : significand;
    const auto halfway = shift <= 64 ? uint64_t{1} << (shift - 1) : UINT64_MAX;
    if (remainder &&
        (away || (rounding == Nearest && (remainder > halfway || (remainder == halfway && (significand16 & 1))))))
        ++significand16;
    if (significand16 == 0x800) {
        significand16 >>= 1;
        ++exponent;
    }

    if (exponent > (alternative ? 16 : 15)) {
        if (alternative) {
            state.fpsr |= FPInvalid;
            return sign | 0x7fff;
        }
        state.fpsr |= FPOverflow | FPInexact;
        return sign | ((rounding == Nearest || away) ? 0x7c00 : 0x7bff);
    }
    if (remainder) state.fpsr |= FPInexact | (tiny ? FPUnderflow : 0u);
    if (tiny) return sign | significand16;
    return sign | (uint64_t(exponent + 15) << 10) | (significand16 & 0x3ff);
}

inline uint64_t WidenHalf(uint64_t bits, FPLayout to) {
    const uint64_t sign = (bits & 0x8000) ? to.sign : 0;
    unsigned exponent = (bits >> 10) & 31;
    auto significand = bits & 0x3ff;
    if (!exponent && !significand) return sign;
    int adjusted_exponent = int(exponent ? exponent : 1) + to.bias - 15;
    if (!exponent) {
        const unsigned shift = 11 - std::bit_width(significand);
        significand <<= shift;
        adjusted_exponent -= int(shift);
    }
    return sign | (uint64_t(adjusted_exponent) << to.fraction_bits) |
           ((significand & 0x3ff) << (to.fraction_bits - 10));
}

inline uint64_t FPConvert(State& state, unsigned source, unsigned dest, uint64_t bits) {
    const auto from = FPBits(source);
    const auto to = FPBits(dest);
    const bool alternative = (state.fpcr & (1u << 26)) != 0;
    const bool alternative_source = source == 16 && alternative;
    const bool alternative_dest = dest == 16 && alternative;
    if (source != 16) bits = FPInput(state, bits, from);
    const auto sign = (bits & from.sign) ? to.sign : 0;
    if (!alternative_source && FPNaN(bits, from)) {
        if (alternative_dest) {
            state.fpsr |= FPInvalid;
            return sign;
        }
        if (FPSignalingNaN(bits, from)) state.fpsr |= FPInvalid;
        if (state.fpcr & (1u << 25)) return to.exponent | to.quiet;
        const auto fraction = bits & from.fraction;
        const auto payload = source < dest ? fraction << (to.fraction_bits - from.fraction_bits)
                                           : fraction >> (from.fraction_bits - to.fraction_bits);
        return sign | to.exponent | to.quiet | payload;
    }
    if (!alternative_source && (bits & from.exponent) == from.exponent) {
        if (alternative_dest) {
            state.fpsr |= FPInvalid;
            return sign | 0x7fff;
        }
        return sign | to.exponent;
    }
    if (source == 16) return WidenHalf(bits, to);
    if (dest == 16) return NarrowHalf(state, bits, from, alternative);
    const FPEnvironment environment(state, FPRounding(state));
#if FIBERCPU_AARCH64_SOFTFLOAT
    const auto result = dest == 64 ? f32_to_f64({uint32_t(bits)}).v : f64_to_f32({bits}).v;
#else
    const auto result = dest == 64 ? std::bit_cast<uint64_t>(double(std::bit_cast<float>(uint32_t(bits))))
                                   : std::bit_cast<uint32_t>(float(std::bit_cast<double>(bits)));
#endif
    return FPResult(state, result, to);
}

inline uint64_t IntToFP(State& state, unsigned width, uint64_t bits, unsigned integer_width, bool is_unsigned,
                        unsigned scale) {
    const FPEnvironment environment(state, FPRounding(state));
    uint64_t result;
#if FIBERCPU_AARCH64_SOFTFLOAT
    if (width == 32)
        result = is_unsigned ? ui64_to_f32(bits).v : i64_to_f32(Signed(bits, integer_width)).v;
    else
        result = is_unsigned ? ui64_to_f64(bits).v : i64_to_f64(Signed(bits, integer_width)).v;
#else
    if (width == 32)
        result = std::bit_cast<uint32_t>(is_unsigned ? float(bits) : float(Signed(bits, integer_width)));
    else
        result = std::bit_cast<uint64_t>(is_unsigned ? double(bits) : double(Signed(bits, integer_width)));
#endif
    // Scaling by a power of two is exact and remains in the normal range.
    if (result) result -= uint64_t(scale) << FPBits(width).fraction_bits;
    return result;
}

// Lowered SoftFloat calls resolve to native C symbols so the interpreter
// and compiled code share the same thread-local floating-point environment.
template <bool jit>
inline constexpr auto BoundFPArithmetic =
#if FIBERCPU_AARCH64_SOFTFLOAT
    jit ? &FiberArmSoftFloatArithmetic :
#endif
        &FPArithmetic;

template <bool jit>
inline constexpr auto BoundFPConvert =
#if FIBERCPU_AARCH64_SOFTFLOAT
    jit ? &FiberArmSoftFloatConvert :
#endif
        &FPConvert;

template <bool jit>
inline constexpr auto BoundIntToFP =
#if FIBERCPU_AARCH64_SOFTFLOAT
    jit ? &FiberArmSoftFloatIntToFP :
#endif
        &IntToFP;

inline uint64_t FPToInt(State& state, unsigned width, uint64_t bits, unsigned integer_width, bool is_unsigned,
                        unsigned scale, FPRound round) {
    const auto f = FPBits(width);
    bits = FPInput(state, bits, f);
    if (FPNaN(bits, f)) {
        state.fpsr |= FPInvalid;
        return 0;
    }
    const bool negative = bits & f.sign;
    const auto exponent = unsigned((bits & f.exponent) >> f.fraction_bits);
    const auto significand = (bits & f.fraction) | (exponent ? uint64_t{1} << f.fraction_bits : 0);
    const int shift = int(exponent ? exponent : 1) - f.bias - int(f.fraction_bits) + int(scale);
    uint64_t magnitude = 0;
    bool overflow = exponent == Ones(f.exponent_bits);
    bool inexact = false;
    if (significand && !overflow) {
        if (shift >= 0) {
            overflow = shift >= 64 || significand > (UINT64_MAX >> shift);
            if (!overflow) magnitude = significand << shift;
        } else {
            const unsigned right = -shift;
            magnitude = right >= 64 ? 0 : significand >> right;
            const auto remainder = right >= 64 ? significand : significand & Ones(right);
            const auto half = right > 64 ? UINT64_MAX : uint64_t{1} << (right - 1);
            inexact = remainder != 0;
            if (inexact && ((round == PlusInf && !negative) || (round == MinusInf && negative) ||
                            ((round == Nearest || round == NearestAway) &&
                             (remainder > half || (remainder == half && (round == NearestAway || (magnitude & 1)))))))
                ++magnitude;
        }
    }
    const auto sign = uint64_t{1} << (integer_width - 1);
    const auto limit = is_unsigned ? (negative ? 0 : Ones(integer_width)) : (negative ? sign : sign - 1);
    if (overflow || magnitude > limit) {
        state.fpsr |= FPInvalid;
        magnitude = limit;
    } else if (inexact) {
        state.fpsr |= FPInexact;
    }
    return negative ? uint64_t{0} - magnitude : magnitude;
}

A64_FORCE_INLINE constexpr uint64_t FPImmediate(unsigned width, unsigned b) {
    const auto f = FPBits(width);
    const auto top = (b >> 6) & 1;
    const auto exponent =
        (uint64_t(top ^ 1) << (f.exponent_bits - 1)) | (top ? Ones(f.exponent_bits - 3) << 2 : 0) | ((b >> 4) & 3);
    return (b & 128 ? f.sign : 0) | (exponent << f.fraction_bits) | (uint64_t(b & 15) << (f.fraction_bits - 4));
}

A64_FORCE_INLINE void WriteFloatResult(State& state, unsigned rd, uint64_t value) { state.v[rd] = {value, 0}; }

template <FloatFamily family, bool jit = false, class Registers>
inline bool FloatRegisterOp(State& state, const DecodedInstruction& op, uint32_t& flags, const Registers& registers) {
    const auto word = op.word;
    const auto type = Field(word, 22, 2);
    const unsigned width = type == 0 ? 32 : type == 3 ? 16 : 64;
    const auto f = FPBits(width);
    const auto left = state.v[op.rn].lo & Ones(width);
    const auto right = state.v[op.rm].lo & Ones(width);

    if constexpr (family == FloatFamily::Convert) {
        const auto operation = Field(word, 15, 6);
        if (type == 2 || operation - 4 == type) return false;
        const unsigned dest = operation == 4 ? 32 : operation == 5 ? 64 : 16;
        WriteFloatResult(state, op.rd, BoundFPConvert<jit>(state, width, dest, left));
        return true;
    }
    if constexpr (family == FloatFamily::Fused) {
        const bool negate_addend = Field(word, 21, 1);
        const bool negate_product = negate_addend != bool(Field(word, 15, 1));
        const auto addend = (state.v[op.ra].lo & Ones(width)) ^ (negate_addend ? f.sign : 0);
        WriteFloatResult(state, op.rd,
                         BoundFPArithmetic<jit>(state, width, FPOp::Fused, left ^ (negate_product ? f.sign : 0), right,
                                                addend, Nearest, false));
        return true;
    }
    if constexpr (family == FloatFamily::Compare) {
        const bool zero = Field(word, 3, 1);
        if (zero && op.rm) return false;
        flags = FPCompareFlags(state, left, zero ? 0 : right, width, Field(word, 4, 1));
        return true;
    }
    if constexpr (family == FloatFamily::ConditionalCompare) {
        flags = ConditionHolds(flags, Field(word, 12, 4)) ? FPCompareFlags(state, left, right, width, Field(word, 4, 1))
                                                          : Field(word, 0, 4) << 28;
        return true;
    }
    if constexpr (family == FloatFamily::Select) {
        WriteFloatResult(state, op.rd, ConditionHolds(flags, Field(word, 12, 4)) ? left : right);
        return true;
    }
    if constexpr (family == FloatFamily::Binary) {
        const auto operation = Field(word, 12, 4);
        if (operation > unsigned(FPOp::NegMul)) return false;
        WriteFloatResult(state, op.rd,
                         BoundFPArithmetic<jit>(state, width, FPOp(operation), left, right, 0, Nearest, false));
        return true;
    }
    if constexpr (family == FloatFamily::Immediate) {
        WriteFloatResult(state, op.rd, FPImmediate(width, Field(word, 13, 8)));
        return true;
    }
    if constexpr (family == FloatFamily::Unary) {
        const auto operation = Field(word, 15, 6);
        switch (operation) {
            case 0:
                WriteFloatResult(state, op.rd, left);
                return true;
            case 1:
                WriteFloatResult(state, op.rd, left & ~f.sign);
                return true;
            case 2:
                WriteFloatResult(state, op.rd, left ^ f.sign);
                return true;
            case 3:
                WriteFloatResult(state, op.rd,
                                 BoundFPArithmetic<jit>(state, width, FPOp::Sqrt, left, 0, 0, Nearest, false));
                return true;
            case 8:
            case 9:
            case 10:
            case 11:
            case 12:
            case 14:
            case 15:
                WriteFloatResult(state, op.rd,
                                 BoundFPArithmetic<jit>(state, width, FPOp::Round, left, 0, 0,
                                                        operation < 13 ? FPRound(operation - 8) : FPRounding(state),
                                                        operation == 14));
                return true;
            default:
                return false;
        }
    }

    if constexpr (family == FloatFamily::MoveFromVector) {
        registers.Write(op.rd, width, left);
        return true;
    }
    if constexpr (family == FloatFamily::MoveToVector) {
        WriteFloatResult(state, op.rd, registers.Read(op.rn, width));
        return true;
    }

    const bool fixed = (word & 0x7f200000) == 0x1e000000;
    if (!fixed && (word & 0x7f20fc00) != 0x1e200000) return false;
    const auto integer_width = op.width;
    const auto operation = Field(word, 16, 5);
    if (type == 2) {
        if (fixed || integer_width != 64) return false;
        if (operation == 14)
            registers.Write(op.rd, 64, state.v[op.rn].hi);
        else if (operation == 15)
            state.v[op.rd].hi = registers.Read(op.rn, 64);
        else
            return false;
        return true;
    }
    const unsigned scale = fixed ? 64 - Field(word, 10, 6) : 0;
    if (scale > integer_width) return false;
    if (operation == 2 || operation == 3) {
        WriteFloatResult(state, op.rd,
                         BoundIntToFP<jit>(state, width, registers.Read(op.rn, integer_width), integer_width,
                                           operation == 3, scale));
    } else if (!fixed && integer_width == width && (operation == 6 || operation == 7)) {
        if (operation == 6)
            registers.Write(op.rd, integer_width, left);
        else
            WriteFloatResult(state, op.rd, registers.Read(op.rn, integer_width));
    } else {
        const auto mode = operation >> 3;
        const auto conversion = operation & 7;
        if ((fixed && mode != TowardZero) || (conversion > 1 && (operation != 4 && operation != 5))) return false;
        const auto round = conversion >= 4 ? NearestAway : FPRound(mode);
        registers.Write(op.rd, integer_width, FPToInt(state, width, left, integer_width, operation & 1, scale, round));
    }
    return true;
}

}  // namespace fiberish::aarch64::detail
