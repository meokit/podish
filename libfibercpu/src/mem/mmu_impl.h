#pragma once
#include <cstring>
#include <limits>
#include "../logger.h"
#include "../state.h"  // Ensure EmuState is defined
#include "mmu.h"

namespace fiberish::mem {

static FORCE_INLINE bool ShouldInterceptExecWriteForSmc(EmuState* state, GuestAddr addr) {
    if (!state->intercept_exec_write_for_smc || state->allow_write_exec_page) return false;

    const uint32_t current_page = state->ctx.eip >> 12;
    const uint32_t target_page = addr >> 12;
    return target_page == current_page || target_page == (current_page + 1);
}

inline EmuState* Mmu::get_state() {
    const intptr_t offset = reinterpret_cast<intptr_t>(&(static_cast<EmuState*>(nullptr)->mmu));
    return reinterpret_cast<EmuState*>(reinterpret_cast<char*>(this) - offset);
}

inline void Mmu::on_code_cache_hard_reset() {
    clear_block_lookup_cache();
    seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
    if (auto* state = get_state()) {
        state->last_block = invalid_code_block();
    }
}

inline void* Mmu::allocate_block_bytes(size_t size, uint32_t guest_eip) {
    sync_code_cache_generation();

    auto& cache = code_cache();
    if (cache.ShouldFlushForAllocation(size)) {
        const size_t bytes_before_flush = cache.requested_block_bytes;
        const size_t valid_blocks_before_flush = cache.CountValidBlocks();
        const size_t total_blocks_before_flush = cache.all_blocks.size();
        const uint64_t next_flush_count = cache.flush_count + 1;
        const uintptr_t mmu_identity = core_ ? core_->identity : 0;

        LogMsgState(get_state(), LogWarning,
                    "[CodeCacheBudgetFlush] mmu=0x%zx budget=%zu bytes_before=%zu alloc=%zu valid_blocks=%zu "
                    "all_blocks=%zu flush_count=%llu guest_eip=0x%08x",
                    static_cast<size_t>(mmu_identity), cache.block_budget_bytes, bytes_before_flush, size,
                    valid_blocks_before_flush, total_blocks_before_flush,
                    static_cast<unsigned long long>(next_flush_count), guest_eip);

        cache.HardResetReleaseAll(1, 1);
        on_code_cache_hard_reset();
    }

    void* block_mem = cache.block_pool.allocate(size);
    cache.NoteAllocatedBlockBytes(size);
    return block_mem;
}

// Signal Fault
[[nodiscard]] FORCE_INLINE EmuStatus Mmu::signal_fault(uint32_t addr, int is_write) {
    // Always Trigger Precise Fault mechanism to stop current block execution
    // and allow retry (if handled) or stop (if not).

    bool handled = false;
    if (fault_handler) {
        handled = (bool)fault_handler(fault_opaque, addr, is_write);
    }

    if (!handled) {
        get_state()->status = EmuStatus::Fault;
    }

    // Caller MUST check status; if not Running, exit from DecodedOp chain
    return get_state()->status;
}

FORCE_INLINE void Mmu::sync_dirty(GuestAddr vaddr) {
    const uint32_t l1_idx = vaddr >> 22;
    const uint32_t l2_idx = (vaddr >> 12) & 0x3FF;
    auto* dir = current_page_directory();
    if (!dir) return;
    auto& chunk = dir->l1_directory[l1_idx];
    if (chunk) {
        chunk->permissions[l2_idx] = chunk->permissions[l2_idx] | Property::Dirty;
    }
}

// Resolve Slow
[[nodiscard]] FORCE_INLINE MemResult<HostAddr> Mmu::resolve_slow(GuestAddr addr, Property req_perm) {
    auto* dir = current_page_directory();
    if (!dir) return std::unexpected(FaultCode::PageFault);

    const uint32_t l1_idx = addr >> 22;
    const uint32_t l2_idx = (addr >> 12) & 0x3FF;
    const uint32_t offset = addr & 0xFFF;

    // 1. Check L1 Directory
    if (!dir->l1_directory[l1_idx]) {
        auto status = signal_fault(addr, (int)has_property(req_perm, Property::Write));
        if (status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        // Retry after fault handler
        dir = current_page_directory();
        if (!dir || !dir->l1_directory[l1_idx]) {
            return std::unexpected(FaultCode::PageFault);
        }
    }

    auto* chunk = dir->l1_directory[l1_idx].get();

    // 2. Check Permissions
    Property current_perm = chunk->permissions[l2_idx];
    if (!has_property(current_perm, req_perm)) {
        auto status = signal_fault(addr, (int)has_property(req_perm, Property::Write));
        if (status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        // Retry: Refetch chunk/perm because handler might have changed them
        dir = current_page_directory();
        if (!dir || !dir->l1_directory[l1_idx]) return std::unexpected(FaultCode::PageFault);

        chunk = dir->l1_directory[l1_idx].get();
        current_perm = chunk->permissions[l2_idx];

        if (!has_property(current_perm, req_perm)) {
            return std::unexpected(FaultCode::PageFault);
        }
    }

    // Mark Dirty Bit if writing
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

    // 4. Fill TLB
    if (!mem_hook) {
        tlb.fill(addr, page_base, current_perm);
    }

    return page_base + offset;
}

// Resolve Ptr
[[nodiscard]] FORCE_INLINE MemResult<HostAddr> Mmu::resolve_ptr(GuestAddr addr, Property req_perm) {
    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const uint32_t tag = addr & ~PAGE_MASK;

    const auto& tlb_array = (req_perm == Property::Read) ? tlb.read_tlb : tlb.write_tlb;
    const auto entry = tlb_array[idx];

    if (entry.tag == tag) [[likely]] {
        return reinterpret_cast<HostAddr>(entry.addend + addr);
    }

    return resolve_slow(addr, req_perm);
}

// Read No UTLB
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<T> Mmu::read_no_utlb(GuestAddr addr) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = addr & ~PAGE_MASK;
    const uint32_t cross_page_mask = (addr ^ end_addr) & ~PAGE_MASK;

    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto entry = tlb.read_tlb[idx];

    if (((entry.tag ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l2_read_hits++;
        stats.total_reads++;
#endif
        T val;
        std::memcpy(&val, (const void*)(entry.addend + addr), sizeof(T));
        return val;
    }

#ifdef ENABLE_TLB_STATS
    stats.read_misses++;
    stats.total_reads++;
#endif
    return read_slow<T>(addr);
}

// Write No UTLB
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write_no_utlb(GuestAddr addr, T val) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = addr & ~PAGE_MASK;
    const uint32_t cross_page_mask = (addr ^ end_addr) & ~PAGE_MASK;

    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto entry = tlb.write_tlb[idx];

    if (((entry.tag ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l2_write_hits++;
        stats.total_writes++;
#endif
        std::memcpy((void*)(entry.addend + addr), &val, sizeof(T));
        return {};
    }

#ifdef ENABLE_TLB_STATS
    stats.write_misses++;
    stats.total_writes++;
#endif
    return write_slow<T>(addr, val);
}

template <typename T>
MemResult<T> Mmu::read_tlb_only(GuestAddr addr, MicroTLB* utlb) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = addr & ~PAGE_MASK;
    const uint32_t cross_page_mask = (addr ^ end_addr) & ~PAGE_MASK;

    if (((utlb->tag_r ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l1_read_hits++;
        stats.total_reads++;
#endif
        T val;
        std::memcpy(&val, (const void*)(utlb->addend + addr), sizeof(T));
        return val;
    }

    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto entry = tlb.read_tlb[idx];

    if (((entry.tag ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l2_read_hits++;
        stats.total_reads++;
#endif
        T val;
        std::memcpy(&val, (const void*)(entry.addend + addr), sizeof(T));

        utlb->tag_r = target_tag;
        utlb->addend = entry.addend;
        // Read-path refill must stay cheap: whether writes may fast-path is
        // encoded entirely in ForceWriteSlow, with no alias lookup here.
        utlb->tag_w = (has_property(entry.perm, Property::Write) && !has_property(entry.perm, Property::ForceWriteSlow))
                          ? target_tag
                          : std::numeric_limits<decltype(utlb->tag_w)>::max();
        return val;
    }

    InvalidateMicroTLB(utlb);

#ifdef ENABLE_TLB_STATS
    stats.read_misses++;
    stats.total_reads++;
#endif

    return std::unexpected(FaultCode::PageFault);
}

template <typename T>
MemResult<void> Mmu::write_tlb_only(GuestAddr addr, T val, MicroTLB* utlb) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = addr & ~PAGE_MASK;
    const uint32_t cross_page_mask = (addr ^ end_addr) & ~PAGE_MASK;

    if (((utlb->tag_w ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l1_write_hits++;
        stats.total_writes++;
#endif
        std::memcpy((void*)(utlb->addend + addr), &val, sizeof(T));
        return {};
    }

    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto entry = tlb.write_tlb[idx];

    if (((entry.tag ^ target_tag) | cross_page_mask) == 0) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l2_write_hits++;
        stats.total_writes++;
#endif
        std::memcpy((void*)(entry.addend + addr), &val, sizeof(T));

        utlb->tag_w = target_tag;
        utlb->addend = entry.addend;
        utlb->tag_r =
            has_property(entry.perm, Property::Read) ? target_tag : std::numeric_limits<decltype(utlb->tag_r)>::max();
        return {};
    }

    // A write miss can be followed by a slow-path COW remap on this page.
    // Invalidate both read/write micro-TLB tags to avoid stale read hits
    // against the pre-remap addend in the same translated block.
    InvalidateMicroTLB(utlb);
    tlb.flush_page(addr);
#ifdef ENABLE_TLB_STATS
    stats.write_misses++;
    stats.total_writes++;
#endif

    return std::unexpected(FaultCode::PageFault);
}

// Read
template <typename T, bool fail_on_tlb_miss>
[[nodiscard]] FORCE_INLINE MemResult<T> Mmu::read(GuestAddr addr, MicroTLB* utlb, const DecodedOp* cur_op) {
    if (auto result = read_tlb_only<T>(addr, utlb)) return result;
    get_state()->sync_eip_to_op_start(cur_op);
    if constexpr (fail_on_tlb_miss) return std::unexpected(FaultCode::PageFault);
    return read_slow<T>(addr);
}

// Write
template <typename T, bool fail_on_tlb_miss>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write(GuestAddr addr, T val, MicroTLB* utlb, const DecodedOp* cur_op) {
    if (auto result = write_tlb_only<T>(addr, val, utlb)) return result;
    get_state()->sync_eip_to_op_start(cur_op);
    if constexpr (fail_on_tlb_miss) return std::unexpected(FaultCode::PageFault);
    return write_slow<T>(addr, val);
}

// Read Slow
template <typename T>
[[nodiscard]] MemResult<T> Mmu::read_slow(GuestAddr addr) {
    T val;
    if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
        auto res = read_cross_page<T>(addr);
        if (!res) return res;
        val = *res;
    } else {
        auto res = resolve_slow(addr, Property::Read);
        if (!res) return std::unexpected(res.error());
        // Safe copy for unaligned slow-path access
        std::memcpy(&val, *res, sizeof(T));
    }

    if (mem_hook) {
        uint64_t hook_val = 0;
        if constexpr (sizeof(T) <= 8) {
            std::memcpy(&hook_val, &val, sizeof(T));
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, hook_val);
        } else {
            std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, 8, 0, hook_val);

            std::memcpy(&hook_val, reinterpret_cast<const void*>(reinterpret_cast<uintptr_t>(&val) + 8), sizeof(T) - 8);
            mem_hook(mem_hook_opaque, addr + 8, sizeof(T) - 8, 0, hook_val);
        }
    }

    return val;
}

// Write Slow
template <typename T>
[[nodiscard]] MemResult<void> Mmu::write_slow(GuestAddr addr, T val) {
    if (smc_handler) {
        Property p = get_property(addr);
        if (has_property(p, Property::ForceWriteSlow)) {
            EmuState* state = get_state();
            if (ShouldInterceptExecWriteForSmc(state, addr)) {
                state->smc_write_to_exec = true;
                return std::unexpected(FaultCode::PageFault);
            }
            smc_handler(smc_opaque, addr);
        }
    }

    if (mem_hook) {
        uint64_t hook_val = 0;
        if constexpr (sizeof(T) <= 8) {
            std::memcpy(&hook_val, &val, sizeof(T));
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);
        } else {
            std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, 8, 1, hook_val);

            std::memcpy(&hook_val, reinterpret_cast<const void*>(reinterpret_cast<uintptr_t>(&val) + 8), sizeof(T) - 8);
            mem_hook(mem_hook_opaque, addr + 8, sizeof(T) - 8, 1, hook_val);
        }
    }

    if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
        return write_cross_page<T>(addr, val);
    }

    auto res = resolve_slow(addr, Property::Write);
    if (!res) return std::unexpected(res.error());
    HostAddr ptr = *res;

    if (ptr) {
        // Safe copy for unaligned slow-path access
        std::memcpy(ptr, &val, sizeof(T));
    }
    return {};
}

// Read Cross Page
template <typename T>
[[nodiscard]] MemResult<T> Mmu::read_cross_page(GuestAddr addr) {
    static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for read_cross_page");

    uint32_t page_offset = addr & PAGE_MASK;
    uint32_t len1 = PAGE_SIZE - page_offset;
    uint32_t len2 = sizeof(T) - len1;

    GuestAddr addr2 = addr + len1;

    auto res1 = resolve_ptr(addr, Property::Read);
    if (!res1) return std::unexpected(res1.error());

    auto res2 = resolve_ptr(addr2, Property::Read);
    if (!res2) return std::unexpected(res2.error());

    T val;
    std::byte* dest = reinterpret_cast<std::byte*>(&val);
    std::memcpy(dest, *res1, len1);
    std::memcpy(dest + len1, *res2, len2);

    return val;
}

// Write Cross Page
template <typename T>
[[nodiscard]] MemResult<void> Mmu::write_cross_page(GuestAddr addr, T val) {
    static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for write_cross_page");

    uint32_t page_offset = addr & PAGE_MASK;
    uint32_t len1 = PAGE_SIZE - page_offset;
    uint32_t len2 = sizeof(T) - len1;

    GuestAddr addr2 = addr + len1;

    if (smc_handler) {
        Property prop1 = get_property(addr);
        Property prop2 = get_property(addr2);
        const bool touches_exec =
            has_property(prop1, Property::ForceWriteSlow) || has_property(prop2, Property::ForceWriteSlow);
        if (touches_exec) {
            EmuState* state = get_state();
            if ((has_property(prop1, Property::ForceWriteSlow) && ShouldInterceptExecWriteForSmc(state, addr)) ||
                (has_property(prop2, Property::ForceWriteSlow) && ShouldInterceptExecWriteForSmc(state, addr2))) {
                state->smc_write_to_exec = true;
                return std::unexpected(FaultCode::PageFault);
            }
        }
    }

    auto res1 = resolve_ptr(addr, Property::Write);
    if (!res1) return std::unexpected(res1.error());

    auto res2 = resolve_ptr(addr2, Property::Write);
    if (!res2) return std::unexpected(res2.error());

    if (smc_handler) {
        Property prop1 = get_property(addr);
        if (has_property(prop1, Property::ForceWriteSlow)) smc_handler(smc_opaque, addr);

        Property prop2 = get_property(addr2);
        if (has_property(prop2, Property::ForceWriteSlow)) smc_handler(smc_opaque, addr2);
    }

    std::byte* src = reinterpret_cast<std::byte*>(&val);
    std::memcpy(*res1, src, len1);
    std::memcpy(*res2, src + len1, len2);
    return {};
}

// Read For Exec
template <typename T>
[[nodiscard]] inline T Mmu::read_for_exec(EmuState* state, GuestAddr addr) {
    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto& entry = tlb.exec_tlb[idx];
    const uint32_t tag = addr & ~PAGE_MASK;

    HostAddr ptr = nullptr;
    if (entry.tag == tag) {
        ptr = reinterpret_cast<HostAddr>(entry.addend + addr);
    } else {
        auto res = resolve_slow(addr, Property::Exec);
        ptr = res.value_or(nullptr);
    }

    if (!ptr) return T{};

    T val;
    // Use memcpy for safety, assuming exec reads could also be unaligned
    std::memcpy(&val, ptr, sizeof(T));
    return val;
}

// Translate Exec
[[nodiscard]] inline const std::byte* Mmu::translate_exec(EmuState* state, GuestAddr addr) {
    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto& entry = tlb.exec_tlb[idx];
    const uint32_t tag = addr & ~PAGE_MASK;

    if (entry.tag == tag) {
        return reinterpret_cast<const std::byte*>(entry.addend + addr);
    }

    auto res = resolve_slow(addr, Property::Exec);
    return reinterpret_cast<const std::byte*>(res.value_or(nullptr));
}

}  // namespace fiberish::mem
