#pragma once
#include <algorithm>
#include <array>
#include <cstring>
#include <functional>
#include "types.h"

namespace x86emu::mem {

struct alignas(16) TlbEntry {
    uint32_t tag = 1;  // Mismatch by default (odd value for even aligned vars)
    uint32_t padding = 0;
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
                entry.tag = 1; // Impossible tag for aligned address
                entry.addend = 0;
            }
        };
        invalidate(read_tlb);
        invalidate(write_tlb);
        invalidate(exec_tlb);
    }

    // Fill TLB (called from Slow Path)
    void fill(GuestAddr vaddr, HostAddr hptr, Property property) {
        const uint32_t tag = vaddr & ~PAGE_MASK; // Low bits 0
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const std::uintptr_t addend = reinterpret_cast<std::uintptr_t>(hptr) - static_cast<std::uintptr_t>(tag);

        if (has_property(property, Property::Read)) {
            read_tlb[idx] = {tag, 0, addend};
        }
        
        // Write TLB: Only if Write AND Dirty are set
        if (has_property(property, Property::Write) && has_property(property, Property::Dirty)) {
            write_tlb[idx] = {tag, 0, addend};
        }
        
        if (has_property(property, Property::Exec)) {
            exec_tlb[idx] = {tag, 0, addend};
        }
    }
};
}  // namespace x86emu::mem
