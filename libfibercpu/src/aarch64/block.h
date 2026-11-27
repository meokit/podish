#pragma once

#include <bit>
#include <new>

#include "../block_handler_storage.h"
#include "../mem/types.h"
#include "aarch64.h"

namespace fiberish::aarch64::detail {

constexpr unsigned BlockMax = 24;
constexpr unsigned PageBytes = 4096;
constexpr uint64_t PageMask = PageBytes - 1;
static_assert(BlockMax <= 32);

ScalarHandlerFunc ResolveBlockScalarHandler(BlockHandlerIndex index);

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

struct BasicBlockChainPrefix {
    uint32_t pc_low;
    uint16_t metadata;
    mutable uint16_t llvm_hot_count;

    static constexpr unsigned CountShift = 7;
    static constexpr uint16_t Valid = uint16_t{1} << 13;
    static constexpr uint16_t HasBindings = uint16_t{1} << 14;
    static constexpr uint16_t HasRegionContext = uint16_t{1} << 15;
    static constexpr uint64_t StartPcMask = mem::ArmTaskSize - 1;

    static constexpr BasicBlockChainPrefix Pack(uint64_t pc, unsigned count, bool bindings, bool region_context) {
        return {uint32_t(pc),
                uint16_t((pc >> 32) | (count << CountShift) | Valid | (bindings ? HasBindings : 0) |
                         (region_context ? HasRegionContext : 0)),
                0};
    }

    uint64_t Packed() const {
        if constexpr (std::endian::native == std::endian::little) return std::bit_cast<uint64_t>(*this);
        return (uint64_t{llvm_hot_count} << 48) | (uint64_t{metadata} << 32) | pc_low;
    }

    uint64_t start_pc() const { return Packed() & StartPcMask; }
    unsigned inst_count() const { return (Packed() >> (32 + CountShift)) & 0x3f; }
};

struct alignas(16) BasicBlockHeader {
    BasicBlockChainPrefix chain{};
    HandlerFunc entry = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t entry_padding = 0;
#endif
    uint64_t start_pc() const { return chain.start_pc(); }
    unsigned inst_count() const { return chain.inst_count(); }
    bool IsValid() const { return chain.Packed() & (uint64_t{BasicBlockChainPrefix::Valid} << 32); }
};

struct alignas(16) BasicBlock {
    static const BasicBlock Invalid;

    BasicBlockHeader header;
    const uint8_t* executable_page = nullptr;
    uint8_t sentinel_slot_index = 0;
    bool has_wide_bindings = false;
    uint16_t invalidation_revision = 0;

    uint64_t start_pc() const { return header.start_pc(); }
    unsigned inst_count() const { return header.inst_count(); }
    bool IsValid() const { return header.IsValid(); }
    bool HasBindings() const { return header.chain.metadata & BasicBlockChainPrefix::HasBindings; }
    void Invalidate() {
        if (!IsValid()) return;
        header.chain.metadata &= ~BasicBlockChainPrefix::Valid;
        ++invalidation_revision;
    }

    static constexpr size_t BindingsOffset(unsigned count) { return (count * sizeof(uint32_t) + 7) & ~size_t{7}; }

    static constexpr size_t RegionContextOffset(unsigned units) {
        return units * sizeof(DecodedOp) + sizeof(BlockSentinel);
    }

    static size_t CalculateSize(unsigned units, unsigned count, unsigned bindings, unsigned fusions,
                                BlockHandlerStorage storage, bool retain_region_context) {
        const auto code_size =
            sizeof(BasicBlock) + RegionContextOffset(units) + (retain_region_context ? sizeof(void*) : 0);
        if (!bindings) return code_size + count * sizeof(uint32_t);
        return code_size + BindingsOffset(count) + 2 * sizeof(uint32_t) +
               bindings * BlockHandlerWidth<ScalarHandlerFunc>(storage) + fusions;
    }

    const DecodedOp* FirstOp() const { return reinterpret_cast<const DecodedOp*>(this + 1); }
    const DecodedOp* Sentinel() const { return FirstOp()->Advance(sentinel_slot_index); }
    HandlerFunc& EntryHandler() { return header.entry; }
    bool HasRegionContext() const { return header.chain.metadata & BasicBlockChainPrefix::HasRegionContext; }
    const void* RegionContext() const {
        return HasRegionContext() ? *reinterpret_cast<const void* const*>(&GetBlockSentinel(*Sentinel()) + 1) : nullptr;
    }
    void SetRegionContext(const void* context) {
        *reinterpret_cast<const void**>(reinterpret_cast<std::byte*>(this + 1) +
                                        RegionContextOffset(sentinel_slot_index)) = context;
    }

    // The footer uses guest ordinals, so it includes instructions with no slot.
    const uint32_t* Words() const {
        const auto* footer = reinterpret_cast<const std::byte*>(&GetBlockSentinel(*Sentinel()) + 1);
        return reinterpret_cast<const uint32_t*>(footer + (HasRegionContext() ? sizeof(void*) : 0));
    }

    uint32_t Word(unsigned ordinal) const {
        const auto word = Words()[ordinal];
        if constexpr (std::endian::native == std::endian::big) return std::byteswap(word);
        return word;
    }

    // Scalar and fusion masks rank the handlers by guest ordinal.
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
        return Binding(masks + 2, std::popcount(mask & (bit - 1)));
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
        const auto* bindings = masks + 2;
        const auto storage = has_wide_bindings ? BlockHandlerStorage::Pointers : BlockHandlerStorage::Indexed;
        const auto* lengths = reinterpret_cast<const uint8_t*>(bindings) +
                              (count + std::popcount(mask)) * BlockHandlerWidth<ScalarHandlerFunc>(storage);
        return {Binding(bindings, count + index), lengths[index]};
    }

private:
    ScalarHandlerFunc Binding(const void* bindings, unsigned index) const {
        if (has_wide_bindings) return static_cast<const ScalarHandlerFunc*>(bindings)[index];
        return ResolveBlockScalarHandler(static_cast<const BlockHandlerIndex*>(bindings)[index]);
    }
};

inline const BasicBlock* DecodedOp::OwningBlock() const {
    const auto& sentinel = GetBlockSentinel(*Sentinel());
    return reinterpret_cast<const BasicBlock*>(reinterpret_cast<const std::byte*>(&sentinel) -
                                               sentinel.OwnerUnits() * sizeof(DecodedOp));
}

inline uint64_t DecodedOp::GetStartPc() const { return OwningBlock()->start_pc() + uint64_t{GuestIndex()} * 4; }

inline uint32_t DecodedOp::Word() const { return OwningBlock()->Word(GuestIndex()); }

static_assert(alignof(BasicBlock) >= alignof(DecodedOp));
static_assert(mem::ArmTaskSize == (uint64_t{1} << 39) && BlockMax < 64);
static_assert(sizeof(BasicBlockChainPrefix) == 8);
static_assert(offsetof(BasicBlockChainPrefix, llvm_hot_count) == 6);
static_assert(sizeof(BasicBlockHeader) == 16);
static_assert(offsetof(BasicBlockHeader, chain) == 0 && offsetof(BasicBlockHeader, entry) == 8);
static_assert(offsetof(BasicBlock, header) == 0);
static_assert(sizeof(BasicBlock) == 32);

}  // namespace fiberish::aarch64::detail
