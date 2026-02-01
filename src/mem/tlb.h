#pragma once
#include "types.h"
#include <array>
#include <algorithm>
#include <cstring>

namespace x86emu::mem {

    struct alignas(16) TlbEntry {
        uint32_t tag_page = 0xFFFFFFFF; // Init to impossible value
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

        SoftTlb() {
            flush();
        }

        // Clear TLB
        void flush() {
            auto invalidate = [](auto& arr) {
                // Manually loop or use fill
                for (auto& entry : arr) {
                    entry.tag_page = 0xFFFFFFFF;
                    entry.addend = 0;
                }
            };
            invalidate(read_tlb);
            invalidate(write_tlb);
            invalidate(exec_tlb);
        }

        // Fill TLB (called from Slow Path)
        void fill(GuestAddr vaddr, HostAddr hptr, Perm perm) {
            const uint32_t tag = vaddr & ~PAGE_MASK;
            const size_t idx = (vaddr >> PAGE_SHIFT) & TLB_INDEX_MASK;
            
            // Calculate addend: hptr - vaddr
            const std::uintptr_t addend = reinterpret_cast<std::uintptr_t>(hptr) - static_cast<std::uintptr_t>(vaddr);

            // Fill based on permissions
            // Note: Dirty bit logic is handled by caller deciding whether to pass Perm::Write
            if (has_perm(perm, Perm::Read)) {
                read_tlb[idx] = { tag, addend };
            }
            if (has_perm(perm, Perm::Write)) {
                write_tlb[idx] = { tag, addend };
            }
            if (has_perm(perm, Perm::Exec)) {
                exec_tlb[idx] = { tag, addend };
            }
        }
    };
}
