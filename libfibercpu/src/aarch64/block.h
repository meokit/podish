#pragma once

#include "aarch64.h"

namespace fiberish::aarch64::detail {

constexpr unsigned BlockMax = 24;
constexpr unsigned PageBytes = 4096;
constexpr uint64_t PageMask = PageBytes - 1;

struct DispatchSlot {
    ThreadHandler thread = nullptr;
    uint64_t pc = 0;
    union {
        DecodedInstruction op{};
        mutable const Block* links[2];  // Only the sentinel has link slots.
    };
    union {
        Handler fused = nullptr;
        const Block* owner;  // Only the sentinel has an owner pointer.
    };
    uint8_t remaining = 0;  // Includes this member, excludes the sentinel.
    uint8_t fused_length = 1;
    bool retains_page = false;
    bool can_follow = false;
    bool omits_flags = false;
    bool untrusted_scalar = false;
    bool untrusted_fused = false;
};

static_assert(offsetof(DispatchSlot, thread) == 0);

struct alignas(16) BlockHeader {
    ThreadHandler entry = nullptr;
    uint64_t chain = 0;

    static constexpr uint64_t Valid = uint64_t{1} << 63;
    uint32_t Start() const { return static_cast<uint32_t>(chain); }
    unsigned Count() const { return (chain >> 32) & 0xff; }
    bool IsValid() const { return (chain & Valid) != 0; }
};

struct alignas(16) Block {
    static const Block Invalid;

    BlockHeader header;
    uint64_t pc = 0;
    const uint8_t* executable_page = nullptr;

    unsigned Count() const { return header.Count(); }
    bool IsValid() const { return header.IsValid(); }
    void Invalidate() { header.chain &= ~BlockHeader::Valid; }

    static size_t Size(unsigned count, bool save_words) {
        return sizeof(Block) + (count + 1) * sizeof(DispatchSlot) + (save_words ? count * sizeof(uint32_t) : 0);
    }

    DispatchSlot* Slots() { return reinterpret_cast<DispatchSlot*>(this + 1); }
    const DispatchSlot* Slots() const { return const_cast<Block*>(this)->Slots(); }
    const DispatchSlot& Sentinel() const { return Slots()[Count()]; }
    uint32_t* Words() { return reinterpret_cast<uint32_t*>(Slots() + Count() + 1); }
    const uint32_t* Words() const { return const_cast<Block*>(this)->Words(); }
};

static_assert(alignof(Block) >= alignof(DispatchSlot));
static_assert(sizeof(BlockHeader) == 16);
static_assert(offsetof(Block, header) == 0);

}  // namespace fiberish::aarch64::detail
