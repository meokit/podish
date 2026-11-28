#pragma once

#include "block.h"
#include "integer.h"

namespace fiberish::aarch64::detail {

constexpr uint32_t NopWord = 0xd503201f;

constexpr bool HasExtraOperandSlot(Kind kind, int operation = -1) {
    if (kind == Kind::Address) return operation != 1;
    return HasBlockTarget(kind) || kind == Kind::BranchRegister || kind == Kind::LogicalImmediate ||
           kind == Kind::Bitfield;
}

constexpr bool HasExtraOperandSlot(const DecodedInstruction& op) { return HasExtraOperandSlot(op.kind, op.operation); }

// ADRP adds a signed 21-bit page offset to a 27-bit PC page number.
// Its result can be negative or above the end of the guest address space.
constexpr unsigned AdrpPageBits = 29;
constexpr unsigned AddressPageShift = 6;

inline uint64_t PackOpOperands(const DecodedInstruction& op) {
    if (op.kind == Kind::Syscall || op.kind == Kind::Breakpoint) return op.immediate;
    // The two common immediate forms have room for the sign-extended or
    // scaled byte value. A hot handler never unscales and rescales an offset.
    if (op.kind == Kind::AddSubImmediate)
        return op.immediate | (uint64_t{op.rd} << 24) | (uint64_t{op.rn} << 29) | (uint64_t{op.operation} << 34) |
               (uint64_t{op.width == 64} << 36);

    if (op.kind == Kind::Address) {
        const auto page = (op.target_pc >> 12) & Ones(AdrpPageBits);
        return op.rd | (uint64_t{op.operation} << 5) | (op.operation ? page << AddressPageShift : 0);
    }
    if (op.kind == Kind::MoveWide) {
        return op.rd | ((op.immediate >> op.shift) << 5) | (uint64_t{op.shift / 16u} << 21) |
               (uint64_t{op.operation} << 23) | (uint64_t{op.width == 64} << 25);
    }

    const auto width = std::countr_zero(unsigned(op.width) / 8);
    const auto vector = Field(op.word, 26, 1);
    if (op.kind == Kind::LoadStorePair) {
        return op.rd | (uint64_t{op.rn} << 5) | (uint64_t{op.ra} << 10) | ((op.immediate & 0x7ff) << 15) |
               (uint64_t(width - 2) << 26) | (uint64_t{op.mode} << 28) | (uint64_t{op.operation} << 30) |
               (uint64_t{vector} << 32);
    }
    if (op.kind == Kind::LoadStoreImmediate) {
        return (op.immediate & 0x1ffff) | (uint64_t{op.rd} << 17) | (uint64_t{op.rn} << 22) | (uint64_t(width) << 27) |
               (uint64_t{op.mode} << 30) | (uint64_t{op.operation} << 32) | (uint64_t{vector} << 35);
    }

    const auto aux = op.kind == Kind::Multiply || op.kind == Kind::LoadStoreExclusive ? op.ra : op.shift;
    const bool transfer = op.kind >= Kind::LoadStoreRegister && op.kind <= Kind::LoadLiteral;
    // Bitfield decoding reuses rm for the mask endpoint, which can be 0..63.
    const auto mode = op.kind == Kind::Bitfield ? op.rm >> 5 : op.mode;
    const uint32_t packed = op.rd | (op.rn << 5) | ((op.rm & 31) << 10) | (aux << 15) | (op.operation << 21) |
                            (transfer ? vector << 25 : 0) | (mode << 26) | (uint32_t(width) << 29);
    if (op.kind == Kind::LoadStoreExclusive || op.kind == Kind::LoadStoreAtomic)
        return packed | (uint64_t{Field(op.word, 15, 1)} << 32) | (uint64_t{Field(op.word, 22, 2)} << 33);
    return packed;
}

#if defined(_MSC_VER)
#define A64_OPERANDS_INLINE __forceinline
#else
#define A64_OPERANDS_INLINE inline __attribute__((always_inline))
#endif

template <int operation = -1>
A64_OPERANDS_INLINE uint64_t GetAddressTarget(const DecodedOp& slot) {
    if constexpr (operation == 0) return GetExtraOperands(slot).primary;
    const auto bits = slot.Operands();
    if constexpr (operation < 0) {
        if (!Field(bits, 5, 1)) return GetExtraOperands(slot).primary;
    }
    return SignExtend(bits >> AddressPageShift, AdrpPageBits) << 12;
}

template <Kind kind, int operation = -1>
A64_OPERANDS_INLINE DecodedInstruction GetOpOperands(const DecodedOp& slot) {
    const auto bits = slot.Operands();
    DecodedInstruction op;
    op.kind = kind;
    if constexpr (kind == Kind::Address) {
        op.rd = Field(bits, 0, 5);
        op.operation = operation < 0 ? Field(bits, 5, 1) : operation;
        op.target_pc = GetAddressTarget<operation>(slot);
    } else if constexpr (kind == Kind::MoveWide) {
        op.rd = Field(bits, 0, 5);
        op.shift = Field(bits, 21, 2) * 16;
        op.immediate = uint64_t{Field(bits, 5, 16)} << op.shift;
        op.operation = Field(bits, 23, 2);
        op.width = Field(bits, 25, 1) ? 64 : 32;
    } else if constexpr (kind == Kind::LoadStorePair) {
        op.rd = Field(bits, 0, 5);
        op.rn = Field(bits, 5, 5);
        op.ra = Field(bits, 10, 5);
        op.immediate = SignExtend(Field(bits, 15, 11), 11);
        op.width = 32 << Field(bits, 26, 2);
        op.mode = Field(bits, 28, 2);
        op.operation = Field(bits, 30, 2);
        op.word = ((bits >> 32) & 1) << 26;
    } else if constexpr (kind == Kind::AddSubImmediate) {
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
        if constexpr (kind == Kind::Multiply || kind == Kind::LoadStoreExclusive)
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
        if constexpr (kind == Kind::LoadStoreExclusive || kind == Kind::LoadStoreAtomic)
            op.word = (((bits >> 32) & 1) << 15) | (((bits >> 33) & 3) << 22);
        if constexpr (HasExtraOperandSlot(kind)) {
            const auto& wide = GetExtraOperands(slot);
            if constexpr (HasBlockTarget(kind))
                op.target_pc = wide.primary;
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
