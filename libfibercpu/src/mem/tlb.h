#pragma once
#include <algorithm>
#include <array>
#include <bit>
#include <cstring>
#include <functional>
#include "types.h"

namespace fiberish::mem {

#ifdef ENABLE_TLB_STATS
struct TlbStats {
    uint64_t l1_read_hits = 0;
    uint64_t l1_write_hits = 0;
    uint64_t l2_read_hits = 0;
    uint64_t l2_write_hits = 0;
    uint64_t read_misses = 0;
    uint64_t write_misses = 0;
    uint64_t total_reads = 0;
    uint64_t total_writes = 0;

    void reset() { *this = {}; }
};

struct DispatchStats {
    uint64_t direct_links = 0;
    uint64_t resolved_links = 0;
    uint64_t cross_page_links = 0;
    uint64_t link_misses = 0;
    uint64_t block_lookup_hits = 0;
    uint64_t block_lookup_misses = 0;
    uint64_t block_allocations = 0;
    uint64_t block_allocated_bytes = 0;
    uint64_t memory_replays = 0;
};
#endif

using MicroTlbAbiWord = uint64_t;
inline constexpr uint32_t kInvalidMicroTlbTag = std::numeric_limits<uint32_t>::max();

inline constexpr MicroTlbAbiWord PackMicroTlbTags(uint32_t tag_r, uint32_t tag_w) {
    return static_cast<MicroTlbAbiWord>(tag_r) | (static_cast<MicroTlbAbiWord>(tag_w) << 32);
}

inline constexpr MicroTlbAbiWord InvalidMicroTlbAbiTags() {
    return PackMicroTlbTags(kInvalidMicroTlbTag, kInvalidMicroTlbTag);
}

// Keep the same representation in the MMU and the handler ABI so a hit
// can forward both tags without reconstructing the word.
struct alignas(16) MicroTLB {
    MicroTlbAbiWord tags = InvalidMicroTlbAbiTags();
    std::uintptr_t addend = 0;

    constexpr uint32_t ReadTag() const { return static_cast<uint32_t>(tags); }
    constexpr uint32_t WriteTag() const { return static_cast<uint32_t>(tags >> 32); }

    void invalidate() {
        tags = InvalidMicroTlbAbiTags();
        addend = 0;
    }
};

inline constexpr MicroTlbAbiWord EncodeMicroTlbTags(const MicroTLB& utlb) { return utlb.tags; }

inline constexpr MicroTlbAbiWord EncodeMicroTlbAddend(const MicroTLB& utlb) {
    return static_cast<MicroTlbAbiWord>(utlb.addend);
}

inline constexpr void EncodeMicroTlbAbi(const MicroTLB& utlb, MicroTlbAbiWord& tags, MicroTlbAbiWord& addend) {
    tags = EncodeMicroTlbTags(utlb);
    addend = EncodeMicroTlbAddend(utlb);
}

inline constexpr MicroTLB DecodeMicroTlbAbi(MicroTlbAbiWord tags, MicroTlbAbiWord addend) {
    return {tags, static_cast<std::uintptr_t>(addend)};
}

inline void InvalidateMicroTLB(MicroTLB* utlb) { utlb->invalidate(); }

struct alignas(16) TlbEntry {
    uint32_t tag = std::numeric_limits<uint32_t>::max();  // Mismatch by default (odd value for even aligned vars)
    Property perm = Property::None;
    std::uintptr_t addend = 0;
};

#ifndef FIBERCPU_DATA_TLB_ENTRIES
#define FIBERCPU_DATA_TLB_ENTRIES 1024
#endif

constexpr size_t TLB_ENTRIES = FIBERCPU_DATA_TLB_ENTRIES;
static_assert(std::has_single_bit(TLB_ENTRIES));
constexpr size_t TLB_INDEX_MASK = TLB_ENTRIES - 1;
constexpr size_t EXEC_TLB_ENTRIES = 256;

constexpr size_t ExecTlbIndex(GuestAddr address) { return (address >> PAGE_SHIFT) & (EXEC_TLB_ENTRIES - 1); }

class SoftTlb {
public:
    // Split 3-way TLB
    std::array<MicroTLB, TLB_ENTRIES> read_tlb;
    std::array<MicroTLB, TLB_ENTRIES> write_tlb;
    std::array<TlbEntry, EXEC_TLB_ENTRIES> exec_tlb;

    SoftTlb() { flush(); }

    // Clear TLB
    void flush() {
        for (auto& entry : read_tlb) entry.invalidate();
        for (auto& entry : write_tlb) entry.invalidate();
        for (auto& entry : exec_tlb) {
            entry.tag = std::numeric_limits<uint32_t>::max();
            entry.addend = 0;
        }
    }

    // Invalidate a single page in all TLBs
    void flush_page(GuestAddr vaddr) {
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const uint32_t tag = vaddr & ~PAGE_MASK;

        if (read_tlb[idx].ReadTag() == tag) read_tlb[idx].invalidate();
        if (write_tlb[idx].WriteTag() == tag) write_tlb[idx].invalidate();
        auto& exec_entry = exec_tlb[ExecTlbIndex(vaddr)];
        if (exec_entry.tag == tag) {
            exec_entry.tag = std::numeric_limits<uint32_t>::max();
        }
    }

    // Fill TLB (called from Slow Path)
    void fill(GuestAddr vaddr, HostAddr hptr, Property property) {
        const uint32_t tag = vaddr & ~PAGE_MASK;  // Low bits 0
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const std::uintptr_t addend = reinterpret_cast<std::uintptr_t>(hptr) - static_cast<std::uintptr_t>(tag);

        const bool readable = has_property(property, Property::Read);
        // Clean pages and pages that require write interception must go through the slow path.
        const bool writable = has_property(property, Property::Write) && has_property(property, Property::Dirty) &&
                              !has_property(property, Property::ForceWriteSlow);
        // L2 hits can forward this entry directly to the MicroTLB.
        const MicroTLB entry{
            PackMicroTlbTags(readable ? tag : kInvalidMicroTlbTag, writable ? tag : kInvalidMicroTlbTag), addend};
        if (readable) read_tlb[idx] = entry;
        if (writable) write_tlb[idx] = entry;

        if (has_property(property, Property::Exec)) {
            exec_tlb[ExecTlbIndex(vaddr)] = {tag, property, addend};
        }
    }

    // AArch64 packs the 27-bit VPN in each existing tag word.
    void flush_arm_page(uint32_t tag) {
        const size_t index = tag & TLB_INDEX_MASK;
        if (read_tlb[index].ReadTag() == tag) read_tlb[index].invalidate();
        if (write_tlb[index].WriteTag() == tag) write_tlb[index].invalidate();
        auto& exec_entry = exec_tlb[tag & (EXEC_TLB_ENTRIES - 1)];
        if (exec_entry.tag == tag) exec_entry.tag = kInvalidMicroTlbTag;
    }

    void fill_arm(uint64_t address, HostAddr host_page, Property property) {
        const uint32_t tag = ArmPageTag(address);
        const size_t index = tag & TLB_INDEX_MASK;
        const auto addend =
            reinterpret_cast<uintptr_t>(host_page) - static_cast<uintptr_t>(address & ~uint64_t{PAGE_MASK});
        const bool readable = has_property(property, Property::Read);
        const bool writable = has_property(property, Property::Write | Property::Dirty) &&
                              !has_property(property, Property::ForceWriteSlow);
        const MicroTLB entry{
            PackMicroTlbTags(readable ? tag : kInvalidMicroTlbTag, writable ? tag : kInvalidMicroTlbTag), addend};
        if (readable) read_tlb[index] = entry;
        if (writable) write_tlb[index] = entry;
        if (has_property(property, Property::Exec)) {
            exec_tlb[tag & (EXEC_TLB_ENTRIES - 1)] = {tag, property, addend};
        }
    }
};
}  // namespace fiberish::mem
