#pragma once

#include <bit>
#include <new>

#include "aarch64.h"

namespace fiberish::aarch64::detail {

constexpr unsigned BlockMax = 24;
constexpr unsigned PageBytes = 4096;
constexpr uint64_t PageMask = PageBytes - 1;
static_assert(BlockMax <= 32);

enum SlotFlag : uint8_t {
    HasDataSlot = 1,
    RetainsPage = 2,
    OmitsFlags = 4,
    UntrustedScalar = 8,
    UntrustedFused = 16,
    BaseFields = 32,
    Fused = 64,
};

struct alignas(16) DecodedOp {
    HandlerFunc handler = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t thread_padding = 0;
#endif
    // Low 40 bits hold the operands. The upper bytes locate the guest
    // instruction and block sentinel independently of the physical stride.
    uint64_t payload = 0;

    static constexpr uint64_t OperandMask = (uint64_t{1} << 40) - 1;
    static constexpr uint64_t Pack(uint64_t operands, unsigned ordinal, unsigned sentinel_units, uint8_t flags) {
        return operands | (uint64_t{ordinal} << 40) | (uint64_t{sentinel_units} << 48) | (uint64_t{flags} << 56);
    }

    uint64_t Operands() const { return payload & OperandMask; }
    unsigned GuestIndex() const { return (payload >> 40) & 0xff; }
    unsigned SentinelUnits() const { return (payload >> 48) & 0xff; }
    bool Has(SlotFlag flag) const { return (payload >> 56) & flag; }

    const DecodedOp* Advance(unsigned units) const {
        return std::launder(
            reinterpret_cast<const DecodedOp*>(reinterpret_cast<const std::byte*>(this) + units * sizeof(DecodedOp)));
    }

    template <bool wide>
    const DecodedOp* NextOp() const {
        return Advance(1 + wide);
    }

    const DecodedOp* NextOp() const { return Advance(1 + Has(HasDataSlot)); }
    const DecodedOp* Sentinel() const { return Advance(SentinelUnits()); }

    const DecodedOp* LastOp(unsigned length) const {
        auto* slot = this;
        for (unsigned i = 1; i < length; ++i) slot = slot->NextOp();
        return slot;
    }

    const BasicBlock* OwningBlock() const;
    uint64_t GetStartPc() const;
    uint32_t Word() const;
};

struct DecodedOpWithDataSlot {
    DecodedOp head;
    uint64_t primary = 0;
    uint64_t secondary = 0;
};

inline const DecodedOpWithDataSlot& GetExtraOperands(const DecodedOp& slot) {
    return *reinterpret_cast<const DecodedOpWithDataSlot*>(&slot);
}

struct BlockSentinel {
    DecodedOp head;
    mutable const BasicBlock* links[2];

    // On the sentinel the top byte points back to the owner, in slot units.
    unsigned OwnerUnits() const { return head.payload >> 56; }
};

inline const BlockSentinel& GetBlockSentinel(const DecodedOp& slot) {
    return *reinterpret_cast<const BlockSentinel*>(&slot);
}

static_assert(sizeof(DecodedOp) == 16 && offsetof(DecodedOp, handler) == 0);
static_assert(sizeof(DecodedOpWithDataSlot) == 32 && sizeof(BlockSentinel) == 32);

struct alignas(16) BasicBlockHeader {
    HandlerFunc entry = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t entry_padding = 0;
#endif
    uint64_t chain = 0;

    static constexpr uint64_t Valid = uint64_t{1} << 63;
    static constexpr uint64_t HasBindings = uint64_t{1} << 48;
    uint32_t start_pc() const { return static_cast<uint32_t>(chain); }
    unsigned inst_count() const { return (chain >> 32) & 0xff; }
    unsigned SentinelSlotIndex() const { return (chain >> 40) & 0xff; }
    bool IsValid() const { return (chain & Valid) != 0; }
};

struct alignas(16) BasicBlock {
    static const BasicBlock Invalid;

    BasicBlockHeader header;
    uint64_t start_pc = 0;
    const uint8_t* executable_page = nullptr;

    unsigned inst_count() const { return header.inst_count(); }
    bool IsValid() const { return header.IsValid(); }
    bool HasBindings() const { return (header.chain & BasicBlockHeader::HasBindings) != 0; }
    void Invalidate() { header.chain &= ~BasicBlockHeader::Valid; }

    static constexpr size_t BindingsOffset(unsigned count) { return (count * sizeof(uint32_t) + 7) & ~size_t{7}; }

    static size_t CalculateSize(unsigned units, unsigned count, unsigned bindings, unsigned fusions) {
        const auto code_size = sizeof(BasicBlock) + units * sizeof(DecodedOp) + sizeof(BlockSentinel);
        if (!bindings) return code_size + count * sizeof(uint32_t);
        return code_size + BindingsOffset(count) + 2 * sizeof(uint32_t) + bindings * sizeof(ScalarHandlerFunc) +
               fusions;
    }

    const DecodedOp* FirstOp() const { return reinterpret_cast<const DecodedOp*>(this + 1); }
    const DecodedOp* Sentinel() const { return FirstOp()->Advance(header.SentinelSlotIndex()); }

    // The footer uses guest ordinals, so it includes instructions with no slot.
    const uint32_t* Words() const { return reinterpret_cast<const uint32_t*>(&GetBlockSentinel(*Sentinel()) + 1); }

    uint32_t Word(unsigned ordinal) const {
        const auto word = Words()[ordinal];
        if constexpr (std::endian::native == std::endian::big) return std::byteswap(word);
        return word;
    }

    // Two masks index the optional scalar and fused handlers, followed by fusion lengths.
    const uint32_t* BindingMasks() const {
        return reinterpret_cast<const uint32_t*>(reinterpret_cast<const std::byte*>(Words()) +
                                                 BindingsOffset(inst_count()));
    }

    ScalarHandlerFunc ScalarBinding(unsigned ordinal) const {
        if (!HasBindings()) return nullptr;
        const auto* masks = BindingMasks();
        const auto mask = masks[0];
        const auto bit = uint32_t{1} << ordinal;
        if (!(mask & bit)) return nullptr;
        const auto* bindings = reinterpret_cast<const ScalarHandlerFunc*>(masks + 2);
        return bindings[std::popcount(mask & (bit - 1))];
    }

    struct Fusion {
        ScalarHandlerFunc handler = nullptr;
        unsigned length = 1;
    };

    Fusion FusedBinding(unsigned ordinal) const {
        const auto* masks = BindingMasks();
        const auto count = std::popcount(masks[0]);
        const auto mask = masks[1];
        const auto index = std::popcount(mask & ((uint32_t{1} << ordinal) - 1));
        const auto* bindings = reinterpret_cast<const ScalarHandlerFunc*>(masks + 2);
        const auto* lengths = reinterpret_cast<const uint8_t*>(bindings + count + std::popcount(mask));
        return {bindings[count + index], lengths[index]};
    }
};

inline const BasicBlock* DecodedOp::OwningBlock() const {
    const auto& sentinel = GetBlockSentinel(*Sentinel());
    return reinterpret_cast<const BasicBlock*>(reinterpret_cast<const std::byte*>(&sentinel) -
                                               sentinel.OwnerUnits() * sizeof(DecodedOp));
}

inline uint64_t DecodedOp::GetStartPc() const { return OwningBlock()->start_pc + uint64_t{GuestIndex()} * 4; }

inline uint32_t DecodedOp::Word() const { return OwningBlock()->Word(GuestIndex()); }

static_assert(alignof(BasicBlock) >= alignof(DecodedOp));
static_assert(sizeof(BasicBlockHeader) == 16);
static_assert(offsetof(BasicBlock, header) == 0);

}  // namespace fiberish::aarch64::detail
