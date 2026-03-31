#pragma once
#include <algorithm>
#include <array>
#include <cstring>
#include <functional>
#include "types.h"

namespace fiberish::mem {

#if defined(__wasm32__)
struct alignas(1) MicroTLB {};
#else
struct alignas(16) MicroTLB {
    uint32_t tag_r = std::numeric_limits<uint32_t>::max();  // Mismatch by default (odd value for even aligned vars)
    uint32_t tag_w = std::numeric_limits<uint32_t>::max();
    std::uintptr_t addend = 0;

    void invalidate() {
        tag_r = std::numeric_limits<decltype(tag_r)>::max();
        tag_w = std::numeric_limits<decltype(tag_w)>::max();
        addend = 0;
    }
};
#endif

inline void InvalidateMicroTLB(MicroTLB* utlb) {
#if !defined(__wasm32__)
    utlb->invalidate();
#else
    (void)utlb;
#endif
}

struct alignas(16) TlbEntry {
    uint32_t tag = std::numeric_limits<uint32_t>::max();  // Mismatch by default (odd value for even aligned vars)
    Property perm = Property::None;
    std::uintptr_t addend = 0;
};

// TLB Size, must be power of 2
constexpr size_t TLB_ENTRIES = 256;
constexpr size_t TLB_INDEX_MASK = TLB_ENTRIES - 1;

class SoftTlb {
public:
    // Split 3-way TLB
    std::array<TlbEntry, TLB_ENTRIES> read_tlb;
    std::array<TlbEntry, TLB_ENTRIES> write_tlb;
    std::array<TlbEntry, TLB_ENTRIES> exec_tlb;

    SoftTlb() { flush(); }

    // Clear TLB
    void flush() {
        auto invalidate = [&](auto& arr) {
            for (auto& entry : arr) {
                entry.tag = std::numeric_limits<decltype(entry.tag)>::max();
                entry.addend = 0;
            }
        };
        invalidate(read_tlb);
        invalidate(write_tlb);
        invalidate(exec_tlb);
    }

    // Invalidate a single page in all TLBs
    void flush_page(GuestAddr vaddr) {
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const uint32_t tag = vaddr & ~PAGE_MASK;

        if (read_tlb[idx].tag == tag) {
            read_tlb[idx].tag = std::numeric_limits<uint32_t>::max();
        }
        if (write_tlb[idx].tag == tag) {
            write_tlb[idx].tag = std::numeric_limits<uint32_t>::max();
        }
        if (exec_tlb[idx].tag == tag) {
            exec_tlb[idx].tag = std::numeric_limits<uint32_t>::max();
        }
    }

    // Fill TLB (called from Slow Path)
    void fill(GuestAddr vaddr, HostAddr hptr, Property property) {
        const uint32_t tag = vaddr & ~PAGE_MASK;  // Low bits 0
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const std::uintptr_t addend = reinterpret_cast<std::uintptr_t>(hptr) - static_cast<std::uintptr_t>(tag);

        if (has_property(property, Property::Read)) {
            read_tlb[idx] = {tag, property, addend};
        }

        // Write TLB: Only if Write AND Dirty are set
        if (has_property(property, Property::Write) && has_property(property, Property::Dirty)) {
            write_tlb[idx] = {tag, property, addend};
        }

        if (has_property(property, Property::Exec)) {
            exec_tlb[idx] = {tag, property, addend};
        }
    }
};
}  // namespace fiberish::mem
