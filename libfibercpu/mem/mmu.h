#pragma once
#include <algorithm>
#include <cstring>
#include <functional>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <vector>
#include "../common.h"
#include "tlb.h"
#include "types.h"

namespace fiberish::mem {

// L2 Chunk: Covers 4MB (1024 * 4KB)
// Sparse allocation of actual pages
struct PageTableChunk {
    std::array<std::byte*, 1024> pages;
    std::array<Property, 1024> permissions;

    PageTableChunk() {
        pages.fill(nullptr);
        permissions.fill(Property::None);
    }

    // Copy Constructor for Deep Copy (Fork)
    PageTableChunk(const PageTableChunk& other) {
        permissions = other.permissions;
        for (size_t i = 0; i < 1024; ++i) {
            if (other.pages[i]) {
                pages[i] = new std::byte[PAGE_SIZE];
                std::memcpy(pages[i], other.pages[i], PAGE_SIZE);
            } else {
                pages[i] = nullptr;
            }
        }
    }

    ~PageTableChunk() {
        for (auto* ptr : pages) {
            if (ptr) delete[] ptr;
        }
    }
};

// Callback Signatures
// We use the same signatures as the original SoftMMU to allow easy integration
using FaultHandler = void (*)(void* opaque, uint32_t addr, int is_write);
using MemHook = void (*)(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val);

// Shared Memory State (Page Tables)
struct PageDirectory {
    std::array<std::unique_ptr<PageTableChunk>, 1024> l1_directory;

    // Deep Copy Constructor for Fork
    PageDirectory(const PageDirectory& other) {
        for (size_t i = 0; i < 1024; ++i) {
            if (other.l1_directory[i]) {
                l1_directory[i] = std::make_unique<PageTableChunk>(*other.l1_directory[i]);
            }
        }
    }

    PageDirectory() = default;

    // Copy assignment deleted to prevent accidental heavy copies without explicit intent
    PageDirectory& operator=(const PageDirectory&) = delete;
};

class Mmu {
public:
    // Shared pointer to the page tables
    std::shared_ptr<PageDirectory> page_dir;

private:
    SoftTlb tlb;

    // Callbacks
    FaultHandler fault_handler = nullptr;
    void* fault_opaque = nullptr;

    MemHook mem_hook = nullptr;
    void* mem_hook_opaque = nullptr;

    // SMC Callback: Called when writing to an Executable Page
    using SmcHandler = void (*)(void* opaque, uint32_t addr);
    SmcHandler smc_handler = nullptr;
    void* smc_opaque = nullptr;

    // Status Linking
    EmuStatus* emu_status = nullptr;
    uint8_t* emu_fault_vector = nullptr;

    void signal_fault(uint32_t addr, int is_write) {
        if (fault_handler) {
            fault_handler(fault_opaque, addr, is_write);
        } else {
            // Default action if no handler
            // fprintf(stderr, "[MMU] Page Fault at 0x%08X (W=%d)\n", addr, is_write);
            if (emu_status) *emu_status = EmuStatus::Fault;
            if (emu_fault_vector) *emu_fault_vector = 14;  // #PF
        }
    }

    void sync_dirty(GuestAddr vaddr) {
        const uint32_t l1_idx = vaddr >> 22;
        const uint32_t l2_idx = (vaddr >> 12) & 0x3FF;
        if (!page_dir) return;
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (chunk) {
            chunk->permissions[l2_idx] = chunk->permissions[l2_idx] | Property::Dirty;
        }
    }

    // Slow Path: Resolve address, handle allocation/permissions/faults
    [[nodiscard]] HostAddr resolve_slow(GuestAddr addr, Property req_perm) {
        if (!page_dir) return nullptr;  // Should not happen if initialized correctly

        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        const uint32_t offset = addr & 0xFFF;

        // 1. Check L1
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) {
            // Unmapped region
            signal_fault(addr, (int)has_property(req_perm, Property::Write));
            // Check if fault handler mapped it
            if (emu_status && *emu_status == EmuStatus::Fault) return nullptr;
            if (!page_dir->l1_directory[l1_idx]) {
                return nullptr;
            }
        }

        // 2. Check Permissions
        Property current_perm = chunk->permissions[l2_idx];
        if (!has_property(current_perm, req_perm)) {
            signal_fault(addr, (int)has_property(req_perm, Property::Write));
            if (emu_status && *emu_status != EmuStatus::Running) return nullptr;
            current_perm = chunk->permissions[l2_idx];
            if (!has_property(current_perm, req_perm)) return nullptr;
        }

        // --- Triggered Dirty Bit Update ---
        if (has_property(req_perm, Property::Write) && !has_property(current_perm, Property::Dirty)) {
            current_perm = current_perm | Property::Dirty;
            chunk->permissions[l2_idx] = current_perm;
        }

        // 3. Lazy Allocation
        if (!chunk->pages[l2_idx]) {
            chunk->pages[l2_idx] = new std::byte[PAGE_SIZE];
            std::memset(chunk->pages[l2_idx], 0, PAGE_SIZE);
        }

        HostAddr page_base = chunk->pages[l2_idx];

        // 4. Fill TLB (ONLY if no hooks)
        if (!mem_hook) {
            bool is_exec = has_property(current_perm, Property::Exec);

            tlb.fill(addr, page_base, current_perm);

            // If it was Exec, ensure Write TLB entry is invalid, effectively creating a "Trap on Write"
            if (is_exec) {
                const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
                // Invalidate Write TLB to force slow path on write (triggering SMC check)
                tlb.write_tlb[idx].tag = 1;
            }
        }

        return page_base + offset;
    }

public:
    // Safe resolution without faulting
    [[nodiscard]] HostAddr resolve_safe(GuestAddr addr, Property req_perm) {
        if (!page_dir) return nullptr;

        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        const uint32_t offset = addr & 0xFFF;

        // 1. Check L1
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) return nullptr;

        // 2. Check Permissions
        Property current_perm = chunk->permissions[l2_idx];
        if (!has_property(current_perm, req_perm)) return nullptr;

        // --- Triggered Dirty Bit Update ---
        if (has_property(req_perm, Property::Write) && !has_property(current_perm, Property::Dirty)) {
            current_perm = current_perm | Property::Dirty;
            chunk->permissions[l2_idx] = current_perm;
        }

        // 3. Return existing page only (no lazy allocation for safe resolution)
        if (!chunk->pages[l2_idx]) {
            return nullptr;  // Page not populated, caller should trigger fault handling
        }

        HostAddr page_base = chunk->pages[l2_idx];
        return page_base + offset;
    }

public:
    Mmu() { page_dir = std::make_shared<PageDirectory>(); }

    // Explicitly share memory with another MMU
    explicit Mmu(std::shared_ptr<PageDirectory> shared_pd) : page_dir(std::move(shared_pd)) {}

    // Callback Setup
    void set_fault_callback(FaultHandler handler, void* opaque) {
        fault_handler = handler;
        fault_opaque = opaque;
    }

    void set_mem_hook(MemHook hook, void* opaque) {
        mem_hook = hook;
        mem_hook_opaque = opaque;
        // Flush TLB to ensure hooks are caught immediately (forcing slow path)
        tlb.flush();
    }

    void set_smc_callback(SmcHandler handler, void* opaque) {
        smc_handler = handler;
        smc_opaque = opaque;
    }

    Property get_property(GuestAddr addr) const {
        if (!page_dir) return Property::None;
        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) return Property::None;
        return chunk->permissions[l2_idx];
    }

    void set_status_ptr(EmuStatus* status, uint8_t* vector) {
        emu_status = status;
        emu_fault_vector = vector;
    }

    // API: mmap
    void mmap(GuestAddr addr, uint32_t size, uint8_t perms_raw) {
        Property perms = static_cast<Property>(perms_raw);
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end_addr = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end_addr; curr += PAGE_SIZE) {
            uint32_t l1_idx = curr >> 22;
            uint32_t l2_idx = (curr >> 12) & 0x3FF;

            if (!page_dir->l1_directory[l1_idx]) {
                page_dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
            }

            page_dir->l1_directory[l1_idx]->permissions[l2_idx] = perms;
        }
        tlb.flush();
    }

    // API: munmap - Clear pages and permissions
    void munmap(GuestAddr addr, uint32_t size) {
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end_addr = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end_addr; curr += PAGE_SIZE) {
            uint32_t l1_idx = curr >> 22;
            uint32_t l2_idx = (curr >> 12) & 0x3FF;

            auto& chunk = page_dir->l1_directory[l1_idx];
            if (chunk) {
                // Delete the page data
                if (chunk->pages[l2_idx]) {
                    delete[] chunk->pages[l2_idx];
                    chunk->pages[l2_idx] = nullptr;
                }
                // Clear permissions
                chunk->permissions[l2_idx] = Property::None;
            }
        }
        tlb.flush();
    }

    // Internal helper for resolution (TLB or Slow) without hooks
    [[nodiscard]] FORCE_INLINE HostAddr resolve_ptr(GuestAddr addr, Property req_perm) {
        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const uint32_t tag = addr & ~PAGE_MASK;  // Standard tag (page base)

        // Select the appropriate TLB array based on permission
        const auto& tlb_array = (req_perm == Property::Read) ? tlb.read_tlb : tlb.write_tlb;
        const auto entry = tlb_array[idx];

        // Check if the TLB entry matches the requested address
        if (entry.tag == tag) [[likely]] {
            return reinterpret_cast<HostAddr>(entry.addend + addr);
        }

        return resolve_slow(addr, req_perm);
    }

    template <typename T>
    [[nodiscard]] FORCE_INLINE T read_no_utlb(GuestAddr addr) {
        // We calculate the address of the *last* byte accessed.
        const GuestAddr end_addr = addr + sizeof(T) - 1;

        // If it crosses a page, this calculated tag will differ, causing a mismatch.
        const uint32_t target_tag = end_addr & ~PAGE_MASK;

        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto entry = tlb.read_tlb[idx];

        if (entry.tag == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l2_read_hits++;
            stats.total_reads++;
#endif
            auto ptr = *reinterpret_cast<T*>(entry.addend + addr);
            return ptr;
        }

#ifdef ENABLE_TLB_STATS
        stats.read_misses++;
        stats.total_reads++;
#endif
        // Fallback: TLB Miss OR Cross-page access
        return read_slow<T>(addr);
    }

    template <typename T>
    FORCE_INLINE void write_no_utlb(GuestAddr addr, T val) {
        // Identical logic to read(), but uses the write_tlb.
        const GuestAddr end_addr = addr + sizeof(T) - 1;

        // A match guarantees the write is contained entirely within a valid, writable, dirty page.
        const uint32_t target_tag = end_addr & ~PAGE_MASK;

        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto entry = tlb.write_tlb[idx];

        if (entry.tag == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l2_write_hits++;
            stats.total_writes++;
#endif
            *reinterpret_cast<T*>(entry.addend + addr) = val;
            return;
        }

#ifdef ENABLE_TLB_STATS
        stats.write_misses++;
        stats.total_writes++;
#endif
        // Fallback: TLB Miss OR Cross-page access OR Read-only page
        write_slow<T>(addr, val);
    }

    template <typename T>
    [[nodiscard]] FORCE_INLINE T read(GuestAddr addr, MicroTLB* utlb) {
        // We calculate the address of the *last* byte accessed.
        const GuestAddr end_addr = addr + sizeof(T) - 1;

        // If it crosses a page, this calculated tag will differ, causing a mismatch.
        const uint32_t target_tag = end_addr & ~PAGE_MASK;

        // L1 TLB
        if (utlb->tag_r == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l1_read_hits++;
            stats.total_reads++;
#endif
            return *reinterpret_cast<T*>(utlb->addend + addr);
        }

        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto entry = tlb.read_tlb[idx];

        if (entry.tag == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l2_read_hits++;
            stats.total_reads++;
#endif
            auto ptr = *reinterpret_cast<T*>(entry.addend + addr);
            utlb->tag_r = target_tag;
            utlb->addend = entry.addend;
            utlb->tag_w = has_property(entry.perm, Property::Write) ? target_tag
                                                                    : std::numeric_limits<decltype(utlb->tag_w)>::max();
            return ptr;
        }

        // Invalidate L1 TLB
        utlb->invalidate();

#ifdef ENABLE_TLB_STATS
        stats.read_misses++;
        stats.total_reads++;
#endif
        // Fallback: TLB Miss OR Cross-page access
        return read_slow<T>(addr);
    }

    template <typename T>
    FORCE_INLINE void write(GuestAddr addr, T val, MicroTLB* utlb) {
        // Identical logic to read(), but uses the write_tlb.
        const GuestAddr end_addr = addr + sizeof(T) - 1;
        // A match guarantees the write is contained entirely within a valid, writable, dirty page.
        const uint32_t target_tag = end_addr & ~PAGE_MASK;

        // L1 TLB
        if (utlb->tag_w == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l1_write_hits++;
            stats.total_writes++;
#endif
            *reinterpret_cast<T*>(utlb->addend + addr) = val;
            return;
        }

        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto entry = tlb.write_tlb[idx];

        if (entry.tag == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            stats.l2_write_hits++;
            stats.total_writes++;
#endif
            *reinterpret_cast<T*>(entry.addend + addr) = val;
            utlb->tag_w = target_tag;
            utlb->addend = entry.addend;
            utlb->tag_r = has_property(entry.perm, Property::Read) ? target_tag
                                                                   : std::numeric_limits<decltype(utlb->tag_r)>::max();
            return;
        }

        // Invalidate L1 TLB
        utlb->tag_w = std::numeric_limits<decltype(utlb->tag_w)>::max();
#ifdef ENABLE_TLB_STATS
        stats.write_misses++;
        stats.total_writes++;
#endif
        // Fallback: TLB Miss OR Cross-page access OR Read-only page
        write_slow<T>(addr, val);
    }

    // Slow Path: Hooks, Resolution, Faults
    template <typename T>
    T read_slow(GuestAddr addr) {
        T val;

        // 1. Fetch
        if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
            val = read_cross_page<T>(addr);
        } else {
            HostAddr ptr = resolve_slow(addr, Perm::Read);
            if (!ptr) return T{};
            val = *reinterpret_cast<T*>(ptr);
        }

        // 2. Hooks (Only here)
        if (mem_hook) {
            uint64_t hook_val = 0;
            if constexpr (sizeof(T) <= 8)
                std::memcpy(&hook_val, &val, sizeof(T));
            else
                std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, hook_val);
        }
        return val;
    }

    template <typename T>
    void write_slow(GuestAddr addr, T val) {
        // 1. Hooks (Trigger before write?)
        // Usually we want to trace what IS GOING to be written.
        if (mem_hook) {
            uint64_t hook_val = 0;
            if constexpr (sizeof(T) <= 8)
                std::memcpy(&hook_val, &val, sizeof(T));
            else
                std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);
        }

        // 2. Write
        if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
            write_cross_page<T>(addr, val);
            return;
        }

        HostAddr ptr = resolve_slow(addr, Property::Write);

        // SMC Detection
        if (ptr && smc_handler) {
            // Optimization: We could cache "is_exec" in resolve_slow or check TLB,
            // but getting property here is safe.
            Property p = get_property(addr);
            if (has_property(p, Property::Exec)) {
                smc_handler(smc_opaque, addr);
            }
        }

        if (ptr) *reinterpret_cast<T*>(ptr) = val;
    }

    // Optimized Cross-Page Access
    template <typename T>
    T read_cross_page(GuestAddr addr) {
        static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for read_cross_page");

        uint32_t page_offset = addr & PAGE_MASK;
        uint32_t len1 = PAGE_SIZE - page_offset;
        uint32_t len2 = sizeof(T) - len1;

        GuestAddr addr2 = addr + len1;

        HostAddr p1 = resolve_ptr(addr, Property::Read);
        if (!p1) return T{};

        HostAddr p2 = resolve_ptr(addr2, Property::Read);
        if (!p2) return T{};

        T val;
        std::byte* dest = reinterpret_cast<std::byte*>(&val);
        std::memcpy(dest, p1, len1);
        std::memcpy(dest + len1, p2, len2);

        return val;
    }

    template <typename T>
    void write_cross_page(GuestAddr addr, T val) {
        static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for write_cross_page");

        uint32_t page_offset = addr & PAGE_MASK;
        uint32_t len1 = PAGE_SIZE - page_offset;
        uint32_t len2 = sizeof(T) - len1;

        GuestAddr addr2 = addr + len1;

        HostAddr p1 = resolve_ptr(addr, Property::Write);
        if (!p1) return;

        HostAddr p2 = resolve_ptr(addr2, Property::Write);
        if (!p2) return;

        // SMC Detection
        if (smc_handler) {
            Property prop1 = get_property(addr);
            if (has_property(prop1, Property::Exec)) smc_handler(smc_opaque, addr);

            Property prop2 = get_property(addr2);
            if (has_property(prop2, Property::Exec)) smc_handler(smc_opaque, addr2);
        }

        std::byte* src = reinterpret_cast<std::byte*>(&val);
        std::memcpy(p1, src, len1);
        std::memcpy(p2, src + len1, len2);
    }

    // Block Copy
    uint32_t copy_block(GuestAddr src_addr, GuestAddr dst_addr, uint32_t size) {
        if (mem_hook) {
            for (uint32_t i = 0; i < size; ++i) {
                uint8_t val = this->read_no_utlb<uint8_t>(src_addr + i);
                if (emu_status && *emu_status != EmuStatus::Running) return i;
                this->write_no_utlb<uint8_t>(dst_addr + i, val);
                if (emu_status && *emu_status != EmuStatus::Running) return i;
            }
            return size;
        }

        uint32_t bytes_done = 0;
        while (bytes_done < size) {
            uint32_t curr_src = src_addr + bytes_done;
            uint32_t curr_dst = dst_addr + bytes_done;

            uint32_t src_rem = PAGE_SIZE - (curr_src & PAGE_MASK);
            uint32_t dst_rem = PAGE_SIZE - (curr_dst & PAGE_MASK);
            uint32_t chunk = std::min(size - bytes_done, std::min(src_rem, dst_rem));

            HostAddr p_src = resolve_slow(curr_src, Property::Read);
            if (!p_src) return bytes_done;

            HostAddr p_dst = resolve_slow(curr_dst, Property::Write);
            if (!p_dst) return bytes_done;

            // x86 REP MOVS with DF=0 usually handles overlap by copying byte-by-byte.
            // std::memcpy is NOT safe for overlap.
            // However, std::memmove is safe for overlap but doesn't match "fill" behavior.
            // For now, use memmove as it's safer than memcpy.
            std::memmove(p_dst, p_src, chunk);
            bytes_done += chunk;
        }
        return bytes_done;
    }

    // Fetch Instruction Pointer
    [[nodiscard]] inline const std::byte* translate_exec(GuestAddr addr) {
        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto& entry = tlb.exec_tlb[idx];
        const uint32_t tag = addr & ~PAGE_MASK;

        if (entry.tag == tag) {
            return reinterpret_cast<const std::byte*>(entry.addend + addr);
        }

        return resolve_slow(addr, Property::Exec);
    }

    // Read for Execution (Instruction Fetch)
    template <typename T>
    [[nodiscard]] inline T read_for_exec(GuestAddr addr) {
        const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto& entry = tlb.exec_tlb[idx];
        const uint32_t tag = addr & ~PAGE_MASK;

        HostAddr ptr = nullptr;
        if (entry.tag == tag) {
            ptr = reinterpret_cast<HostAddr>(entry.addend + addr);
        } else {
            ptr = resolve_slow(addr, Property::Exec);
        }

        if (!ptr) return T{};
        // No hooks for exec
        return *reinterpret_cast<T*>(ptr);
    }
#ifdef ENABLE_TLB_STATS
public:
    struct TlbStats {
        uint64_t l1_read_hits = 0;
        uint64_t l1_write_hits = 0;
        uint64_t l2_read_hits = 0;
        uint64_t l2_write_hits = 0;
        uint64_t read_misses = 0;
        uint64_t write_misses = 0;
        uint64_t total_reads = 0;
        uint64_t total_writes = 0;

        void reset() {
            l1_read_hits = l1_write_hits = l2_read_hits = l2_write_hits = 0;
            read_misses = write_misses = total_reads = total_writes = 0;
        }
    } stats;
#endif
};
}  // namespace fiberish::mem
