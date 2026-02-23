#pragma once
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "../state.h"  // Ensure EmuState is defined
#include "mmu.h"

namespace fiberish::mem {

// ----------------------------------------------------------------------------
// Mmu Implementation
// ----------------------------------------------------------------------------

}  // namespace fiberish::mem

namespace fiberish::mem {

inline EmuState* Mmu::get_state() {
    const intptr_t offset = reinterpret_cast<intptr_t>(&(static_cast<EmuState*>(nullptr)->mmu));
    return reinterpret_cast<EmuState*>(reinterpret_cast<char*>(this) - offset);
}

// Signal Fault
[[nodiscard]] FORCE_INLINE EmuStatus Mmu::signal_fault(uint32_t addr, int is_write) {
    // Always Trigger Precise Fault mechanism to stop current block execution
    // and allow retry (if handled) or stop (if not).

    // 1. Rollback EIP
    // if (op) state->ctx.eip = op->next_eip - op->GetLength();
    // Now the caller need to rollback eip itself

    // 2. Call User Handler (if any)
    bool handled = false;
    if (fault_handler) {
        handled = (bool)fault_handler(fault_opaque, addr, is_write);
    }

    if (!handled) {
        get_state()->status = EmuStatus::Fault;
    }

    // Caller MUST check status, if not Running, exit from DecodedOp chain
    return get_state()->status;
}

FORCE_INLINE void Mmu::sync_dirty(GuestAddr vaddr) {
    const uint32_t l1_idx = vaddr >> 22;
    const uint32_t l2_idx = (vaddr >> 12) & 0x3FF;
    if (!page_dir) return;
    auto& chunk = page_dir->l1_directory[l1_idx];
    if (chunk) {
        chunk->permissions[l2_idx] = chunk->permissions[l2_idx] | Property::Dirty;
    }
}

// Resolve Slow
[[nodiscard]] FORCE_INLINE MemResult<HostAddr> Mmu::resolve_slow(GuestAddr addr, Property req_perm) {
    if (!page_dir) return std::unexpected(FaultCode::PageFault);

    const uint32_t l1_idx = addr >> 22;
    const uint32_t l2_idx = (addr >> 12) & 0x3FF;
    const uint32_t offset = addr & 0xFFF;

    // 1. Check L1
    // We do NOT use a reference here because signal_fault might invalidate it
    if (!page_dir->l1_directory[l1_idx]) {
        auto status = signal_fault(addr, (int)has_property(req_perm, Property::Write));
        if (status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        // Retry
        if (!page_dir || !page_dir->l1_directory[l1_idx]) {
            return std::unexpected(FaultCode::PageFault);
        }
    }

    // Now it is safe to acquire the pointer (but still safer to use raw pointer if we fear realloc)
    // We use raw pointer to the Chunk to avoid any unique_ptr ref weirdness
    auto* chunk = page_dir->l1_directory[l1_idx].get();

    // 2. Check Permissions
    Property current_perm = chunk->permissions[l2_idx];
    if (!has_property(current_perm, req_perm)) {
        auto status = signal_fault(addr, (int)has_property(req_perm, Property::Write));
        if (status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        // Retry: Need to re-fetch chunk/perm because handler might have changed them!
        if (!page_dir || !page_dir->l1_directory[l1_idx]) return std::unexpected(FaultCode::PageFault);

        chunk = page_dir->l1_directory[l1_idx].get();  // Refresh pointer
        current_perm = chunk->permissions[l2_idx];

        if (!has_property(current_perm, req_perm)) {
            return std::unexpected(FaultCode::PageFault);
        }
    }

    // Dirty Bit
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
        bool is_exec = has_property(current_perm, Property::Exec);
        tlb.fill(addr, page_base, current_perm);

        // Trap SMC on executable pages
        if (is_exec) {
            const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
            tlb.write_tlb[idx].tag = 1;
        }
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
    return read_slow<T>(addr);
}

// Write No UTLB
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write_no_utlb(GuestAddr addr, T val) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = end_addr & ~PAGE_MASK;
    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const auto entry = tlb.write_tlb[idx];

    if (entry.tag == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l2_write_hits++;
        stats.total_writes++;
#endif
        *reinterpret_cast<T*>(entry.addend + addr) = val;
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
    const uint32_t target_tag = end_addr & ~PAGE_MASK;

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
        utlb->tag_w =
            has_property(entry.perm, Property::Write) ? target_tag : std::numeric_limits<decltype(utlb->tag_w)>::max();
        return ptr;
    }

    utlb->invalidate();

#ifdef ENABLE_TLB_STATS
    stats.read_misses++;
    stats.total_reads++;
#endif

    return std::unexpected(FaultCode::PageFault);
}

template <typename T>
MemResult<void> Mmu::write_tlb_only(GuestAddr addr, T val, MicroTLB* utlb) {
    const GuestAddr end_addr = addr + sizeof(T) - 1;
    const uint32_t target_tag = end_addr & ~PAGE_MASK;

    if (utlb->tag_w == target_tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
        stats.l1_write_hits++;
        stats.total_writes++;
#endif
        *reinterpret_cast<T*>(utlb->addend + addr) = val;
        return {};
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
        utlb->tag_r =
            has_property(entry.perm, Property::Read) ? target_tag : std::numeric_limits<decltype(utlb->tag_r)>::max();
        return {};
    }

    utlb->tag_w = std::numeric_limits<decltype(utlb->tag_w)>::max();
#ifdef ENABLE_TLB_STATS
    stats.write_misses++;
    stats.total_writes++;
#endif

    return std::unexpected(FaultCode::PageFault);
}

// Read
template <typename T, bool fail_on_tlb_miss>
[[nodiscard]] FORCE_INLINE MemResult<T> Mmu::read(GuestAddr addr, MicroTLB* utlb, const ShimOp* cur_op) {
    if (auto result = read_tlb_only<T>(addr, utlb)) return result;

    get_state()->sync_eip_to_op_start(cur_op);

    if constexpr (fail_on_tlb_miss) return std::unexpected(FaultCode::PageFault);

    return read_slow<T>(addr);
}

// Write
template <typename T, bool fail_on_tlb_miss>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write(GuestAddr addr, T val, MicroTLB* utlb, const ShimOp* cur_op) {
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
        val = *reinterpret_cast<T*>(*res);
    }

    if (mem_hook) {
        uint64_t hook_val = 0;
        if constexpr (sizeof(T) <= 8) {
            std::memcpy(&hook_val, &val, sizeof(T));
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, hook_val);
        } else {
            // First trigger
            std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 0, hook_val);

            // The second Trigger
            std::memcpy(&hook_val, reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(&val) + 8), sizeof(T) - 8);
            mem_hook(mem_hook_opaque, addr + 8, sizeof(T) - 8, 0, hook_val);
        }
    }

    return val;
}

// Write Slow
template <typename T>
[[nodiscard]] MemResult<void> Mmu::write_slow(GuestAddr addr, T val) {
    if (mem_hook) {
        uint64_t hook_val = 0;
        if constexpr (sizeof(T) <= 8) {
            std::memcpy(&hook_val, &val, sizeof(T));
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);
        } else {
            // First trigger
            std::memcpy(&hook_val, &val, 8);
            mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);

            // The second Trigger
            std::memcpy(&hook_val, reinterpret_cast<void*>(reinterpret_cast<uintptr_t>(&val) + 8), sizeof(T) - 8);
            mem_hook(mem_hook_opaque, addr + 8, sizeof(T) - 8, 1, hook_val);
        }
    }

    if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
        return write_cross_page<T>(addr, val);
    }

    auto res = resolve_slow(addr, Property::Write);
    if (!res) return std::unexpected(res.error());
    HostAddr ptr = *res;

    if (ptr && smc_handler) {
        Property p = get_property(addr);
        if (has_property(p, Property::Exec)) {
            smc_handler(smc_opaque, addr);
        }
    }

    if (ptr) *reinterpret_cast<T*>(ptr) = val;
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

    auto res1 = resolve_ptr(addr, Property::Write);
    if (!res1) return std::unexpected(res1.error());

    auto res2 = resolve_ptr(addr2, Property::Write);
    if (!res2) return std::unexpected(res2.error());

    if (smc_handler) {
        Property prop1 = get_property(addr);
        if (has_property(prop1, Property::Exec)) smc_handler(smc_opaque, addr);

        Property prop2 = get_property(addr2);
        if (has_property(prop2, Property::Exec)) smc_handler(smc_opaque, addr2);
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
    return *reinterpret_cast<T*>(ptr);
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
