#pragma once
#include <algorithm>
#include <array>
#include <cstring>
#include <functional>
#include "types.h"

namespace x86emu::mem {

struct alignas(16) TlbEntry {
    uint32_t tag_page = 0;  // Low bits store Property (Valid bit determines if entry is active)
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
    void flush(std::function<void(GuestAddr)> sync_cb = nullptr) {
        auto invalidate = [&](auto& arr) {
            for (size_t i = 0; i < TLB_ENTRIES; ++i) {
                auto& entry = arr[i];
                if (sync_cb && (entry.tag_page & (uint32_t)Property::Valid) &&
                    (entry.tag_page & (uint32_t)Property::Dirty)) {
                    sync_cb(entry.tag_page & ~PAGE_MASK);
                }
                entry.tag_page = 0;
                entry.addend = 0;
            }
        };
        invalidate(read_tlb);
        invalidate(write_tlb);
        invalidate(exec_tlb);
    }

    // Fill TLB (called from Slow Path)
    void fill(GuestAddr vaddr, HostAddr hptr, Property property, std::function<void(GuestAddr)> sync_cb = nullptr) {
        const uint32_t tag_with_prop = (vaddr & ~PAGE_MASK) | (uint32_t)property | (uint32_t)Property::Valid;
        const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;

        // Calculate addend: hptr - vaddr
        const std::uintptr_t addend = reinterpret_cast<std::uintptr_t>(hptr) - static_cast<std::uintptr_t>(vaddr);

        auto fill_entry = [&](auto& arr, Property req_perm) {
            if (has_property(property, req_perm)) {
                auto& entry = arr[idx];
                // Check for eviction of a dirty entry
                if (sync_cb && (entry.tag_page & (uint32_t)Property::Valid) &&
                    (entry.tag_page & (uint32_t)Property::Dirty)) {
                    sync_cb(entry.tag_page & ~PAGE_MASK);
                }
                entry = {tag_with_prop, addend};
            }
        };

        fill_entry(read_tlb, Property::Read);
        fill_entry(write_tlb, Property::Write);
        fill_entry(exec_tlb, Property::Exec);
    }
};
}  // namespace x86emu::mem
