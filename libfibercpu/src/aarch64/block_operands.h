#pragma once

#include "block.h"
#include "integer.h"

namespace fiberish::aarch64::detail {

constexpr uint32_t NopWord = 0xd503201f;

constexpr bool HasBranchTarget(Kind kind) { return kind == Kind::BranchImmediate || IsConditionalBranch(kind); }

constexpr unsigned LogicalImmediateShift = 13;
constexpr unsigned LogicalImmediateBits = DecodedOp::SpanShift - LogicalImmediateShift;

constexpr bool UsesInlineLogical64(const DecodedInstruction& op) {
    return op.kind == Kind::LogicalImmediate && op.width == 64 &&
           SignExtend(op.immediate & Ones(LogicalImmediateBits), LogicalImmediateBits) == op.immediate;
}

constexpr bool HasExtraOperandSlot(Kind kind) { return HasBranchTarget(kind) || kind == Kind::BranchRegister; }

constexpr bool HasExtraOperandSlot(const DecodedInstruction& op) {
    return HasExtraOperandSlot(op.kind) ||
           (op.kind == Kind::LogicalImmediate && op.width == 64 && !UsesInlineLogical64(op));
}

// A signed 41-bit byte target covers VA39 plus the largest ADRP displacement.
// Literal loads can store the target in words because their addresses are aligned.
constexpr unsigned AddressTargetBits = 41;
constexpr unsigned AddressTargetShift = 6;
constexpr unsigned LiteralTargetBits = 39;
constexpr unsigned LiteralTargetShift = 10;
constexpr unsigned BitfieldShiftsOffset = 25;
constexpr unsigned BitfieldShiftsBits = 18;
static_assert(mem::ArmTaskSize == (uint64_t{1} << (AddressTargetBits - 2)));
static_assert(AddressTargetBits + AddressTargetShift <= DecodedOp::SpanShift);
static_assert(LiteralTargetBits + LiteralTargetShift <= DecodedOp::SpanShift);
static_assert(BitfieldShiftsOffset + BitfieldShiftsBits <= DecodedOp::SpanShift);
static_assert(LogicalImmediateBits == 38);

inline uint64_t PackOpOperands(const DecodedInstruction& op) {
    if (op.kind == Kind::Syscall || op.kind == Kind::Breakpoint) return op.immediate;
    // The two common immediate forms have room for the sign-extended or
    // scaled byte value. A hot handler never unscales and rescales an offset.
    if (op.kind == Kind::AddSubImmediate)
        return op.immediate | (uint64_t{op.rd} << 24) | (uint64_t{op.rn} << 29) | (uint64_t{op.operation} << 34) |
               (uint64_t{op.width == 64} << 36);

    if (op.kind == Kind::Address) {
        const auto target = op.target_pc & Ones(AddressTargetBits);
        return op.rd | (uint64_t{op.operation} << 5) | (target << AddressTargetShift);
    }
    if (op.kind == Kind::LoadLiteral) {
        const auto target = (op.target_pc >> 2) & Ones(LiteralTargetBits);
        return op.rd | (uint64_t(std::countr_zero(unsigned(op.width) / 32)) << 5) | (uint64_t{op.operation - 1u} << 7) |
               (uint64_t{Field(op.word, 26, 1)} << 9) | (target << LiteralTargetShift);
    }
    if (op.kind == Kind::LogicalImmediate) {
        return op.rd | (uint64_t{op.rn} << 5) | (uint64_t{op.operation} << 10) | (uint64_t{op.width == 64} << 12) |
               ((op.immediate & Ones(LogicalImmediateBits)) << LogicalImmediateShift);
    }
    if (op.kind == Kind::Bitfield) {
        const auto shifts = PackBitfieldShifts(op.width, op.shift, op.rm);
        return op.rd | (uint64_t{op.rn} << 5) | (uint64_t{op.shift} << 10) | (uint64_t{op.rm} << 16) |
               (uint64_t{op.operation} << 22) | (uint64_t{op.width == 64} << 24) |
               (uint64_t{shifts} << BitfieldShiftsOffset);
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
    const bool transfer = op.kind == Kind::LoadStoreRegister;
    const uint32_t packed = op.rd | (op.rn << 5) | ((op.rm & 31) << 10) | (aux << 15) | (op.operation << 21) |
                            (transfer ? vector << 25 : 0) | (op.mode << 26) | (uint32_t(width) << 29);
    if (op.kind == Kind::LoadStoreExclusive || op.kind == Kind::LoadStoreAtomic)
        return packed | (uint64_t{Field(op.word, 15, 1)} << 32) | (uint64_t{Field(op.word, 22, 2)} << 33);
    return packed;
}

#if defined(_MSC_VER)
#define A64_OPERANDS_INLINE __forceinline
#else
#define A64_OPERANDS_INLINE inline __attribute__((always_inline))
#endif

template <Kind kind>
A64_OPERANDS_INLINE uint64_t GetBoundTarget(const DecodedOp& slot) {
    if constexpr (kind == Kind::Address) {
        return SignExtend((slot.payload >> AddressTargetShift) & Ones(AddressTargetBits), AddressTargetBits);
    } else if constexpr (kind == Kind::LoadLiteral) {
        return SignExtend((slot.payload >> LiteralTargetShift) & Ones(LiteralTargetBits), LiteralTargetBits) << 2;
    } else if constexpr (HasBranchTarget(kind)) {
        return GetExtraOperands(slot).primary;
    }
    return 0;
}

A64_OPERANDS_INLINE uint64_t GetInlineLogical64(const DecodedOp& slot) {
    return SignExtend(slot.Operands() >> LogicalImmediateShift, LogicalImmediateBits);
}

template <Kind kind, unsigned width = 0, bool inline_logical64 = false>
A64_OPERANDS_INLINE DecodedInstruction GetOpOperands(const DecodedOp& slot) {
    const auto bits = slot.Operands();
    DecodedInstruction op;
    op.kind = kind;
    if constexpr (kind == Kind::Address) {
        op.rd = Field(bits, 0, 5);
        op.operation = Field(bits, 5, 1);
        op.target_pc = GetBoundTarget<kind>(slot);
    } else if constexpr (kind == Kind::LoadLiteral) {
        op.rd = Field(bits, 0, 5);
        op.width = 32 << Field(bits, 5, 2);
        op.operation = Field(bits, 7, 2) + 1;
        op.word = Field(bits, 9, 1) << 26;
        op.target_pc = GetBoundTarget<kind>(slot);
    } else if constexpr (kind == Kind::LogicalImmediate) {
        op.rd = Field(bits, 0, 5);
        op.rn = Field(bits, 5, 5);
        op.operation = Field(bits, 10, 2);
        op.width = width ? width : (Field(bits, 12, 1) ? 64 : 32);
        const bool wide = width == 0 ? slot.Has(HasDataSlot) : width == 64 && !inline_logical64;
        if (wide)
            op.immediate = GetExtraOperands(slot).primary;
        else
            op.immediate = op.width == 32 ? uint32_t(slot.payload >> LogicalImmediateShift) : GetInlineLogical64(slot);
    } else if constexpr (kind == Kind::Bitfield) {
        op.rd = Field(bits, 0, 5);
        op.rn = Field(bits, 5, 5);
        op.shift = Field(bits, 10, 6);
        op.rm = Field(bits, 16, 6);
        op.operation = Field(bits, 22, 2);
        op.width = Field(bits, 24, 1) ? 64 : 32;
        // Threaded bitfields use shift counts in place of the expanded mask.
        op.immediate = (bits >> BitfieldShiftsOffset) & Ones(BitfieldShiftsBits);
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
        constexpr bool transfer = kind == Kind::LoadStoreRegister;
        op.rm = Field(bits, 10, 5);
        if constexpr (kind == Kind::Multiply || kind == Kind::LoadStoreExclusive)
            op.ra = Field(bits, 15, 5);
        else
            op.shift = Field(bits, 15, 6);
        op.operation = Field(bits, 21, transfer ? 4 : 5);
        op.mode = Field(bits, 26, 3);
        op.width = 8 << Field(bits, 29, 3);
        if constexpr (transfer) op.word = Field(bits, 25, 1) << 26;
        if constexpr (kind == Kind::LoadStoreExclusive || kind == Kind::LoadStoreAtomic)
            op.word = (((bits >> 32) & 1) << 15) | (((bits >> 33) & 3) << 22);
        if constexpr (HasExtraOperandSlot(kind)) {
            if constexpr (HasBranchTarget(kind))
                op.target_pc = GetBoundTarget<kind>(slot);
            else
                op.immediate = GetExtraOperands(slot).primary;
        } else if constexpr (kind == Kind::ConditionalCompare) {
            op.immediate = op.rd & 15;
        }
    }
    return op;
}

#undef A64_OPERANDS_INLINE

}  // namespace fiberish::aarch64::detail
