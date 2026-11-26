#if FIBERCPU_GUEST_AARCH64
#include "mmu.h"

#include "../logger.h"
#include "../state.h"

namespace fiberish::mem {

void Mmu::enable_arm_address_space() {
    if (!core_ || has_arm_address_space()) return;
    auto pages = std::make_unique<ArmPageTable>();
    if (const auto* directory = current_page_directory()) {
        for (uint32_t i = 0; i < directory->l1_directory.size(); ++i) {
            const auto& chunk = directory->l1_directory[i];
            if (!chunk) continue;
            for (uint32_t j = 0; j < chunk->pages.size(); ++j) {
                if (auto* page = chunk->pages[j])
                    pages->Ensure((i << 22) | (j << PAGE_SHIFT)) = {page,
                                                                    chunk->permissions[j] & ~Property::ForceWriteSlow};
            }
        }
    }

    if (core_->code_cache.requested_block_bytes) reset_code_cache();
    // While i386 runs, the ARM table retains only mappings outside its range.
    if (core_->arm_pages) {
        core_->arm_pages->ReplaceCompatMappings(*pages);
    } else {
        core_->arm_pages = std::move(pages);
    }
    core_->page_directory.reset();
    page_dir = nullptr;
    core_->host_page_aliases.clear();
    core_->arm_pages->VisitMapped(0, ArmTaskSize, [&](uint64_t address, const ArmPageTable::Entry& entry) {
        add_host_page_alias(ArmPageTag(address), entry.page, entry.permissions);
        return true;
    });
    tlb.flush();
    invalidate_arm_tlb_all();
}

#if FIBERCPU_GUEST_I386
void Mmu::enable_i386_address_space() {
    if (!has_arm_address_space()) return;
    auto directory = std::make_unique<PageDirectory>();
    core_->arm_pages->VisitMapped(0, uint64_t{1} << 32, [&](uint64_t address, const ArmPageTable::Entry& entry) {
        const auto guest_page = static_cast<GuestAddr>(address);
        auto& chunk = directory->l1_directory[guest_page >> 22];
        if (!chunk) chunk = std::make_unique<PageTableChunk>();
        const auto slot = (guest_page >> PAGE_SHIFT) & 0x3ff;
        chunk->pages[slot] = entry.page;
        chunk->permissions[slot] = entry.permissions;
        return true;
    });

    if (core_->code_cache.requested_block_bytes) reset_code_cache();
    core_->arm_pages->DropCompatMappings();
    core_->page_directory = std::move(directory);
    page_dir = core_->page_directory.get();
    core_->host_page_aliases.clear();
    IndexI386HostPages(*core_);
    tlb.flush();
    // Each ISA uses a different tag representation for its shootdowns.
    publish_runtime_tlb_flush_all();
}
#endif

MmuCore* Mmu::CloneArmCoreMappings(MmuCore& core) {
    auto pages = std::make_unique<ArmPageTable>(*core.arm_pages);
    auto* clone = create_core(nullptr, &core);
    pages->VisitMapped(0, ArmTaskSize, [&](uint64_t address, ArmPageTable::Entry& entry) {
        entry.permissions = entry.permissions & ~Property::ForceWriteSlow;
        auto& aliases = clone->host_page_aliases[reinterpret_cast<uintptr_t>(entry.page)];
        aliases.guest_pages.push_unique(ArmPageTag(address));
        if (has_property(entry.permissions, Property::Exec)) ++aliases.exec_count;
        return true;
    });
    clone->arm_pages = std::move(pages);
    return clone;
}

ArmMmuRuntime& Mmu::arm_runtime() {
    auto& runtime = get_state()->arm_mmu;
    if (!runtime) runtime = std::make_unique<ArmMmuRuntime>();
    return *runtime;
}

void Mmu::sync_arm_tlb_shootdowns(ArmMmuRuntime& runtime) {
    const auto& ring = core_->runtime_tlb_shootdown_ring;
    const auto write_seq = ring.write_seq;
    constexpr auto capacity = MmuCore::RuntimeTlbShootdownRing::kCapacity;
    if (runtime.identity != core_->identity || write_seq - runtime.tlb_read_seq > capacity) {
        runtime.tlb.flush();
        runtime.identity = core_->identity;
    } else {
        for (auto seq = runtime.tlb_read_seq; seq < write_seq; ++seq)
            runtime.tlb.flush_arm_page(ring.guest_pages[seq % capacity]);
    }
    runtime.tlb_read_seq = write_seq;
}

void Mmu::invalidate_arm_tlb_page(uint64_t address) {
    auto& runtime = arm_runtime();
    sync_arm_tlb_shootdowns(runtime);
    const auto tag = ArmPageTag(address);
    runtime.tlb.flush_arm_page(tag);
    auto& ring = core_->runtime_tlb_shootdown_ring;
    ring.guest_pages[ring.write_seq % ring.guest_pages.size()] = tag;
    runtime.tlb_read_seq = ++ring.write_seq;
}

void Mmu::invalidate_arm_tlb_all() {
    auto& runtime = arm_runtime();
    runtime.tlb.flush();
    auto& ring = core_->runtime_tlb_shootdown_ring;
    ring.write_seq += ring.guest_pages.size() + 1;
    runtime.identity = core_->identity;
    runtime.tlb_read_seq = ring.write_seq;
}

void Mmu::set_arm_fault_callback(ArmFaultHandler handler, void* userdata) {
    auto& runtime = arm_runtime();
    runtime.fault_handler = handler;
    runtime.fault_userdata = userdata;
}

void Mmu::set_arm_mem_hook(ArmMemHook handler, void* userdata) {
    auto& runtime = arm_runtime();
    runtime.mem_hook = handler;
    runtime.mem_userdata = userdata;
    runtime.tlb.flush();
}

bool Mmu::has_arm_mem_hook() const {
    const auto& runtime = const_cast<Mmu*>(this)->get_state()->arm_mmu;
    return mem_hook || (runtime && runtime->mem_hook);
}

void Mmu::flush_arm_tlb_for_hook() {
    if (const auto& runtime = get_state()->arm_mmu) runtime->tlb.flush();
}

const SoftTlb* Mmu::arm_data_tlb() {
    auto& runtime = arm_runtime();
    sync_arm_tlb_shootdowns(runtime);
    return has_arm_mem_hook() ? nullptr : &runtime.tlb;
}

EmuStatus Mmu::signal_arm_fault(uint64_t address, Property permission) {
    auto* state = get_state();
    auto& runtime = arm_runtime();
    if (runtime.fault_handler) {
        const auto previous_vector = state->fault_vector;
        const auto previous_fault = state->arm_fault_address;
        state->fault_vector = 14;
        state->arm_fault_address = address;
        const bool write = has_property(permission, Property::Write);
        const bool execute = has_property(permission, Property::Exec);
        const int flags = (write ? 1 : 0) | (execute ? 2 : 0) |
                          (execute || (state->can_restart_memory_fault && !has_arm_mem_hook()) ? 4 : 0);
        if (runtime.fault_handler(state, address, flags, runtime.fault_userdata)) {
            if (state->status != EmuStatus::Fault) {
                state->fault_vector = previous_vector;
                state->arm_fault_address = previous_fault;
            }
            return state->status;
        }
    } else if (address <= UINT32_MAX && fault_handler) {
        const auto previous_fault = state->arm_fault_address;
        state->arm_fault_address = address;
        const auto status = signal_fault(static_cast<uint32_t>(address), permission);
        if (status != EmuStatus::Fault) state->arm_fault_address = previous_fault;
        return status;
    }
    state->arm_fault_address = address;
    state->fault_vector = 14;
    return state->status = EmuStatus::Fault;
}

void Mmu::call_arm_mem_hook(uint64_t address, const void* value, size_t size, bool write) {
    const auto& runtime = arm_runtime();
    const auto* bytes = static_cast<const std::byte*>(value);
    for (size_t offset = 0; offset < size; offset += sizeof(uint64_t)) {
        uint64_t bits = 0;
        const auto count = std::min(sizeof bits, size - offset);
        std::memcpy(&bits, bytes + offset, count);
        if (runtime.mem_hook) {
            runtime.mem_hook(get_state(), address + offset, static_cast<uint32_t>(count), write, bits,
                             runtime.mem_userdata);
        } else if (mem_hook && address + offset <= UINT32_MAX) {
            mem_hook(mem_hook_opaque, static_cast<uint32_t>(address + offset), static_cast<uint32_t>(count), write,
                     bits);
        }
    }
}

Property Mmu::get_arm_property(uint64_t address) const {
    const auto* entry = has_arm_address_space() ? core_->arm_pages->Find(address) : nullptr;
    return entry ? entry->permissions : Property::None;
}

HostAddr Mmu::resolve_arm_safe_for_read(uint64_t address, Property permission) {
    const auto* entry = has_arm_address_space() ? core_->arm_pages->Find(address) : nullptr;
    if (!entry || !entry->page || !has_property(entry->permissions, permission)) return nullptr;
    return entry->page + (address & PAGE_MASK);
}

HostAddr Mmu::resolve_arm_safe_for_write(uint64_t address) {
    auto* entry = has_arm_address_space() ? core_->arm_pages->Find(address) : nullptr;
    if (!entry || !entry->page || !has_property(entry->permissions, Property::Write)) return nullptr;
    entry->permissions = entry->permissions | Property::Dirty;
    if (has_property(entry->permissions, Property::ForceWriteSlow)) invalidate_arm_code_cache_page(address);
    return entry->page + (address & PAGE_MASK);
}

MemResult<HostAddr> Mmu::resolve_arm_slow(uint64_t address, Property permission) {
    auto find = [&] { return has_arm_address_space() ? core_->arm_pages->Find(address) : nullptr; };
    auto* entry = find();
    if (!entry || !entry->page || !has_property(entry->permissions, permission)) {
        if (signal_arm_fault(address, permission) != EmuStatus::Running) return std::unexpected(FaultCode::PageFault);

        // Fault resolution can load file pages, remap COW, or swap the core.
        entry = find();
        if (!entry || !entry->page || !has_property(entry->permissions, permission))
            return std::unexpected(FaultCode::PageFault);
    }

    if (has_property(permission, Property::Write)) {
        entry->permissions = entry->permissions | Property::Dirty;
        if (has_property(entry->permissions, Property::ForceWriteSlow)) invalidate_arm_code_cache_page(address);
    }
    if (!has_arm_mem_hook()) {
        auto& runtime = arm_runtime();
        sync_arm_tlb_shootdowns(runtime);
        runtime.tlb.fill_arm(address, entry->page, entry->permissions);
    }
    return entry->page + (address & PAGE_MASK);
}

MemResult<HostAddr> Mmu::resolve_arm_ptr(uint64_t address, Property permission) {
    if (!ArmAddressRangeValid(address, 1)) return std::unexpected(FaultCode::PageFault);
    auto& runtime = arm_runtime();
    sync_arm_tlb_shootdowns(runtime);
    const auto tag = ArmPageTag(address);
    if (permission == Property::Read || has_property(permission, Property::Write)) {
        const auto index = tag & TLB_INDEX_MASK;
        const auto& entry = permission == Property::Read ? runtime.tlb.read_tlb[index] : runtime.tlb.write_tlb[index];
        const bool read = !has_property(permission, Property::Read) || entry.ReadTag() == tag;
        const bool write = !has_property(permission, Property::Write) || entry.WriteTag() == tag;
        if (read && write) return reinterpret_cast<HostAddr>(entry.addend + static_cast<uintptr_t>(address));
    } else if (permission == Property::Exec) {
        const auto& entry = runtime.tlb.exec_tlb[tag & (EXEC_TLB_ENTRIES - 1)];
        if (entry.tag == tag) return reinterpret_cast<HostAddr>(entry.addend + static_cast<uintptr_t>(address));
    }
    return resolve_arm_slow(address, permission);
}

const std::byte* Mmu::translate_arm_exec(EmuState*, uint64_t address) {
    return resolve_arm_ptr(address, Property::Exec).value_or(nullptr);
}

const std::byte* Mmu::try_arm_executable_page(uint64_t address) {
    if (!ArmAddressRangeValid(address, PAGE_SIZE) || (address & PAGE_MASK) || has_arm_mem_hook()) return nullptr;
    auto& runtime = arm_runtime();
    sync_arm_tlb_shootdowns(runtime);
    const auto tag = ArmPageTag(address);
    auto& entry = runtime.tlb.exec_tlb[tag & (EXEC_TLB_ENTRIES - 1)];
    if (entry.tag == tag) return reinterpret_cast<const std::byte*>(entry.addend + static_cast<uintptr_t>(address));
    auto* page = resolve_arm_safe_for_read(address, Property::Exec);
    if (page) entry = {tag, Property::Exec, reinterpret_cast<uintptr_t>(page) - static_cast<uintptr_t>(address)};
    return page;
}

void Mmu::refresh_arm_smc_armed_for_host_page(HostAddr page) {
    const auto it = core_->host_page_aliases.find(reinterpret_cast<uintptr_t>(page));
    if (it == core_->host_page_aliases.end()) return;

    const bool write_slow = it->second.exec_count && host_page_has_live_blocks(page);
    for (uint32_t tag : it->second.guest_pages) {
        const uint64_t address = static_cast<uint64_t>(tag) << PAGE_SHIFT;
        auto* entry = core_->arm_pages->Find(address);
        if (!entry || entry->page != page) continue;
        const auto permissions = entry->permissions;
        auto updated = permissions & ~Property::ForceWriteSlow;
        if (write_slow && has_property(updated, Property::Write)) updated = updated | Property::ForceWriteSlow;
        if (updated == permissions) continue;
        entry->permissions = updated;
        invalidate_arm_tlb_page(address);
    }
}

bool Mmu::map_arm_host_page(uint64_t address, HostAddr page, uint8_t requested) {
    if (!ArmAddressRangeValid(address, PAGE_SIZE) || (address & PAGE_MASK) || !page) return false;
    enable_arm_address_space();
    if (!has_arm_address_space()) return false;

    auto& entry = core_->arm_pages->Ensure(address);
    auto permissions = static_cast<Property>(requested) & (Property::RWX | Property::StableBacking);
    auto* before = entry.page;
    const auto previous_permissions = entry.permissions;
    if (before == page) {
        permissions = permissions | (previous_permissions & Property::Dirty);
        invalidate_code_before_permission_change(before, previous_permissions, permissions);
    } else if (before && has_property(previous_permissions, Property::Exec)) {
        invalidate_arm_code_cache_page(address);
    }
    entry = {page, permissions};
    update_host_page_alias(ArmPageTag(address), before, previous_permissions, page, permissions);
    invalidate_arm_tlb_page(address);
    return true;
}

void Mmu::protect_arm_mapped_range(uint64_t address, uint64_t size, uint8_t requested) {
    if (!has_arm_address_space() || !size) return;
    const auto permission = static_cast<Property>(requested) & Property::RWX;
    core_->arm_pages->VisitMapped(address, size, [&](uint64_t page, ArmPageTable::Entry& entry) {
        const auto previous = entry.permissions;
        const auto next = permission | (previous & (Property::Dirty | Property::StableBacking));
        if ((previous & ~Property::ForceWriteSlow) == next) return true;
        invalidate_code_before_permission_change(entry.page, previous, next);
        entry.permissions = next;
        update_host_page_alias(ArmPageTag(page), entry.page, previous, entry.page, next);
        invalidate_arm_tlb_page(page);
        return true;
    });
}

void Mmu::unmap_arm_range(uint64_t address, uint64_t size) {
    if (!has_arm_address_space() || !size) return;
    core_->arm_pages->VisitMapped(
        address, size,
        [&](uint64_t page, ArmPageTable::Entry& entry) {
            const auto previous = entry.permissions;
            auto* backing = entry.page;
            if (has_property(previous, Property::Exec)) invalidate_arm_code_cache_page(page);
            entry = {};
            remove_host_page_alias(ArmPageTag(page), backing, previous);
            refresh_smc_armed_for_host_page(backing);
            invalidate_arm_tlb_page(page);
            return true;
        },
        true);
}

void Mmu::invalidate_arm_code_cache_range(uint64_t address, uint64_t size) {
    HostPageSet backings;
    visit_arm_mappings(address, size, [&](uint64_t, HostAddr page, Property) {
        backings.push_unique(reinterpret_cast<uintptr_t>(page));
        return true;
    });
    if (!backings.empty()) invalidate_code_cache_host_pages(backings);
}

void Mmu::invalidate_arm_code_cache_page(uint64_t address) {
    const auto* entry = has_arm_address_space() ? core_->arm_pages->Find(address) : nullptr;
    if (entry && entry->page) {
        const auto backing = reinterpret_cast<uintptr_t>(entry->page);
        invalidate_code_cache_host_pages(&backing, 1);
    }
}

void* Mmu::try_allocate_arm_block_bytes(size_t size, uint64_t pc) {
    sync_code_cache_generation();
    auto& cache = code_cache();
    if (code_cache_flush_pending()) return nullptr;
    if (cache.ShouldFlushForAllocation(size)) {
        if (cache.requested_block_bytes) {
            cache.pending_flush = true;
            cache.pending_budget_flush = true;
            LogMsgState(get_state(), LogWarning,
                        "[CodeCacheBudgetFlush] mmu=0x%zx budget=%zu bytes_before=%zu alloc=%zu valid_blocks=%zu "
                        "all_blocks=%zu flush_count=%llu guest_pc=0x%llx pending=1",
                        static_cast<size_t>(core_->identity), cache.block_budget_bytes, cache.requested_block_bytes,
                        size, cache.CountValidBlocks(), cache.AllocatedBlockCount(),
                        static_cast<unsigned long long>(cache.flush_count + 1), static_cast<unsigned long long>(pc));
        }
        return nullptr;
    }

    void* block = cache.block_pool.allocate(size, alignof(aarch64::detail::BasicBlock));
    cache.NoteAllocatedBlockBytes(size);
#ifdef ENABLE_TLB_STATS
    ++dispatch_stats.block_allocations;
    dispatch_stats.block_allocated_bytes += size;
#endif
    return block;
}

}  // namespace fiberish::mem
#endif
