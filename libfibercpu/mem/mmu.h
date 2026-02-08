#pragma once
#include <algorithm>
#include <cstring>
#include <functional>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <vector>
#include "../common.h"
#include "../decoder.h"
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

    // Updated signal_fault to take context
    void signal_fault(EmuState* state, uint32_t addr, int is_write, DecodedOp* op);

    void sync_dirty(GuestAddr vaddr);

    // Slow Path: Resolve address, handle allocation/permissions/faults
    [[nodiscard]] MemResult<HostAddr> resolve_slow(EmuState* state, GuestAddr addr, Property req_perm, DecodedOp* op);

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

    // API: allocate_page - Allocate a single page and return host pointer
    // Sets permissions and allocates memory in one call
    // Returns the host address of the allocated page, or nullptr on failure
    [[nodiscard]] HostAddr allocate_page(GuestAddr addr, uint8_t perms_raw) {
        Property perms = static_cast<Property>(perms_raw);
        uint32_t page_addr = addr & ~PAGE_MASK;
        uint32_t l1_idx = page_addr >> 22;
        uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        if (!page_dir->l1_directory[l1_idx]) {
            page_dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
        }

        auto& chunk = page_dir->l1_directory[l1_idx];

        // Allocate page if not already allocated
        if (!chunk->pages[l2_idx]) {
            chunk->pages[l2_idx] = new std::byte[PAGE_SIZE];
            std::memset(chunk->pages[l2_idx], 0, PAGE_SIZE);
        }

        chunk->permissions[l2_idx] = perms;
        tlb.flush_page(page_addr);

        return chunk->pages[l2_idx];
    }

    // API: map_external_page - Map an external memory page to guest address
    // The external memory is NOT owned by the MMU (will not be freed on munmap)
    // For mmap passthrough, shared memory, etc.
    // Returns true on success
    bool map_external_page(GuestAddr addr, HostAddr external_page, uint8_t perms_raw) {
        Property perms = static_cast<Property>(perms_raw);
        uint32_t page_addr = addr & ~PAGE_MASK;
        uint32_t l1_idx = page_addr >> 22;
        uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        if (!page_dir->l1_directory[l1_idx]) {
            page_dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
        }

        auto& chunk = page_dir->l1_directory[l1_idx];

        // Free existing page if owned
        if (chunk->pages[l2_idx]) {
            delete[] chunk->pages[l2_idx];
        }

        // Set external page (caller owns this memory)
        chunk->pages[l2_idx] = external_page;
        chunk->permissions[l2_idx] = perms | Property::External;  // Mark as external
        tlb.flush_page(page_addr);

        return true;
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
                // Delete the page data only if not external
                if (chunk->pages[l2_idx] && !has_property(chunk->permissions[l2_idx], Property::External)) {
                    delete[] chunk->pages[l2_idx];
                }
                chunk->pages[l2_idx] = nullptr;
                // Clear permissions
                chunk->permissions[l2_idx] = Property::None;
            }
        }
        tlb.flush();
    }

    // Internal helper for resolution (TLB or Slow) without hooks
    [[nodiscard]] FORCE_INLINE MemResult<HostAddr> resolve_ptr(EmuState* state, GuestAddr addr, Property req_perm,
                                                               DecodedOp* op);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read_no_utlb(EmuState* state, GuestAddr addr, DecodedOp* op = nullptr);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<void> write_no_utlb(EmuState* state, GuestAddr addr, T val,
                                                             DecodedOp* op = nullptr);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read(EmuState* state, GuestAddr addr, MicroTLB* utlb, DecodedOp* op);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<void> write(EmuState* state, GuestAddr addr, T val, MicroTLB* utlb,
                                                     DecodedOp* op);

    // Slow Path: Hooks, Resolution, Faults
    template <typename T>
    [[nodiscard]] MemResult<T> read_slow(EmuState* state, GuestAddr addr, DecodedOp* op);

    template <typename T>
    [[nodiscard]] MemResult<void> write_slow(EmuState* state, GuestAddr addr, T val, DecodedOp* op);

    // Optimized Cross-Page Access
    template <typename T>
    [[nodiscard]] MemResult<T> read_cross_page(EmuState* state, GuestAddr addr, DecodedOp* op);

    template <typename T>
    [[nodiscard]] MemResult<void> write_cross_page(EmuState* state, GuestAddr addr, T val, DecodedOp* op);

    // Block Copy
    // Note: I updated this signature in impl to take EmuState* state.
    [[nodiscard]] MemResult<void> copy_block(EmuState* state, GuestAddr src_addr, GuestAddr dst_addr, uint32_t size,
                                             DecodedOp* op);

    // Fetch Instruction Pointer
    [[nodiscard]] inline const std::byte* translate_exec(EmuState* state, GuestAddr addr);

    // Read for Execution (Instruction Fetch)
    template <typename T>
    [[nodiscard]] inline T read_for_exec(EmuState* state, GuestAddr addr);

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
