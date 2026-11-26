#pragma once

#include <bit>

#include "decoder.h"

#ifndef FIBERCPU_AARCH64_SIMDE
#if defined(__aarch64__) || defined(__arm__) || defined(_M_ARM64) || defined(_M_ARM64EC) || defined(_M_ARM)
#define FIBERCPU_AARCH64_SIMDE 0
#else
#define FIBERCPU_AARCH64_SIMDE 1
#endif
#endif

namespace fiberish::aarch64::detail {

enum class FloatFamily {
    Undefined,
    Convert,
    Fused,
    Compare,
    ConditionalCompare,
    Select,
    Binary,
    Immediate,
    Unary,
    MoveFromVector,
    MoveToVector,
    Integer
};

constexpr FloatFamily DecodeFloatFamily(uint32_t word) {
    const auto type = Field(word, 22, 2);
    if ((word & 0xff207c00) == 0x1e204000) {
        const auto operation = Field(word, 15, 6);
        if (operation == 4 || operation == 5 || operation == 7) return FloatFamily::Convert;
    }
    if (type == 3) return FloatFamily::Undefined;  // FEAT_FP16 arithmetic.
    if (type <= 1) {
        if ((word & 0xff000000) == 0x1f000000) return FloatFamily::Fused;
        if ((word & 0xff20fc07) == 0x1e202000) return FloatFamily::Compare;
        if ((word & 0xff200c00) == 0x1e200400) return FloatFamily::ConditionalCompare;
        if ((word & 0xff200c00) == 0x1e200c00) return FloatFamily::Select;
        if ((word & 0xff200c00) == 0x1e200800) return FloatFamily::Binary;
        if ((word & 0xff201fe0) == 0x1e201000) return FloatFamily::Immediate;
        if ((word & 0xff207c00) == 0x1e204000) return FloatFamily::Unary;
    }
    const auto move = word & 0xfffffc00;
    if (move == 0x1e260000 || move == 0x9e660000) return FloatFamily::MoveFromVector;
    if (move == 0x1e270000 || move == 0x9e670000) return FloatFamily::MoveToVector;
    return FloatFamily::Integer;
}

enum class VectorFamily {
    Undefined,
    Structure,
    Three,
    Widen,
    Misc,
    Reduction,
    PairAdd,
    PairFloatAdd,
    ModifiedImmediate,
    ShiftImmediate,
    Copy,
    Extract,
    Table,
    Permute,
    ThreeFloat,
    MiscFloat
};

constexpr VectorFamily DecodeVectorFamily(uint32_t word) {
    if ((word & 0xbe000000) == 0x0c000000) return VectorFamily::Structure;
    if ((word & 0x8f200400) == 0x0e200400)
        return Field(word, 11, 5) >= 24 ? VectorFamily::ThreeFloat : VectorFamily::Three;
    if ((word & 0x9f200c00) == 0x0e200000) return VectorFamily::Widen;
    if ((word & 0x8f3e0c00) == 0x0e200800) {
        switch (Field(word, 12, 5)) {
            case 15:
            case 27:
            case 29:
            case 31:
                return VectorFamily::MiscFloat;
            default:
                return VectorFamily::Misc;
        }
    }
    if ((word & 0x9f3e0c00) == 0x0e300800) return VectorFamily::Reduction;
    if ((word & 0xfffffc00) == 0x5ef1b800) return VectorFamily::PairAdd;
    if ((word & 0xffbffc00) == 0x7e30d800) return VectorFamily::PairFloatAdd;
    if ((word & 0x9ff80400) == 0x0f000400) return VectorFamily::ModifiedImmediate;
    if ((word & 0x8f800400) == 0x0f000400) return VectorFamily::ShiftImmediate;
    if ((word & 0x8fe08400) == 0x0e000400) return VectorFamily::Copy;
    if ((word & 0xbfe08400) == 0x2e000000) return VectorFamily::Extract;
    if ((word & 0xbfe08c00) == 0x0e000000) return VectorFamily::Table;
    if ((word & 0xbf208c00) == 0x0e000800) return VectorFamily::Permute;
    return VectorFamily::Undefined;
}

enum class ByteVectorOp { PairMin, PairMax, Equal, EqualZero, None };

constexpr ByteVectorOp DecodeByteVectorOp(uint32_t word, VectorFamily family) {
#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
    if constexpr (std::endian::native == std::endian::little) {
        if (family == VectorFamily::Three) {
            switch (word & 0xffe0fc00) {
                case 0x6e20ac00:
                    return ByteVectorOp::PairMin;
                case 0x6e20a400:
                    return ByteVectorOp::PairMax;
                case 0x6e208c00:
                    return ByteVectorOp::Equal;
            }
        }
        if (family == VectorFamily::Misc && (word & 0xfffffc00) == 0x0e209800) return ByteVectorOp::EqualZero;
    }
#endif
    return ByteVectorOp::None;
}

}  // namespace fiberish::aarch64::detail
