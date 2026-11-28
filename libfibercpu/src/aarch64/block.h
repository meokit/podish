#pragma once

#include <bit>
#include <new>

#include "../block_limits.h"
#include "../mem/types.h"
#include "aarch64.h"

namespace fiberish::aarch64::detail {

using ::fiberish::BlockMax;
constexpr unsigned PageBytes = 4096;
constexpr uint64_t PageMask = PageBytes - 1;
static_assert(BlockMax <= 32);

enum SlotFlag : uint8_t {
    HasDataSlot = 1,
    RetainsPage = 2,
    HasBranchLink = 4,
};
static_assert((HasDataSlot | RetainsPage | HasBranchLink) < (1u << 3));

struct alignas(16) DecodedOp {
    HandlerFunc handler = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t thread_padding = 0;
#endif
    // Low 40 bits hold the operands. Byte 5 holds the guest ordinal, byte 6
    // holds the fused instruction span, and byte 7 holds SlotFlag bits and
    // the conditional branch's remaining guest instruction count.
    // The sentinel uses the entire payload for its fallthrough link.
    mutable uint64_t payload = 0;

    static constexpr uint64_t OperandMask = (uint64_t{1} << 40) - 1;
    static constexpr uint64_t Pack(uint64_t operands, unsigned ordinal, uint8_t flags, uint8_t span,
                                   unsigned branch_suffix) {
        return operands | (uint64_t{ordinal} << 40) | (uint64_t{span} << 48) | (uint64_t{flags} << 56) |
               (uint64_t{branch_suffix} << 59);
    }

    uint64_t Operands() const { return payload & OperandMask; }
    unsigned GuestIndex() const { return (payload >> 40) & 0xff; }
    unsigned Span() const { return (payload >> 48) & 0xff; }
    unsigned BranchSuffix() const { return payload >> 59; }
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

    const DecodedOp* LastOp(unsigned length) const {
        auto* slot = this;
        for (unsigned i = 1; i < length; ++i) slot = slot->NextOp();
        return slot;
    }

    uint64_t GetStartPc(const State& state) const;
};

struct DecodedOpWithDataSlot {
    DecodedOp head;
    uint64_t primary = 0;            // Bound target, branch-register PC or immediate.
    mutable uint64_t secondary = 0;  // Taken link for branches, mask for bitfield operations.
};

inline const DecodedOpWithDataSlot& GetExtraOperands(const DecodedOp& slot) {
    return *reinterpret_cast<const DecodedOpWithDataSlot*>(&slot);
}

inline const BasicBlock* LoadBlockLink(uint64_t link) {
    return reinterpret_cast<const BasicBlock*>(static_cast<uintptr_t>(link));
}

inline uint64_t BlockLink(const BasicBlock* block) { return reinterpret_cast<uintptr_t>(block); }

static_assert(sizeof(DecodedOp) == 16 && offsetof(DecodedOp, handler) == 0);
static_assert(sizeof(DecodedOpWithDataSlot) == 32);

struct BasicBlockChainPrefix {
    uint32_t pc_low;
    uint16_t metadata;
    mutable uint16_t llvm_hot_count;

    static constexpr unsigned CountShift = 7;
    static constexpr uint16_t Valid = uint16_t{1} << 13;
    static constexpr uint16_t HasRegionContext = uint16_t{1} << 15;
    static constexpr uint64_t StartPcMask = mem::ArmTaskSize - 1;

    static constexpr BasicBlockChainPrefix Pack(uint64_t pc, unsigned count, bool region_context, bool valid = true) {
        return {uint32_t(pc),
                uint16_t((pc >> 32) | (count << CountShift) | (valid ? Valid : 0) |
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
    uint8_t reserved = 0;
    uint16_t invalidation_revision = 0;

    uint64_t start_pc() const { return header.start_pc(); }
    unsigned inst_count() const { return header.inst_count(); }
    uint64_t end_pc() const { return start_pc() + uint64_t{inst_count()} * 4; }
    bool IsValid() const { return header.IsValid(); }
    void Invalidate() {
        if (!IsValid()) return;
        header.chain.metadata &= ~BasicBlockChainPrefix::Valid;
        ++invalidation_revision;
    }

    static constexpr size_t RegionContextOffset(unsigned units) {
        return units * sizeof(DecodedOp) + sizeof(DecodedOp);
    }

    static constexpr size_t CalculateSize(unsigned units, unsigned count, bool retain_region_context) {
        return sizeof(BasicBlock) + RegionContextOffset(units) + (retain_region_context ? sizeof(void*) : 0) +
               count * sizeof(uint32_t);
    }

    const DecodedOp* FirstOp() const { return reinterpret_cast<const DecodedOp*>(this + 1); }
    const DecodedOp* Sentinel() const { return FirstOp()->Advance(sentinel_slot_index); }
    HandlerFunc& EntryHandler() { return header.entry; }
    bool HasRegionContext() const { return header.chain.metadata & BasicBlockChainPrefix::HasRegionContext; }
    const void* RegionContext() const {
        return HasRegionContext() ? *reinterpret_cast<const void* const*>(Sentinel() + 1) : nullptr;
    }
    void SetRegionContext(const void* context) {
        *reinterpret_cast<const void**>(reinterpret_cast<std::byte*>(this + 1) +
                                        RegionContextOffset(sentinel_slot_index)) = context;
    }

    // The footer uses guest ordinals, so it includes instructions with no slot.
    const uint32_t* Words() const {
        const auto* footer = reinterpret_cast<const std::byte*>(Sentinel() + 1);
        return reinterpret_cast<const uint32_t*>(footer + (HasRegionContext() ? sizeof(void*) : 0));
    }

    uint32_t Word(unsigned ordinal) const {
        const auto word = Words()[ordinal];
        if constexpr (std::endian::native == std::endian::big) return std::byteswap(word);
        return word;
    }
};

inline uint64_t DecodedOp::GetStartPc(const State& state) const {
    return state.last_block->start_pc() + uint64_t{GuestIndex()} * 4;
}

static_assert(alignof(BasicBlock) >= alignof(DecodedOp));
static_assert(mem::ArmTaskSize == (uint64_t{1} << 39) && BlockMax < 64);
static_assert(sizeof(BasicBlockChainPrefix) == 8);
static_assert(offsetof(BasicBlockChainPrefix, llvm_hot_count) == 6);
static_assert(sizeof(BasicBlockHeader) == 16);
static_assert(offsetof(BasicBlockHeader, chain) == 0 && offsetof(BasicBlockHeader, entry) == 8);
static_assert(offsetof(BasicBlock, header) == 0);
static_assert(sizeof(BasicBlock) == 32);

}  // namespace fiberish::aarch64::detail
