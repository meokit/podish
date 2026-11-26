#pragma once

#include <bit>

#include "aarch64.h"

namespace fiberish::aarch64::detail {

constexpr uint64_t Ones(unsigned bits) { return bits == 64 ? ~uint64_t{0} : (uint64_t{1} << bits) - 1; }

constexpr uint32_t Field(uint32_t word, unsigned low, unsigned bits) { return (word >> low) & Ones(bits); }

constexpr uint64_t SignExtend(uint64_t value, unsigned bits) {
    const auto sign = uint64_t{1} << (bits - 1);
    return (value ^ sign) - sign;
}

constexpr uint64_t RotateRight(uint64_t value, unsigned amount, unsigned width) {
    value &= Ones(width);
    return amount ? ((value >> amount) | (value << (width - amount))) & Ones(width) : value;
}

constexpr uint64_t Replicate(uint64_t element, unsigned element_width, unsigned width) {
    for (unsigned k = element_width; k < width; k *= 2) element |= element << k;
    return element;
}

enum : uint8_t { Store, Load, LoadSigned64, LoadSigned32, Prefetch };
enum : uint8_t { Offset, PostIndex, Unprivileged, PreIndex };
enum : uint8_t { Exclusive, ExclusivePair, Ordered, CompareSwap, CompareSwapPair, Acquire };

constexpr bool DecodeBitMasks(DecodedInstruction& op, unsigned n, unsigned r, unsigned s, bool logical) {
    const unsigned pattern = (n << 6) | (~s & 63);
    if (pattern < 2) return false;
    const unsigned size = std::bit_floor(pattern);
    if (size > op.width) return false;
    const unsigned levels = size - 1;
    r &= levels;
    s &= levels;
    if (logical && s == levels) return false;
    op.immediate = Replicate(RotateRight(Ones(s + 1), r, size), size, op.width);
    op.mask = Replicate(Ones(((s - r) & levels) + 1), size, op.width);
    op.shift = r;
    op.rm = s;
    return true;
}

constexpr void DecodeLoadStore(DecodedInstruction& op, Kind kind) {
    const auto word = op.word;
    const bool vector = Field(word, 26, 1);
    const unsigned size = Field(word, 30, 2);
    const unsigned operation = Field(word, 22, 2);
    if (vector) {
        if (operation >= 2 && size != 0) return;
        op.width = operation >= 2 ? 128 : 8 << size;
        op.operation = operation & 1;
    } else {
        if (size >= 2 && operation == 3) return;
        op.width = 8 << size;
        op.operation = size == 3 && operation == 2 ? unsigned(Prefetch) : operation;
    }

    if (kind == Kind::LoadStoreRegister) {
        op.mode = Field(word, 13, 3);
        if (!(op.mode & 2)) return;
        op.shift = Field(word, 12, 1) ? std::countr_zero(unsigned(op.width / 8)) : 0;
    } else if (Field(word, 24, 1)) {
        op.immediate = Field(word, 10, 12) * (op.width / 8);
    } else {
        op.mode = Field(word, 10, 2);
        if (op.operation == Prefetch && op.mode != Offset) return;
        if (vector && op.mode == Unprivileged) return;
        op.immediate = SignExtend(Field(word, 12, 9), 9);
    }
    op.kind = kind;
}

// Arm DDI 0602 encoding diagrams. The high byte selects a short, ordered list
// of candidates; the complete mask also checks fixed bits below bit 24.
enum class Encoding : uint8_t {
    Unallocated,
    Address,
    AddSubImmediate,
    LogicalOrMoveWide,
    BitfieldOrExtract,
    LogicalShifted,
    AddSubRegister,
    AddSubCarry,
    ConditionalCompare,
    ConditionalSelect,
    DataOneSource,
    DataTwoSource,
    Multiply,
    BranchImmediate,
    BranchRegister,
    BranchCondition,
    CompareBranch,
    TestBranch,
    LoadStoreImmediate,
    LoadStoreRegister,
    LoadStorePair,
    LoadLiteral,
    Exclusive,
    Atomic,
    Syscall,
    Breakpoint,
    System,
    ScalarFloat,
    Vector,
};

struct EncodingRule {
    uint32_t mask;
    uint32_t value;
    Encoding encoding;
};

constexpr EncodingRule EncodingRules[] = {
    {0x1f000000, 0x10000000, Encoding::Address},
    {0x1f000000, 0x11000000, Encoding::AddSubImmediate},
    {0x1f000000, 0x12000000, Encoding::LogicalOrMoveWide},
    {0x1f000000, 0x13000000, Encoding::BitfieldOrExtract},
    {0x1f000000, 0x0a000000, Encoding::LogicalShifted},
    {0x1f000000, 0x0b000000, Encoding::AddSubRegister},
    {0x1fe0fc00, 0x1a000000, Encoding::AddSubCarry},
    {0x3fe00410, 0x3a400000, Encoding::ConditionalCompare},
    {0x3fe00800, 0x1a800000, Encoding::ConditionalSelect},
    {0x7fff0000, 0x5ac00000, Encoding::DataOneSource},
    {0x7fe00000, 0x1ac00000, Encoding::DataTwoSource},
    {0x7f000000, 0x1b000000, Encoding::Multiply},
    {0x7c000000, 0x14000000, Encoding::BranchImmediate},
    {0xfffffc1f, 0xd61f0000, Encoding::BranchRegister},
    {0xfffffc1f, 0xd63f0000, Encoding::BranchRegister},
    {0xfffffc1f, 0xd65f0000, Encoding::BranchRegister},
    {0xff000010, 0x54000000, Encoding::BranchCondition},
    {0x7e000000, 0x34000000, Encoding::CompareBranch},
    {0x7e000000, 0x36000000, Encoding::TestBranch},
    {0x3b000000, 0x39000000, Encoding::LoadStoreImmediate},
    {0x3b200000, 0x38000000, Encoding::LoadStoreImmediate},
    {0x3b200c00, 0x38200800, Encoding::LoadStoreRegister},
    {0x3a000000, 0x28000000, Encoding::LoadStorePair},
    {0x3b000000, 0x18000000, Encoding::LoadLiteral},
    {0x3f000000, 0x08000000, Encoding::Exclusive},
    {0x3f200c00, 0x38200000, Encoding::Atomic},
    {0xffe0001f, 0xd4000001, Encoding::Syscall},
    {0xffe0001f, 0xd4200000, Encoding::Breakpoint},
    {0xffc00000, 0xd5000000, Encoding::System},
    {0x5e000000, 0x1e000000, Encoding::ScalarFloat},
    {0x9e000000, 0x0e000000, Encoding::Vector},
    {0xde000000, 0x5e000000, Encoding::Vector},
    {0xbf000000, 0x0c000000, Encoding::Vector},
    {0xbf000000, 0x0d000000, Encoding::Vector},
};

constexpr bool MatchesPrefix(const EncodingRule& rule, unsigned prefix) {
    return (prefix & (rule.mask >> 24)) == rule.value >> 24;
}

constexpr size_t MaxCandidates() {
    size_t max_count = 0;
    for (unsigned prefix = 0; prefix < 256; ++prefix) {
        size_t count = 0;
        for (const auto& rule : EncodingRules) count += MatchesPrefix(rule, prefix);
        if (count > max_count) max_count = count;
    }
    return max_count;
}

struct EncodingBucket {
    std::array<uint8_t, MaxCandidates()> rules{};
    uint8_t count = 0;
};

constexpr auto EncodingTable = [] {
    std::array<EncodingBucket, 256> table{};
    static_assert(std::size(EncodingRules) <= 256);
    for (size_t prefix = 0; prefix < table.size(); ++prefix) {
        auto& bucket = table[prefix];
        for (size_t i = 0; i < std::size(EncodingRules); ++i)
            if (MatchesPrefix(EncodingRules[i], prefix)) bucket.rules[bucket.count++] = uint8_t(i);
    }
    return table;
}();

constexpr Encoding LookupEncoding(uint32_t word) {
    const auto& bucket = EncodingTable[word >> 24];
    for (unsigned i = 0; i < bucket.count; ++i) {
        const auto& rule = EncodingRules[bucket.rules[i]];
        if ((word & rule.mask) == rule.value) return rule.encoding;
    }
    return Encoding::Unallocated;
}

constexpr DecodedInstruction BaseInstruction(uint32_t word) {
    DecodedInstruction op;
    op.word = word;
    op.rd = Field(word, 0, 5);
    op.rn = Field(word, 5, 5);
    op.rm = Field(word, 16, 5);
    op.ra = Field(word, 10, 5);
    op.width = Field(word, 31, 1) ? 64 : 32;
    return op;
}

constexpr DecodedInstruction DecodeFields(uint32_t word) {
    auto op = BaseInstruction(word);
    switch (LookupEncoding(word)) {
        case Encoding::Address: {
            op.kind = Kind::Address;
            op.operation = Field(word, 31, 1);
            op.immediate = SignExtend((Field(word, 5, 19) << 2) | Field(word, 29, 2), 21) << (op.operation ? 12 : 0);
            return op;
        }
        case Encoding::AddSubImmediate: {
            if (!Field(word, 23, 1)) {
                op.kind = Kind::AddSubImmediate;
                op.operation = Field(word, 29, 2);
                op.immediate = uint64_t{Field(word, 10, 12)} << (Field(word, 22, 1) ? 12 : 0);
                return op;
            }
            return op;
        }
        case Encoding::LogicalOrMoveWide: {
            op.operation = Field(word, 29, 2);
            if (!Field(word, 23, 1)) {
                if (op.width == 32 && Field(word, 22, 1)) return op;
                if (DecodeBitMasks(op, Field(word, 22, 1), Field(word, 16, 6), Field(word, 10, 6), true))
                    op.kind = Kind::LogicalImmediate;
            } else {
                op.shift = Field(word, 21, 2) * 16;
                if (op.shift < op.width && op.operation != 1) {
                    op.kind = Kind::MoveWide;
                    op.immediate = uint64_t{Field(word, 5, 16)} << op.shift;
                }
            }
            return op;
        }
        case Encoding::BitfieldOrExtract: {
            if (Field(word, 22, 1) != unsigned(op.width == 64)) return op;
            op.operation = Field(word, 29, 2);
            if (!Field(word, 23, 1)) {
                const auto r = Field(word, 16, 6);
                const auto s = Field(word, 10, 6);
                if (r < op.width && s < op.width && op.operation != 3 &&
                    DecodeBitMasks(op, Field(word, 22, 1), r, s, false))
                    op.kind = Kind::Bitfield;
            } else {
                op.shift = Field(word, 10, 6);
                if (op.operation == 0 && !Field(word, 21, 1) && op.shift < op.width) op.kind = Kind::Extract;
            }
            return op;
        }
        case Encoding::LogicalShifted: {
            op.shift = Field(word, 10, 6);
            if (op.shift < op.width) {
                op.kind = Kind::LogicalShifted;
                op.operation = Field(word, 29, 2) | (Field(word, 21, 1) << 2);
                op.mode = Field(word, 22, 2);
            }
            return op;
        }
        case Encoding::AddSubRegister: {
            op.operation = Field(word, 29, 2);
            if (!Field(word, 21, 1)) {
                op.mode = Field(word, 22, 2);
                op.shift = Field(word, 10, 6);
                if (op.mode != 3 && op.shift < op.width) op.kind = Kind::AddSubShifted;
            } else {
                op.mode = Field(word, 13, 3);
                op.shift = Field(word, 10, 3);
                if (!Field(word, 22, 2) && op.shift <= 4) op.kind = Kind::AddSubExtended;
            }
            return op;
        }
        case Encoding::AddSubCarry: {
            op.kind = Kind::AddSubCarry;
            op.operation = Field(word, 29, 2);
            return op;
        }
        case Encoding::ConditionalCompare: {
            op.kind = Kind::ConditionalCompare;
            op.operation = Field(word, 30, 1);
            op.mode = Field(word, 11, 1);
            op.shift = Field(word, 12, 4);
            op.immediate = Field(word, 0, 4);
            return op;
        }
        case Encoding::ConditionalSelect: {
            op.kind = Kind::ConditionalSelect;
            op.operation = (Field(word, 30, 1) << 1) | Field(word, 10, 1);
            op.shift = Field(word, 12, 4);
            return op;
        }
        case Encoding::DataOneSource: {
            op.operation = Field(word, 10, 6);
            if (op.operation <= 5 && (op.width == 64 || op.operation != 3)) op.kind = Kind::DataOneSource;
            return op;
        }
        case Encoding::DataTwoSource: {
            op.operation = Field(word, 10, 6);
            if (op.operation == 2 || op.operation == 3 || (op.operation >= 8 && op.operation <= 11) ||
                (op.operation >= 16 && op.operation <= 23 && (op.width == 64) == ((op.operation & 3) == 3)))
                op.kind = Kind::DataTwoSource;
            return op;
        }
        case Encoding::Multiply: {
            op.operation = Field(word, 21, 3);
            op.mode = Field(word, 15, 1);
            switch (op.operation) {
                case 0:
                    op.kind = Kind::Multiply;
                    break;
                case 1:
                case 5:
                    if (op.width == 64) op.kind = Kind::Multiply;
                    break;
                case 2:
                case 6:
                    if (op.width == 64 && op.ra == 31 && op.mode == 0) op.kind = Kind::Multiply;
                    break;
                default:
                    break;
            }
            return op;
        }
        case Encoding::BranchImmediate: {
            op.kind = Kind::BranchImmediate;
            op.operation = Field(word, 31, 1);
            op.immediate = SignExtend(Field(word, 0, 26), 26) << 2;
            return op;
        }
        case Encoding::BranchRegister: {
            op.kind = Kind::BranchRegister;
            op.operation = (word & 0xfffffc1f) == 0xd63f0000;
            return op;
        }
        case Encoding::BranchCondition: {
            op.kind = Kind::BranchCondition;
            op.shift = Field(word, 0, 4);
            op.immediate = SignExtend(Field(word, 5, 19), 19) << 2;
            return op;
        }
        case Encoding::CompareBranch: {
            op.kind = Kind::CompareBranch;
            op.operation = Field(word, 24, 1);
            op.immediate = SignExtend(Field(word, 5, 19), 19) << 2;
            return op;
        }
        case Encoding::TestBranch: {
            op.kind = Kind::TestBranch;
            op.operation = Field(word, 24, 1);
            op.shift = (Field(word, 31, 1) << 5) | Field(word, 19, 5);
            op.immediate = SignExtend(Field(word, 5, 14), 14) << 2;
            return op;
        }
        case Encoding::LoadStoreImmediate: {
            DecodeLoadStore(op, Kind::LoadStoreImmediate);
            return op;
        }
        case Encoding::LoadStoreRegister: {
            DecodeLoadStore(op, Kind::LoadStoreRegister);
            return op;
        }
        case Encoding::LoadStorePair: {
            const auto size = Field(word, 30, 2);
            const bool vector = Field(word, 26, 1);
            op.operation = Field(word, 22, 1);
            op.mode = Field(word, 23, 2);
            if (size == 3 || (!vector && size == 1 && (op.operation == Store || op.mode == 0))) return op;
            op.width = vector ? 32 << size : (size == 2 ? 64 : 32);
            op.immediate = SignExtend(Field(word, 15, 7), 7) * (op.width / 8);
            if (!vector && size == 1) op.operation = LoadSigned64;
            op.kind = Kind::LoadStorePair;
            return op;
        }
        case Encoding::LoadLiteral: {
            const auto size = Field(word, 30, 2);
            const bool vector = Field(word, 26, 1);
            if (vector && size == 3) return op;
            op.operation = vector || size <= 1 ? Load : (size == 2 ? LoadSigned64 : Prefetch);
            op.width = vector ? 32 << size : (size == 1 ? 64 : 32);
            op.immediate = SignExtend(Field(word, 5, 19), 19) << 2;
            op.kind = Kind::LoadLiteral;
            return op;
        }
        case Encoding::Exclusive: {
            const auto size = Field(word, 30, 2);
            const bool pair = Field(word, 21, 1);
            const bool ordered = Field(word, 23, 1);
            op.operation = Field(word, 22, 1);
            op.width = 8 << size;
            if (ordered && pair) {
                if (op.ra != 31) return op;
                op.mode = CompareSwap;
            } else if (pair && size < 2) {
                if (op.ra != 31 || (op.rd & 1) || (op.rm & 1)) return op;
                op.width = 32 << size;
                op.mode = CompareSwapPair;
            } else if (ordered) {
                if (op.rm != 31 || op.ra != 31) return op;
                op.mode = Ordered;
            } else {
                if ((!pair && op.ra != 31) || (op.operation == Load && op.rm != 31)) return op;
                op.mode = pair ? ExclusivePair : Exclusive;
            }
            op.kind = Kind::LoadStoreExclusive;
            return op;
        }
        case Encoding::Atomic: {
            op.width = 8 << Field(word, 30, 2);
            op.operation = Field(word, 12, 4);
            if (op.operation <= 8) {
                op.kind = Kind::LoadStoreAtomic;
            } else if ((word & 0x3ffffc00) == 0x38bfc000) {
                op.kind = Kind::LoadStoreExclusive;
                op.mode = Acquire;
                op.operation = Load;
            }
            return op;
        }
        case Encoding::Syscall: {
            op.kind = Kind::Syscall;
            op.immediate = Field(word, 5, 16);
            return op;
        }
        case Encoding::Breakpoint: {
            op.kind = Kind::Breakpoint;
            op.immediate = Field(word, 5, 16);
            return op;
        }
        case Encoding::System: {
            op.kind = Kind::System;
            return op;
        }
        case Encoding::ScalarFloat: {
            op.kind = Kind::ScalarFloat;
            return op;
        }
        case Encoding::Vector: {
            op.kind = Kind::Vector;
            return op;
        }
        case Encoding::Unallocated:
            return op;
    }
    return op;
}

}  // namespace fiberish::aarch64::detail
