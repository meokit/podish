#pragma once

#include "block.h"
#include "integer.h"

namespace fiberish::aarch64::detail {

constexpr uint32_t NopWord = 0xd503201f;

constexpr bool HasWideOperands(Kind kind) {
    return HasBlockTarget(kind) || kind == Kind::BranchRegister || kind == Kind::LogicalImmediate ||
           kind == Kind::MoveWide || kind == Kind::Bitfield || kind == Kind::LoadStorePair;
}

inline uint64_t PackOperands(const DecodedInstruction& op) {
    // The two common immediate forms have room for the sign-extended or
    // scaled byte value. A hot handler never unscales and rescales an offset.
    if (op.kind == Kind::AddSubImmediate)
        return op.immediate | (uint64_t{op.rd} << 24) | (uint64_t{op.rn} << 29) | (uint64_t{op.operation} << 34) |
               (uint64_t{op.width == 64} << 36);

    const auto width = std::countr_zero(unsigned(op.width) / 8);
    const auto vector = Field(op.word, 26, 1);
    if (op.kind == Kind::LoadStoreImmediate) {
        return (op.immediate & 0x1ffff) | (uint64_t{op.rd} << 17) | (uint64_t{op.rn} << 22) | (uint64_t(width) << 27) |
               (uint64_t{op.mode} << 30) | (uint64_t{op.operation} << 32) | (uint64_t{vector} << 35);
    }

    const auto aux = op.kind == Kind::Multiply || op.kind == Kind::LoadStorePair ? op.ra : op.shift;
    const bool transfer = op.kind >= Kind::LoadStoreRegister && op.kind <= Kind::LoadLiteral;
    // Bitfield decoding reuses rm for the mask endpoint, which can be 0..63.
    const auto mode = op.kind == Kind::Bitfield ? op.rm >> 5 : op.mode;
    return op.rd | (op.rn << 5) | ((op.rm & 31) << 10) | (aux << 15) | (op.operation << 21) |
           (transfer ? vector << 25 : 0) | (mode << 26) | (uint32_t(width) << 29);
}

#if defined(_MSC_VER)
#define A64_OPERANDS_INLINE __forceinline
#else
#define A64_OPERANDS_INLINE inline __attribute__((always_inline))
#endif

template <Kind kind>
A64_OPERANDS_INLINE DecodedInstruction SlotOperands(const DispatchSlot& slot) {
    const auto bits = slot.Operands();
    DecodedInstruction op;
    op.kind = kind;
    if constexpr (kind == Kind::AddSubImmediate) {
        op.immediate = bits & 0xffffff;
        op.rd = (bits >> 24) & 31;
        op.rn = (bits >> 29) & 31;
        op.operation = (bits >> 34) & 3;
        op.width = (bits >> 36) & 1 ? 64 : 32;
    } else if constexpr (kind == Kind::LoadStoreImmediate) {
        op.immediate = SignExtend(bits & 0x1ffff, 17);
        op.rd = (bits >> 17) & 31;
        op.rn = (bits >> 22) & 31;
        op.width = 8 << ((bits >> 27) & 7);
        op.mode = (bits >> 30) & 3;
        op.operation = (bits >> 32) & 7;
        op.word = ((bits >> 35) & 1) << 26;
    } else {
        op.rd = Field(bits, 0, 5);
        op.rn = Field(bits, 5, 5);
        constexpr bool transfer = kind >= Kind::LoadStoreRegister && kind <= Kind::LoadLiteral;
        op.rm = Field(bits, 10, 5);
        if constexpr (kind == Kind::Multiply || kind == Kind::LoadStorePair)
            op.ra = Field(bits, 15, 5);
        else
            op.shift = Field(bits, 15, 6);
        op.operation = Field(bits, 21, transfer ? 4 : 5);
        if constexpr (kind == Kind::Bitfield)
            op.rm |= Field(bits, 26, 1) << 5;
        else
            op.mode = Field(bits, 26, 3);
        op.width = 8 << Field(bits, 29, 3);
        if constexpr (transfer) op.word = Field(bits, 25, 1) << 26;
        if constexpr (HasWideOperands(kind)) {
            const auto& wide = WideOperands(slot);
            if constexpr (HasBlockTarget(kind))
                op.pc_target = wide.primary;
            else
                op.immediate = wide.primary;
            if constexpr (kind == Kind::Bitfield) op.mask = wide.secondary;
        } else if constexpr (kind == Kind::ConditionalCompare) {
            op.immediate = op.rd & 15;
        }
    }
    return op;
}

#undef A64_OPERANDS_INLINE

}  // namespace fiberish::aarch64::detail
