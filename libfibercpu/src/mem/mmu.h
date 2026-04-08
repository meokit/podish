#pragma once
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <atomic>
#include <cstring>
#include <iostream>
#include <memory>
#include <memory_resource>
#include <optional>
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

inline void InitializeInvalidBasicBlock(BasicBlock& block) {
    block.set_start_eip(BasicBlock::kInvalidStartEipBit);
    block.end_eip = 0;
    block.set_inst_count(0);
    block.slot_count = 0;
    block.sentinel_slot_index = 0;
    block.branch_target_eip = 0;
    block.fallthrough_eip = 0;
    block.set_terminal_kind(BlockTerminalKind::None);
    block.exec_count = 0;
    block.entry = nullptr;
}

struct BlockCacheKey {
    uintptr_t host_page_base = 0;
    uint32_t guest_eip = 0;

    bool operator==(const BlockCacheKey& other) const = default;
};

struct BlockCacheKeyHash {
    size_t operator()(const BlockCacheKey& key) const noexcept {
        size_t hash = static_cast<size_t>(key.host_page_base);
        hash ^= static_cast<size_t>(key.guest_eip) + 0x9e3779b97f4a7c15ULL + (hash << 6) + (hash >> 2);
        return hash;
    }
};

struct CodeCacheState {
    std::pmr::monotonic_buffer_resource block_pool;
    ankerl::unordered_dense::map<BlockCacheKey, BasicBlock*, BlockCacheKeyHash> block_cache;
    std::vector<BasicBlock*> all_blocks;
    BasicBlock dummy_invalid_block;
    ankerl::unordered_dense::map<uintptr_t, std::vector<BlockCacheKey>> page_to_blocks;
    ankerl::unordered_dense::map<BlockCacheKey, std::vector<uintptr_t>, BlockCacheKeyHash> block_to_host_pages;

    CodeCacheState() { InitializeInvalidBasicBlock(dummy_invalid_block); }

    void ResetReachability() {
        block_cache.clear();
        page_to_blocks.clear();
        block_to_host_pages.clear();
    }

    void RememberAllocatedBlock(BasicBlock* block) {
        if (block != nullptr) all_blocks.push_back(block);
    }

    BasicBlock* LookupBlock(const BlockCacheKey& key) {
        auto it = block_cache.find(key);
        return it == block_cache.end() ? nullptr : it->second;
    }

    const BasicBlock* LookupBlock(const BlockCacheKey& key) const {
        auto it = block_cache.find(key);
        return it == block_cache.end() ? nullptr : it->second;
    }

    void RegisterBlockHostPages(const BlockCacheKey& cache_key, const std::vector<uintptr_t>& host_page_bases) {
        block_to_host_pages[cache_key] = host_page_bases;
        for (uintptr_t host_page_base : host_page_bases) {
            page_to_blocks[host_page_base].push_back(cache_key);
        }
    }

    void RemoveBlockHostPages(const BlockCacheKey& cache_key) {
        auto host_pages_it = block_to_host_pages.find(cache_key);
        if (host_pages_it == block_to_host_pages.end()) return;

        for (uintptr_t host_page_base : host_pages_it->second) {
            auto page_it = page_to_blocks.find(host_page_base);
            if (page_it == page_to_blocks.end()) continue;

            auto& cache_keys = page_it->second;
            cache_keys.erase(std::remove(cache_keys.begin(), cache_keys.end(), cache_key), cache_keys.end());
            if (cache_keys.empty()) {
                page_to_blocks.erase(page_it);
            }
        }

        block_to_host_pages.erase(host_pages_it);
    }

    BasicBlock* CacheDecodedBlock(const BlockCacheKey& cache_key, const std::vector<uintptr_t>& host_page_bases,
                                  BasicBlock* block) {
        auto [it, inserted] = block_cache.insert({cache_key, block});
        if (!inserted) {
            BasicBlock* existing = it->second;
            if (existing != nullptr && existing->is_valid() && existing != block) {
                return existing;
            }

            it->second = block;
        }

        RemoveBlockHostPages(cache_key);
        RegisterBlockHostPages(cache_key, host_page_bases);
        return block;
    }

    void EraseBlock(const BlockCacheKey& cache_key) {
        RemoveBlockHostPages(cache_key);
        block_cache.erase(cache_key);
    }

    void InvalidateHostPage(uintptr_t host_page_base) {
        auto it = page_to_blocks.find(host_page_base);
        if (it == page_to_blocks.end()) return;

        const std::vector<BlockCacheKey> cache_keys = it->second;
        for (const BlockCacheKey& cache_key : cache_keys) {
            auto block_it = block_cache.find(cache_key);
            if (block_it != block_cache.end()) {
                block_it->second->Invalidate();
            }
            RemoveBlockHostPages(cache_key);
        }
    }

    void InvalidateHostPages(const std::vector<uintptr_t>& host_page_bases) {
        for (uintptr_t host_page_base : host_page_bases) {
            InvalidateHostPage(host_page_base);
        }
    }

    size_t CountValidBlocks() const {
        size_t count = 0;
        for (const auto& [key, block] : block_cache) {
            (void)key;
            if (block && block->is_valid()) {
                count++;
            }
        }
        return count;
    }
};

struct MmuCore {
    std::atomic<uint32_t> ref_count{1};
    std::unique_ptr<PageDirectory> page_directory;
    uintptr_t identity = 0;
    uint32_t state_flags = 0;
    CodeCacheState code_cache;
    struct ExternalAliasState {
        uint32_t exec_count = 0;
        uint32_t write_count = 0;
        std::vector<uint32_t> guest_pages;
    };
    ankerl::unordered_dense::map<uintptr_t, ExternalAliasState> external_aliases;
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

public:
    // Invariant: any executable mapping must also force writes down the slow path.
    // External writable aliases inherit ForceWriteSlow when they share a host page
    // with any executable alias in the same MMU.
    static Property normalize_mapping_perms(Property perms) {
        return has_property(perms, Property::Exec) ? (perms | Property::ForceWriteSlow) : perms;
    }

    static bool should_track_external_alias(Property perms, HostAddr page) {
        return page != nullptr && has_property(perms, Property::External);
    }

    void remove_external_alias(GuestAddr guest_page, HostAddr page, Property perms) {
        if (!core_ || !should_track_external_alias(perms, page)) return;

        auto it = core_->external_aliases.find(reinterpret_cast<uintptr_t>(page));
        if (it == core_->external_aliases.end()) return;

        auto& guest_pages = it->second.guest_pages;
        guest_pages.erase(std::remove(guest_pages.begin(), guest_pages.end(), guest_page), guest_pages.end());
        if (has_property(perms, Property::Exec) && it->second.exec_count > 0) it->second.exec_count--;
        if (has_property(perms, Property::Write) && it->second.write_count > 0) it->second.write_count--;
        if (it->second.exec_count == 0 && it->second.write_count == 0 && guest_pages.empty())
            core_->external_aliases.erase(it);
    }

    void add_external_alias(GuestAddr guest_page, HostAddr page, Property perms) {
        if (!core_ || !should_track_external_alias(perms, page)) return;

        auto& state = core_->external_aliases[reinterpret_cast<uintptr_t>(page)];
        if (std::find(state.guest_pages.begin(), state.guest_pages.end(), guest_page) == state.guest_pages.end())
            state.guest_pages.push_back(guest_page);
        if (has_property(perms, Property::Exec)) state.exec_count++;
        if (has_property(perms, Property::Write)) state.write_count++;
    }

    void refresh_force_write_slow_for_host_page(HostAddr page) {
        if (!core_ || page == nullptr) return;

        auto it = core_->external_aliases.find(reinterpret_cast<uintptr_t>(page));
        if (it == core_->external_aliases.end()) return;

        const bool any_exec_alias = it->second.exec_count != 0;
        // Keep the page-table bit as the single source of truth for write-fast-path
        // eligibility so read-path refill never needs to consult alias metadata.
        for (uint32_t guest_page : it->second.guest_pages) {
            auto* dir = current_page_directory();
            if (!dir) return;

            const uint32_t l1_idx = guest_page >> 22;
            const uint32_t l2_idx = (guest_page >> 12) & 0x3FF;
            auto& chunk = dir->l1_directory[l1_idx];
            if (!chunk || chunk->pages[l2_idx] != page) continue;

            auto perms = chunk->permissions[l2_idx] & ~Property::ForceWriteSlow;
            if (has_property(perms, Property::Exec) || (any_exec_alias && has_property(perms, Property::Write)))
                perms = perms | Property::ForceWriteSlow;

            chunk->permissions[l2_idx] = perms;
            tlb.flush_page(guest_page);
        }
    }

    void remap_external_alias(GuestAddr guest_page, HostAddr old_page, Property old_perms, HostAddr new_page,
                              Property new_perms) {
        remove_external_alias(guest_page, old_page, old_perms);
        add_external_alias(guest_page, new_page, new_perms);
        refresh_force_write_slow_for_host_page(old_page);
        refresh_force_write_slow_for_host_page(new_page);
    }

    [[nodiscard]] bool has_external_exec_alias(HostAddr page) const {
        if (!core_ || page == nullptr) return false;

        auto it = core_->external_aliases.find(reinterpret_cast<uintptr_t>(page));
        return it != core_->external_aliases.end() && it->second.exec_count != 0;
    }

    [[nodiscard]] bool should_trap_external_alias_write(Property perms, HostAddr page) const {
        return should_track_external_alias(perms, page) && has_property(perms, Property::Write) &&
               has_property(perms, Property::ForceWriteSlow);
    }

private:
    EmuState* get_state();

    // Updated signal_fault to take context
    [[nodiscard]] EmuStatus signal_fault(uint32_t addr, int is_write);
    void sync_dirty(GuestAddr vaddr);

    // Slow Path: Resolve address, handle allocation/permissions/faults
    [[nodiscard]] MemResult<HostAddr> resolve_slow(GuestAddr addr, Property req_perm);

    [[nodiscard]] PageDirectory* current_page_directory() {
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        return page_dir;
    }

    [[nodiscard]] const PageDirectory* current_page_directory() const {
        return core_ ? core_->page_directory.get() : nullptr;
    }

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
    [[nodiscard]] HostAddr resolve_safe_for_read(GuestAddr addr) {
        auto* dir = current_page_directory();
        if (!dir) return nullptr;

        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        const uint32_t offset = addr & 0xFFF;

        // 1. Check L1
        auto& chunk = dir->l1_directory[l1_idx];
        if (!chunk) return nullptr;

        // 2. Check Permissions
        Property current_perm = chunk->permissions[l2_idx];
        if (!has_property(current_perm, Property::Read)) return nullptr;

        // 3. Return existing page only (no lazy allocation for safe resolution)
        if (!chunk->pages[l2_idx]) {
            return nullptr;  // Page not populated, caller should trigger fault handling
        }

        HostAddr page_base = chunk->pages[l2_idx];
        return page_base + offset;
    }

    [[nodiscard]] HostAddr resolve_safe_for_write(GuestAddr addr) {
        auto* dir = current_page_directory();
        if (!dir) return nullptr;

        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        const uint32_t offset = addr & 0xFFF;

        auto& chunk = dir->l1_directory[l1_idx];
        if (!chunk) return nullptr;

        Property current_perm = chunk->permissions[l2_idx];
        if (!has_property(current_perm, Property::Write)) return nullptr;

        if (!has_property(current_perm, Property::Dirty)) {
            current_perm = current_perm | Property::Dirty;
            chunk->permissions[l2_idx] = current_perm;
        }

        auto* page_base = chunk->pages[l2_idx];
        if (!page_base) return nullptr;

        if (has_property(current_perm, Property::ForceWriteSlow)) {
            invalidate_code_cache_page(addr);
        }

        return page_base + offset;
    }

public:
    Mmu() {
        core_ = create_core(std::make_unique<PageDirectory>());
        page_dir = core_->page_directory.get();
    }

    Mmu(const Mmu& other) {
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        retain_core(core_);
    }

    Mmu(Mmu&& other) noexcept {
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        other.core_ = nullptr;
        other.page_dir = nullptr;
    }

    Mmu& operator=(const Mmu& other) {
        if (this == &other) return *this;
        retain_core(other.core_);
        auto* old_core = core_;
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        release_core(old_core);
        tlb.flush();
        return *this;
    }

    Mmu& operator=(Mmu&& other) noexcept {
        if (this == &other) return *this;
        release_core(core_);
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
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

    static MmuCore* CloneCorePreserveExternal(MmuCore* core) {
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

                if (!dst_chunk) {
                    dst_chunk = std::make_unique<PageTableChunk>();
                }

                dst_chunk->permissions[l2] = perms;
                if (!src_chunk->pages[l2]) continue;

                if (has_property(perms, Property::External)) {
                    dst_chunk->pages[l2] = src_chunk->pages[l2];
                    continue;
                }

                dst_chunk->pages[l2] = new std::byte[PAGE_SIZE];
                std::memcpy(dst_chunk->pages[l2], src_chunk->pages[l2], PAGE_SIZE);
            }

            if (dst_chunk) cloned->l1_directory[l1] = std::move(dst_chunk);
        }

        return create_core(std::move(cloned));
    }

    [[nodiscard]] MmuCore* core_handle() const { return core_; }

    [[nodiscard]] CodeCacheState& code_cache() { return core_->code_cache; }

    [[nodiscard]] const CodeCacheState& code_cache() const { return core_->code_cache; }

    [[nodiscard]] PageDirectory* page_directory() { return current_page_directory(); }

    [[nodiscard]] const PageDirectory* page_directory() const { return current_page_directory(); }

    [[nodiscard]] std::optional<uintptr_t> try_get_host_page_base(GuestAddr addr) const {
        auto* dir = current_page_directory();
        if (!dir) return std::nullopt;

        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        const auto& chunk = dir->l1_directory[l1_idx];
        if (!chunk) return std::nullopt;

        auto* page_ptr = chunk->pages[l2_idx];
        if (!page_ptr) return std::nullopt;
        return reinterpret_cast<uintptr_t>(page_ptr);
    }

    [[nodiscard]] std::optional<BlockCacheKey> make_block_cache_key(uint32_t guest_eip) const {
        auto host_page_base = try_get_host_page_base(guest_eip);
        if (!host_page_base) return std::nullopt;
        return BlockCacheKey{*host_page_base, guest_eip};
    }

    [[nodiscard]] std::vector<uintptr_t> collect_host_page_bases_for_guest_range(uint32_t addr, uint32_t size) const {
        std::vector<uintptr_t> host_page_bases;
        if (size == 0) return host_page_bases;

        const uint64_t start_page = static_cast<uint64_t>(addr) & ~static_cast<uint64_t>(PAGE_MASK);
        const uint64_t last_addr = static_cast<uint64_t>(addr) + static_cast<uint64_t>(size) - 1;
        const uint64_t end_page = last_addr & ~static_cast<uint64_t>(PAGE_MASK);
        for (uint64_t page = start_page;; page += PAGE_SIZE) {
            if (auto host_page_base = try_get_host_page_base(static_cast<uint32_t>(page))) {
                host_page_bases.push_back(*host_page_base);
            }
            if (page == end_page) break;
        }

        std::sort(host_page_bases.begin(), host_page_bases.end());
        host_page_bases.erase(std::unique(host_page_bases.begin(), host_page_bases.end()), host_page_bases.end());
        return host_page_bases;
    }

    [[nodiscard]] BasicBlock* invalid_code_block() { return &code_cache().dummy_invalid_block; }

    [[nodiscard]] const BasicBlock* invalid_code_block() const { return &code_cache().dummy_invalid_block; }

    [[nodiscard]] void* allocate_block_bytes(size_t size) { return code_cache().block_pool.allocate(size); }

    void remember_allocated_block(BasicBlock* block) { code_cache().RememberAllocatedBlock(block); }

    [[nodiscard]] BasicBlock* lookup_cached_block(uint32_t eip) {
        auto cache_key = make_block_cache_key(eip);
        return cache_key ? code_cache().LookupBlock(*cache_key) : nullptr;
    }

    [[nodiscard]] const BasicBlock* lookup_cached_block(uint32_t eip) const {
        auto cache_key = make_block_cache_key(eip);
        return cache_key ? code_cache().LookupBlock(*cache_key) : nullptr;
    }

    [[nodiscard]] BasicBlock* cache_decoded_block(uint32_t cache_eip, BasicBlock* block) {
        auto cache_key = make_block_cache_key(cache_eip);
        if (!cache_key) return block;

        const uint32_t last_guest_addr =
            block && block->end_eip > cache_eip ? static_cast<uint32_t>(block->end_eip - 1) : cache_eip;
        auto host_page_bases = collect_host_page_bases_for_guest_range(cache_eip, last_guest_addr - cache_eip + 1);
        return code_cache().CacheDecodedBlock(*cache_key, host_page_bases, block);
    }

    void reset_code_cache() { code_cache().ResetReachability(); }

    void erase_cached_block(uint32_t guest_eip) {
        auto cache_key = make_block_cache_key(guest_eip);
        if (cache_key) {
            code_cache().EraseBlock(*cache_key);
        }
    }

    void invalidate_code_cache_page(uint32_t guest_addr) {
        auto host_page_base = try_get_host_page_base(guest_addr);
        if (host_page_base) {
            code_cache().InvalidateHostPage(*host_page_base);
        }
    }

    void invalidate_code_cache_range(uint32_t addr, uint32_t size) {
        code_cache().InvalidateHostPages(collect_host_page_bases_for_guest_range(addr, size));
    }

    void invalidate_code_cache_host_pages(const std::vector<uintptr_t>& host_page_bases) {
        code_cache().InvalidateHostPages(host_page_bases);
    }

    void invalidate_code_cache_host_pages(const uintptr_t* host_page_bases, size_t count) {
        if (!host_page_bases || count == 0) return;
        for (size_t i = 0; i < count; ++i) {
            code_cache().InvalidateHostPage(host_page_bases[i]);
        }
    }

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
        auto* dir = current_page_directory();
        if (!dir) return Property::None;
        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        auto& chunk = dir->l1_directory[l1_idx];
        if (!chunk) return Property::None;
        return chunk->permissions[l2_idx];
    }

    // API: mmap
    void mmap(GuestAddr addr, uint32_t size, uint8_t perms_raw) {
        auto* dir = current_page_directory();
        Property perms = normalize_mapping_perms(static_cast<Property>(perms_raw));
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end_addr = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end_addr; curr += PAGE_SIZE) {
            uint32_t l1_idx = curr >> 22;
            uint32_t l2_idx = (curr >> 12) & 0x3FF;

            if (!dir->l1_directory[l1_idx]) {
                dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
            }

            auto& chunk = dir->l1_directory[l1_idx];
            auto* page = chunk->pages[l2_idx];
            auto& entry_perms = chunk->permissions[l2_idx];
            const auto old_perms = entry_perms;
            // Preserve state bits across permission changes.
            // mprotect/mmap should not clear dirty tracking, and must keep external ownership marker.
            Property preserved = Property::None;
            if (has_property(entry_perms, Property::External)) preserved = preserved | Property::External;
            if (has_property(entry_perms, Property::Dirty)) preserved = preserved | Property::Dirty;
            entry_perms = normalize_mapping_perms(perms | preserved);
            if (page && has_property(entry_perms, Property::External)) {
                remap_external_alias(curr, page, old_perms, page, entry_perms);
            }
        }
        tlb.flush();
    }

    void reprotect_mapped_range(GuestAddr addr, uint32_t size, uint8_t perms_raw) {
        auto* dir = current_page_directory();
        if (!dir || size == 0) return;

        Property perms = normalize_mapping_perms(static_cast<Property>(perms_raw));
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end_addr = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end_addr; curr += PAGE_SIZE) {
            uint32_t l1_idx = curr >> 22;
            uint32_t l2_idx = (curr >> 12) & 0x3FF;

            auto& chunk = dir->l1_directory[l1_idx];
            if (!chunk) continue;

            auto* page = chunk->pages[l2_idx];
            auto& entry_perms = chunk->permissions[l2_idx];
            if (entry_perms == Property::None) continue;

            const Property old_perms = entry_perms;

            Property preserved = Property::None;
            if (has_property(entry_perms, Property::External)) preserved = preserved | Property::External;
            if (has_property(entry_perms, Property::Dirty)) preserved = preserved | Property::Dirty;
            entry_perms = normalize_mapping_perms(perms | preserved);
            remap_external_alias(curr, page, old_perms, page, entry_perms);
        }

        tlb.flush();
    }

    // API: allocate_page - Allocate a single page and return host pointer
    // Sets permissions and allocates memory in one call
    // Returns the host address of the allocated page, or nullptr on failure
    [[nodiscard]] HostAddr allocate_page(GuestAddr addr, uint8_t perms_raw) {
        auto* dir = current_page_directory();
        Property perms = normalize_mapping_perms(static_cast<Property>(perms_raw));
        uint32_t page_addr = addr & ~PAGE_MASK;
        uint32_t l1_idx = page_addr >> 22;
        uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        if (!dir->l1_directory[l1_idx]) {
            dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
        }

        auto& chunk = dir->l1_directory[l1_idx];

        // Allocate page if not already allocated
        if (!chunk->pages[l2_idx]) {
            chunk->pages[l2_idx] = new std::byte[PAGE_SIZE];
            std::memset(chunk->pages[l2_idx], 0, PAGE_SIZE);
        }

        chunk->permissions[l2_idx] = normalize_mapping_perms(perms);
        tlb.flush_page(page_addr);

        return chunk->pages[l2_idx];
    }

    // API: map_external_page - Map an external memory page to guest address
    // The external memory is NOT owned by the MMU (will not be freed on munmap)
    // For mmap passthrough, shared memory, etc.
    // Returns true on success
    bool map_external_page(GuestAddr addr, HostAddr external_page, uint8_t perms_raw) {
        auto* dir = current_page_directory();
        Property perms = normalize_mapping_perms(static_cast<Property>(perms_raw));
        uint32_t page_addr = addr & ~PAGE_MASK;
        uint32_t l1_idx = page_addr >> 22;
        uint32_t l2_idx = (page_addr >> 12) & 0x3FF;

        if (!dir->l1_directory[l1_idx]) {
            dir->l1_directory[l1_idx] = std::make_unique<PageTableChunk>();
        }

        auto& chunk = dir->l1_directory[l1_idx];

        auto* old_page = chunk->pages[l2_idx];
        const Property old_perms = chunk->permissions[l2_idx];

        // Free existing page if owned
        if (chunk->pages[l2_idx] && !has_property(chunk->permissions[l2_idx], Property::External)) {
            delete[] chunk->pages[l2_idx];
        }

        // Set external page (caller owns this memory)
        chunk->pages[l2_idx] = external_page;
        chunk->permissions[l2_idx] = normalize_mapping_perms(perms | Property::External);  // Mark as external
        remap_external_alias(page_addr, old_page, old_perms, external_page, chunk->permissions[l2_idx]);
        tlb.flush_page(page_addr);

        return true;
    }

    // API: munmap - Clear pages and permissions
    void munmap(GuestAddr addr, uint32_t size) {
        auto* dir = current_page_directory();
        uint32_t start = addr & ~PAGE_MASK;
        uint32_t end_addr = (addr + size + PAGE_MASK) & ~PAGE_MASK;

        for (uint32_t curr = start; curr < end_addr; curr += PAGE_SIZE) {
            uint32_t l1_idx = curr >> 22;
            uint32_t l2_idx = (curr >> 12) & 0x3FF;

            auto& chunk = dir->l1_directory[l1_idx];
            if (chunk) {
                auto* old_page = chunk->pages[l2_idx];
                const Property old_perms = chunk->permissions[l2_idx];
                // Delete the page data only if not external
                if (chunk->pages[l2_idx] && !has_property(chunk->permissions[l2_idx], Property::External)) {
                    delete[] chunk->pages[l2_idx];
                }
                chunk->pages[l2_idx] = nullptr;
                // Clear permissions
                chunk->permissions[l2_idx] = Property::None;
                remove_external_alias(curr, old_page, old_perms);
                refresh_force_write_slow_for_host_page(old_page);
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
        core_->code_cache.ResetReachability();
        core_->external_aliases.clear();
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
    [[nodiscard]] FORCE_INLINE MemResult<T> read(GuestAddr addr, MicroTLB* utlb, const DecodedOp* cur_op);

    template <typename T, bool fail_on_tlb_miss = false>
    [[nodiscard]] FORCE_INLINE MemResult<void> write(GuestAddr addr, T val, MicroTLB* utlb, const DecodedOp* cur_op);

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
        auto* dir = current_page_directory();
        if (!dir) return false;
        const uint32_t l1_idx = addr >> 22;
        const uint32_t l2_idx = (addr >> 12) & 0x3FF;
        auto& chunk = dir->l1_directory[l1_idx];
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
