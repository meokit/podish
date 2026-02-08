#pragma once
#include "../state.h"  // Ensure EmuState is defined
#include "mmu.h"

namespace fiberish::mem {

// ----------------------------------------------------------------------------
// Mmu Implementation
// ----------------------------------------------------------------------------

}  // namespace fiberish::mem

namespace fiberish {
// Forward declare HandlerInterrupt to avoid circular dependency with dispatch.h
ATTR_PRESERVE_NONE int64_t HandlerInterrupt(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                            mem::MicroTLB utlb);
}  // namespace fiberish

namespace fiberish::mem {

// Signal Fault
FORCE_INLINE void Mmu::signal_fault(EmuState* state, uint32_t addr, int is_write, DecodedOp* op) {
    // Always Trigger Precise Fault mechanism to stop current block execution
    // and allow retry (if handled) or stop (if not).

    // 1. Rollback EIP
    if (op) state->ctx.eip = op->next_eip - op->length;

    // 2. Call User Handler (if any)
    if (fault_handler) {
        fault_handler(fault_opaque, addr, is_write);
    } else {
        state->status = EmuStatus::Fault;
        if (emu_fault_vector) *emu_fault_vector = 14;  // #PF
    }

    // 3. Swap Handler to Interrupt (Stop this block, return to RunLoop)
    if (state->status != EmuStatus::Running && op) {
        DecodedOp* next = op + 1;
        // Only swap if not already swapped (to avoid overwriting saved_handler)
        if (next->handler != (HandlerFunc)fiberish::HandlerInterrupt) {
            state->saved_handler = (int64_t (*)(EmuState*, DecodedOp*, int64_t, mem::MicroTLB))next->handler;
            next->handler = (HandlerFunc)fiberish::HandlerInterrupt;
        }
    }
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
[[nodiscard]] FORCE_INLINE MemResult<HostAddr> Mmu::resolve_slow(EmuState* state, GuestAddr addr, Property req_perm,
                                                                 DecodedOp* op) {
    if (!page_dir) return std::unexpected(FaultCode::PageFault);

    const uint32_t l1_idx = addr >> 22;
    const uint32_t l2_idx = (addr >> 12) & 0x3FF;
    const uint32_t offset = addr & 0xFFF;

    // 1. Check L1
    auto& chunk = page_dir->l1_directory[l1_idx];
    if (!chunk) {
        signal_fault(state, addr, (int)has_property(req_perm, Property::Write), op);
        if (state && state->status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);
        if (emu_status && *emu_status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        if (!page_dir->l1_directory[l1_idx]) {
            return std::unexpected(FaultCode::PageFault);
        }
    }

    // 2. Check Permissions
    Property current_perm = chunk->permissions[l2_idx];
    if (!has_property(current_perm, req_perm)) {
        signal_fault(state, addr, (int)has_property(req_perm, Property::Write), op);
        if (state && state->status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);
        if (emu_status && *emu_status != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);
        current_perm = chunk->permissions[l2_idx];
        if (!has_property(current_perm, req_perm)) return std::unexpected(FaultCode::PageFault);
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
        if (is_exec) {
            const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
            tlb.write_tlb[idx].tag = 1;
        }
    }

    return page_base + offset;
}

// Resolve Ptr
[[nodiscard]] FORCE_INLINE MemResult<HostAddr> Mmu::resolve_ptr(EmuState* state, GuestAddr addr, Property req_perm,
                                                                DecodedOp* op) {
    const size_t idx = (addr >> PAGE_SHIFT) & TLB_INDEX_MASK;
    const uint32_t tag = addr & ~PAGE_MASK;

    const auto& tlb_array = (req_perm == Property::Read) ? tlb.read_tlb : tlb.write_tlb;
    const auto entry = tlb_array[idx];

    if (entry.tag == tag) [[likely]] {
        return reinterpret_cast<HostAddr>(entry.addend + addr);
    }

    return resolve_slow(state, addr, req_perm, op);
}

// Read No UTLB
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<T> Mmu::read_no_utlb(EmuState* state, GuestAddr addr, DecodedOp* op) {
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
    return read_slow<T>(state, addr, op);
}

// Write No UTLB
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write_no_utlb(EmuState* state, GuestAddr addr, T val, DecodedOp* op) {
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
    return write_slow<T>(state, addr, val, op);
}

// Read
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<T> Mmu::read(EmuState* state, GuestAddr addr, MicroTLB* utlb, DecodedOp* op) {
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
    return read_slow<T>(state, addr, op);
}

// Write
template <typename T>
[[nodiscard]] FORCE_INLINE MemResult<void> Mmu::write(EmuState* state, GuestAddr addr, T val, MicroTLB* utlb,
                                                      DecodedOp* op) {
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
    return write_slow<T>(state, addr, val, op);
}

// Read Slow
template <typename T>
[[nodiscard]] MemResult<T> Mmu::read_slow(EmuState* state, GuestAddr addr, DecodedOp* op) {
    T val;
    if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
        auto res = read_cross_page<T>(state, addr, op);
        if (!res) return res;
        val = *res;
    } else {
        auto res = resolve_slow(state, addr, Property::Read, op);
        if (!res) return std::unexpected(res.error());
        val = *reinterpret_cast<T*>(*res);
    }

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

// Write Slow
template <typename T>
[[nodiscard]] MemResult<void> Mmu::write_slow(EmuState* state, GuestAddr addr, T val, DecodedOp* op) {
    if (mem_hook) {
        uint64_t hook_val = 0;
        if constexpr (sizeof(T) <= 8)
            std::memcpy(&hook_val, &val, sizeof(T));
        else
            std::memcpy(&hook_val, &val, 8);
        mem_hook(mem_hook_opaque, addr, sizeof(T), 1, hook_val);
    }

    if (((addr & PAGE_MASK) + sizeof(T)) > PAGE_SIZE) {
        return write_cross_page<T>(state, addr, val, op);
    }

    auto res = resolve_slow(state, addr, Property::Write, op);
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
[[nodiscard]] MemResult<T> Mmu::read_cross_page(EmuState* state, GuestAddr addr, DecodedOp* op) {
    static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for read_cross_page");

    uint32_t page_offset = addr & PAGE_MASK;
    uint32_t len1 = PAGE_SIZE - page_offset;
    uint32_t len2 = sizeof(T) - len1;

    GuestAddr addr2 = addr + len1;

    auto res1 = resolve_ptr(state, addr, Property::Read, op);
    if (!res1) return std::unexpected(res1.error());

    auto res2 = resolve_ptr(state, addr2, Property::Read, op);
    if (!res2) return std::unexpected(res2.error());

    T val;
    std::byte* dest = reinterpret_cast<std::byte*>(&val);
    std::memcpy(dest, *res1, len1);
    std::memcpy(dest + len1, *res2, len2);

    return val;
}

// Write Cross Page
template <typename T>
[[nodiscard]] MemResult<void> Mmu::write_cross_page(EmuState* state, GuestAddr addr, T val, DecodedOp* op) {
    static_assert(sizeof(T) <= PAGE_SIZE, "Access too large for write_cross_page");

    uint32_t page_offset = addr & PAGE_MASK;
    uint32_t len1 = PAGE_SIZE - page_offset;
    uint32_t len2 = sizeof(T) - len1;

    GuestAddr addr2 = addr + len1;

    auto res1 = resolve_ptr(state, addr, Property::Write, op);
    if (!res1) return std::unexpected(res1.error());

    auto res2 = resolve_ptr(state, addr2, Property::Write, op);
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

// Copy Block
[[nodiscard]] inline MemResult<void> Mmu::copy_block(EmuState* state, GuestAddr src_addr, GuestAddr dst_addr,
                                                     uint32_t size, DecodedOp* op) {
    if (mem_hook) {
        for (uint32_t i = 0; i < size; ++i) {
            auto val = this->read_no_utlb<uint8_t>(state, src_addr + i, op);
            if (!val) return std::unexpected(val.error());
            auto res = this->write_no_utlb<uint8_t>(state, dst_addr + i, *val, op);
            if (!res) return res;
        }
        return {};
    }

    uint32_t bytes_done = 0;
    while (bytes_done < size) {
        uint32_t curr_src = src_addr + bytes_done;
        uint32_t curr_dst = dst_addr + bytes_done;

        uint32_t src_rem = PAGE_SIZE - (curr_src & PAGE_MASK);
        uint32_t dst_rem = PAGE_SIZE - (curr_dst & PAGE_MASK);
        uint32_t chunk = std::min(size - bytes_done, std::min(src_rem, dst_rem));

        auto p_src_res = resolve_slow(state, curr_src, Property::Read, op);
        if (!p_src_res) return std::unexpected(p_src_res.error());

        auto p_dst_res = resolve_slow(state, curr_dst, Property::Write, op);
        if (!p_dst_res) return std::unexpected(p_dst_res.error());

        std::memmove(*p_dst_res, *p_src_res, chunk);
        bytes_done += chunk;
    }
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
        auto res = resolve_slow(state, addr, Property::Exec, nullptr);
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

    auto res = resolve_slow(state, addr, Property::Exec, nullptr);
    return reinterpret_cast<const std::byte*>(res.value_or(nullptr));
}

}  // namespace fiberish::mem
