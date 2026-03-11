#pragma once
#include <algorithm>
#include <atomic>
#include <cstring>
#include <iostream>
#include <memory>
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
    // External pages are intentionally not copied and not converted to owned pages.
    // This guarantees cloned MMUs never "internalize" externally managed memory.
    PageTableChunk(const PageTableChunk& other) {
        for (size_t i = 0; i < 1024; ++i) {
            permissions[i] = other.permissions[i];

            if (!other.pages[i]) {
                pages[i] = nullptr;
                continue;
            }

            if (has_property(permissions[i], Property::External)) {
                // Do not clone external mappings into owned memory.
                pages[i] = nullptr;
                permissions[i] = Property::None;
                continue;
            }

            pages[i] = new std::byte[PAGE_SIZE];
            std::memcpy(pages[i], other.pages[i], PAGE_SIZE);
        }
    }

    ~PageTableChunk() {
        for (size_t i = 0; i < pages.size(); ++i) {
            auto* ptr = pages[i];
            if (ptr && !has_property(permissions[i], Property::External)) {
                delete[] ptr;
            }
        }
    }
};

// Callback Signatures
// We use the same signatures as the original SoftMMU to allow easy integration
using FaultHandler = int (*)(void* opaque, uint32_t addr, int is_write);
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

struct MmuCore {
    std::atomic<uint32_t> ref_count{1};
    std::unique_ptr<PageDirectory> page_directory;
    uintptr_t identity = 0;
    uint32_t state_flags = 0;
};

class Mmu {
public:
    // Fast non-owning alias for access paths.
    PageDirectory* page_dir = nullptr;

private:
    MmuCore* core_ = nullptr;
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

    EmuState* get_state();

    // Updated signal_fault to take context
    [[nodiscard]] EmuStatus signal_fault(uint32_t addr, int is_write);
    void sync_dirty(GuestAddr vaddr);

    // Slow Path: Resolve address, handle allocation/permissions/faults
    [[nodiscard]] MemResult<HostAddr> resolve_slow(GuestAddr addr, Property req_perm);

    static uintptr_t next_identity() {
        static std::atomic<uintptr_t> identity{1};
        return identity.fetch_add(1, std::memory_order_relaxed);
    }

    static MmuCore* create_core(std::unique_ptr<PageDirectory> page_directory) {
        auto* core = new MmuCore();
        core->page_directory = std::move(page_directory);
        core->identity = next_identity();
        core->state_flags = 0;
        return core;
    }

    static void retain_core(MmuCore* core) {
        if (!core) return;
        core->ref_count.fetch_add(1, std::memory_order_relaxed);
    }

    static void release_core(MmuCore* core) {
        if (!core) return;
        if (core->ref_count.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            delete core;
        }
    }

    void bind_core(MmuCore* core, bool add_ref) {
        if (core && add_ref) {
            retain_core(core);
        }

        auto* old_core = core_;
        core_ = core;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        release_core(old_core);
        tlb.flush();
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
    Mmu() {
        core_ = create_core(std::make_unique<PageDirectory>());
        page_dir = core_->page_directory.get();
    }

    Mmu(const Mmu& other) {
        core_ = other.core_;
        page_dir = other.page_dir;
        retain_core(core_);
    }

    Mmu(Mmu&& other) noexcept {
        core_ = other.core_;
        page_dir = other.page_dir;
        other.core_ = nullptr;
        other.page_dir = nullptr;
    }

    Mmu& operator=(const Mmu& other) {
        if (this == &other) return *this;
        retain_core(other.core_);
        auto* old_core = core_;
        core_ = other.core_;
        page_dir = other.page_dir;
        release_core(old_core);
        tlb.flush();
        return *this;
    }

    Mmu& operator=(Mmu&& other) noexcept {
        if (this == &other) return *this;
        release_core(core_);
        core_ = other.core_;
        page_dir = other.page_dir;
        other.core_ = nullptr;
        other.page_dir = nullptr;
        tlb.flush();
        return *this;
    }

    ~Mmu() {
        release_core(core_);
        core_ = nullptr;
        page_dir = nullptr;
    }

    static MmuCore* CreateEmptyCore() { return create_core(std::make_unique<PageDirectory>()); }

    static MmuCore* RetainCore(MmuCore* core) {
        retain_core(core);
        return core;
    }

    static void ReleaseCore(MmuCore* core) { release_core(core); }

    static MmuCore* CloneCoreSkipExternal(MmuCore* core) {
        if (!core || !core->page_directory) {
            return CreateEmptyCore();
        }

        auto cloned = std::make_unique<PageDirectory>();
        auto* source = core->page_directory.get();
        for (size_t l1 = 0; l1 < source->l1_directory.size(); ++l1) {
            const auto& src_chunk = source->l1_directory[l1];
            if (!src_chunk) continue;

            std::unique_ptr<PageTableChunk> dst_chunk;
            for (size_t l2 = 0; l2 < src_chunk->permissions.size(); ++l2) {
                const auto perms = src_chunk->permissions[l2];
                if (perms == Property::None) continue;
                if (has_property(perms, Property::External)) continue;

                if (!dst_chunk) {
                    dst_chunk = std::make_unique<PageTableChunk>();
                }

                dst_chunk->permissions[l2] = perms;
                if (src_chunk->pages[l2]) {
                    dst_chunk->pages[l2] = new std::byte[PAGE_SIZE];
                    std::memcpy(dst_chunk->pages[l2], src_chunk->pages[l2], PAGE_SIZE);
                }
            }

            if (dst_chunk) cloned->l1_directory[l1] = std::move(dst_chunk);
        }

        return create_core(std::move(cloned));
    }

    [[nodiscard]] MmuCore* core_handle() const { return core_; }

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

    [[nodiscard]] Property get_property(GuestAddr addr) const {
        if (!page_dir) return Property::None;
        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) return Property::None;
        return chunk->permissions[l2_idx];
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

            auto& entry_perms = page_dir->l1_directory[l1_idx]->permissions[l2_idx];
            // Preserve state bits across permission changes.
            // mprotect/mmap should not clear dirty tracking, and must keep external ownership marker.
            Property preserved = Property::None;
            if (has_property(entry_perms, Property::External)) preserved = preserved | Property::External;
            if (has_property(entry_perms, Property::Dirty)) preserved = preserved | Property::Dirty;
            entry_perms = perms | preserved;
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
        if (chunk->pages[l2_idx] && !has_property(chunk->permissions[l2_idx], Property::External)) {
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

    // API: reset_memory - Replace page directory contents with a fresh empty one.
    // Used during execve to clear all native pages before loading the new binary.
    // Keep core identity stable so C# MMU handles remain valid.
    void reset_memory() {
        if (!core_) {
            bind_core(CreateEmptyCore(), false);
            return;
        }
        core_->page_directory = std::make_unique<PageDirectory>();
        page_dir = core_->page_directory.get();
        tlb.flush();
    }

    [[nodiscard]] uintptr_t page_directory_identity() const { return core_ ? core_->identity : 0; }

    void flush_tlb_only() { tlb.flush(); }

    [[nodiscard]] MmuCore* detach_core() {
        if (!core_) {
            bind_core(CreateEmptyCore(), false);
        }

        auto* detached = RetainCore(core_);
        bind_core(CreateEmptyCore(), false);
        return detached;
    }

    void attach_core(MmuCore* core, bool add_ref = true) {
        if (!core) {
            bind_core(CreateEmptyCore(), false);
            return;
        }
        bind_core(core, add_ref);
    }

    // Internal helper for resolution (TLB or Slow) without hooks
    [[nodiscard]] FORCE_INLINE MemResult<HostAddr> resolve_ptr(GuestAddr addr, Property req_perm);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read_no_utlb(GuestAddr addr);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<void> write_no_utlb(GuestAddr addr, T val);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read_tlb_only(GuestAddr addr, MicroTLB* utlb);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<void> write_tlb_only(GuestAddr addr, T val, MicroTLB* utlb);

    template <typename T, bool fail_on_tlb_miss = false>
    [[nodiscard]] FORCE_INLINE MemResult<T> read(GuestAddr addr, MicroTLB* utlb, const ShimOp* cur_op);

    template <typename T, bool fail_on_tlb_miss = false>
    [[nodiscard]] FORCE_INLINE MemResult<void> write(GuestAddr addr, T val, MicroTLB* utlb, const ShimOp* cur_op);

    template <typename T, bool fail_on_tlb_miss = false>
    [[nodiscard]] FORCE_INLINE MemResult<T> read(GuestAddr addr, MicroTLB* utlb, std::nullptr_t) = delete;

    template <typename T, bool fail_on_tlb_miss = false>
    [[nodiscard]] FORCE_INLINE MemResult<void> write(GuestAddr addr, T val, MicroTLB* utlb, std::nullptr_t) = delete;

    // Slow Path: Hooks, Resolution, Faults
    template <typename T>
    [[nodiscard]] MemResult<T> read_slow(GuestAddr addr);

    template <typename T>
    [[nodiscard]] MemResult<void> write_slow(GuestAddr addr, T val);

    // Optimized Cross-Page Access
    template <typename T>
    [[nodiscard]] MemResult<T> read_cross_page(GuestAddr addr);

    template <typename T>
    [[nodiscard]] MemResult<void> write_cross_page(GuestAddr addr, T val);

    // Fetch Instruction Pointer
    [[nodiscard]] inline const std::byte* translate_exec(EmuState* state, GuestAddr addr);

    // Read for Execution (Instruction Fetch)
    template <typename T>
    [[nodiscard]] inline T read_for_exec(EmuState* state, GuestAddr addr);

    // Probe Execution (Check if address is executable without faulting)
    [[nodiscard]] inline bool probe_exec(GuestAddr addr) {
        if (!page_dir) return false;
        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        auto& chunk = page_dir->l1_directory[l1_idx];
        if (!chunk) return false;

        // Check only permissions. The page might be mapped but not yet populated (demand paging).
        // If it's mapped as Exec, we allow the decoder to "see" it speculatively.
        Property current_perm = chunk->permissions[l2_idx];
        return has_property(current_perm, Property::Exec);
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
