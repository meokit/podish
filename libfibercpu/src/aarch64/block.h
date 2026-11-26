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
    Wide = 1,
    RetainsPage = 2,
    OmitsFlags = 4,
    UntrustedScalar = 8,
    UntrustedFused = 16,
    BaseFields = 32,
    Fused = 64,
};

struct alignas(16) DispatchSlot {
    ThreadHandler thread = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t thread_padding = 0;
#endif
    // Low 40 bits hold the operands. The upper bytes locate the guest
    // instruction and block sentinel independently of the physical stride.
    uint64_t data = 0;

    static constexpr uint64_t OperandMask = (uint64_t{1} << 40) - 1;
    static constexpr uint64_t Pack(uint64_t operands, unsigned ordinal, unsigned sentinel_units, uint8_t flags) {
        return operands | (uint64_t{ordinal} << 40) | (uint64_t{sentinel_units} << 48) | (uint64_t{flags} << 56);
    }

    uint64_t Operands() const { return data & OperandMask; }
    unsigned Ordinal() const { return (data >> 40) & 0xff; }
    unsigned SentinelUnits() const { return (data >> 48) & 0xff; }
    bool Has(SlotFlag flag) const { return (data >> 56) & flag; }

    const DispatchSlot* Advance(unsigned units) const {
        return std::launder(reinterpret_cast<const DispatchSlot*>(reinterpret_cast<const std::byte*>(this) +
                                                                  units * sizeof(DispatchSlot)));
    }

    template <bool wide>
    const DispatchSlot* Next() const {
        return Advance(1 + wide);
    }

    const DispatchSlot* Next() const { return Advance(1 + Has(Wide)); }
    const DispatchSlot* Sentinel() const { return Advance(SentinelUnits()); }

    const DispatchSlot* Last(unsigned length) const {
        auto* slot = this;
        for (unsigned i = 1; i < length; ++i) slot = slot->Next();
        return slot;
    }

    const Block* Owner() const;
    uint64_t PC() const;
    uint32_t Word() const;
};

struct WideSlot {
    DispatchSlot head;
    uint64_t primary = 0;
    uint64_t secondary = 0;
};

inline const WideSlot& WideOperands(const DispatchSlot& slot) { return *reinterpret_cast<const WideSlot*>(&slot); }

struct SentinelSlot {
    DispatchSlot head;
    mutable const Block* links[2];

    // On the sentinel the top byte points back to the owner, in slot units.
    unsigned OwnerUnits() const { return head.data >> 56; }
};

inline const SentinelSlot& SentinelData(const DispatchSlot& slot) {
    return *reinterpret_cast<const SentinelSlot*>(&slot);
}

static_assert(sizeof(DispatchSlot) == 16 && offsetof(DispatchSlot, thread) == 0);
static_assert(sizeof(WideSlot) == 32 && sizeof(SentinelSlot) == 32);

struct alignas(16) BlockHeader {
    ThreadHandler entry = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t entry_padding = 0;
#endif
    uint64_t chain = 0;

    static constexpr uint64_t Valid = uint64_t{1} << 63;
    static constexpr uint64_t HasBindings = uint64_t{1} << 48;
    uint32_t Start() const { return static_cast<uint32_t>(chain); }
    unsigned Count() const { return (chain >> 32) & 0xff; }
    unsigned SlotUnits() const { return (chain >> 40) & 0xff; }
    bool IsValid() const { return (chain & Valid) != 0; }
};

struct alignas(16) Block {
    static const Block Invalid;

    BlockHeader header;
    uint64_t pc = 0;
    const uint8_t* executable_page = nullptr;

    unsigned Count() const { return header.Count(); }
    bool IsValid() const { return header.IsValid(); }
    bool HasBindings() const { return (header.chain & BlockHeader::HasBindings) != 0; }
    void Invalidate() { header.chain &= ~BlockHeader::Valid; }

    static constexpr size_t BindingsOffset(unsigned count) { return (count * sizeof(uint32_t) + 7) & ~size_t{7}; }

    static size_t Size(unsigned units, unsigned count, unsigned bindings, unsigned fusions) {
        const auto code_size = sizeof(Block) + units * sizeof(DispatchSlot) + sizeof(SentinelSlot);
        if (!bindings) return code_size + count * sizeof(uint32_t);
        return code_size + BindingsOffset(count) + 2 * sizeof(uint32_t) + bindings * sizeof(Handler) + fusions;
    }

    const DispatchSlot* Slots() const { return reinterpret_cast<const DispatchSlot*>(this + 1); }
    const DispatchSlot* Sentinel() const { return Slots()->Advance(header.SlotUnits()); }

    // The footer uses guest ordinals, so it includes instructions with no slot.
    const uint32_t* Words() const { return reinterpret_cast<const uint32_t*>(&SentinelData(*Sentinel()) + 1); }

    uint32_t Word(unsigned ordinal) const {
        const auto word = Words()[ordinal];
        if constexpr (std::endian::native == std::endian::big) return std::byteswap(word);
        return word;
    }

    // Two masks index the optional scalar and fused handlers, followed by fusion lengths.
    const uint32_t* BindingMasks() const {
        return reinterpret_cast<const uint32_t*>(reinterpret_cast<const std::byte*>(Words()) + BindingsOffset(Count()));
    }

    Handler ScalarBinding(unsigned ordinal) const {
        if (!HasBindings()) return nullptr;
        const auto* masks = BindingMasks();
        const auto mask = masks[0];
        const auto bit = uint32_t{1} << ordinal;
        if (!(mask & bit)) return nullptr;
        const auto* bindings = reinterpret_cast<const Handler*>(masks + 2);
        return bindings[std::popcount(mask & (bit - 1))];
    }

    struct Fusion {
        Handler handler = nullptr;
        unsigned length = 1;
    };

    Fusion FusedBinding(unsigned ordinal) const {
        const auto* masks = BindingMasks();
        const auto count = std::popcount(masks[0]);
        const auto mask = masks[1];
        const auto index = std::popcount(mask & ((uint32_t{1} << ordinal) - 1));
        const auto* bindings = reinterpret_cast<const Handler*>(masks + 2);
        const auto* lengths = reinterpret_cast<const uint8_t*>(bindings + count + std::popcount(mask));
        return {bindings[count + index], lengths[index]};
    }
};

inline const Block* DispatchSlot::Owner() const {
    const auto& sentinel = SentinelData(*Sentinel());
    return reinterpret_cast<const Block*>(reinterpret_cast<const std::byte*>(&sentinel) -
                                          sentinel.OwnerUnits() * sizeof(DispatchSlot));
}

inline uint64_t DispatchSlot::PC() const { return Owner()->pc + uint64_t{Ordinal()} * 4; }

inline uint32_t DispatchSlot::Word() const { return Owner()->Word(Ordinal()); }

static_assert(alignof(Block) >= alignof(DispatchSlot));
static_assert(sizeof(BlockHeader) == 16);
static_assert(offsetof(Block, header) == 0);

}  // namespace fiberish::aarch64::detail
