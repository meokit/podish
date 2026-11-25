#pragma once
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <atomic>
#include <bitset>
#include <cstring>
#include <iostream>
#include <memory>
#include <memory_resource>
#include <new>
#include <optional>
#include <span>
#include <vector>
#include "../common.h"
#include "../decoder.h"
#include "block_page_hash_index.h"
#include "tlb.h"
#include "types.h"

namespace fiberish {
namespace mem {
struct CodeCacheState;
}
struct LLVMAsyncState;
struct CpuRunOptions;
#if FIBERCPU_LLVM_JIT
void CancelPendingLLVMBlockCompilations(LLVMAsyncState& state);
bool LLVMJitCacheFlushPending();
uint64_t LLVMJitCacheEpoch();
void LLVMJitMmuCreated();
void LLVMJitMmuDestroyed();
void RetireLLVMWeightedProfile(const mem::CodeCacheState& cache);
void RetireLLVMJitTrace(const mem::CodeCacheState& cache);
#endif
}  // namespace fiberish

namespace fiberish::mem {

// Each L2 chunk describes up to 4 MiB of caller-owned backing.
struct PageTableChunk {
    std::array<HostAddr, 1024> pages{};
    std::array<Property, 1024> permissions{};
};

// Callback Signatures
// We use the same signatures as the original SoftMMU to allow easy integration
using FaultHandler = int (*)(void* opaque, uint32_t addr, int is_write);
using MemHook = void (*)(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val);

// Shared Memory State (Page Tables)
struct PageDirectory {
    std::array<std::unique_ptr<PageTableChunk>, 1024> l1_directory;
};

inline void InitializeInvalidBasicBlock(BasicBlock& block) {
    block.set_start_eip(0);
    block.set_valid(false);
    block.end_eip = 0;
    block.set_inst_count(0);
    block.slot_count = 0;
    block.sentinel_slot_index = 0;
    block.link_only_return_prefixes = 0;
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
    using is_avalanching = void;

    size_t operator()(const BlockCacheKey& key) const noexcept {
        uint64_t hash = static_cast<uint64_t>(key.host_page_base);
        hash ^= static_cast<uint64_t>(key.guest_eip) + 0x9e3779b97f4a7c15ULL;
        hash ^= hash >> 30;
        hash *= 0xbf58476d1ce4e5b9ULL;
        hash ^= hash >> 27;
        hash *= 0x94d049bb133111ebULL;
        hash ^= hash >> 31;
        return static_cast<size_t>(hash);
    }
};

struct HostPageSet {
    static constexpr uint32_t InlineCapacity = 4;

    union Storage {
        std::array<uintptr_t, InlineCapacity> inline_pages;
        uintptr_t* spill;

        Storage() : inline_pages{} {}
    } storage;

    ankerl::unordered_dense::set<uintptr_t> spill_lookup;
    uint32_t count = 0;
    uint32_t capacity = InlineCapacity;

    HostPageSet() = default;

    HostPageSet(const HostPageSet& other) { copy_from(other); }

    HostPageSet& operator=(const HostPageSet& other) {
        if (this != &other) {
            reset();
            copy_from(other);
        }
        return *this;
    }

    HostPageSet(HostPageSet&& other) noexcept { move_from(std::move(other)); }

    HostPageSet& operator=(HostPageSet&& other) noexcept {
        if (this != &other) {
            reset();
            move_from(std::move(other));
        }
        return *this;
    }

    ~HostPageSet() { reset(); }

    [[nodiscard]] bool empty() const { return count == 0; }

    [[nodiscard]] uint32_t size() const { return count; }

    [[nodiscard]] bool contains(uintptr_t page) const {
        if (!is_inline()) return spill_lookup.contains(page);

        const uintptr_t* pages = data();
        for (uint32_t i = 0; i < count; ++i) {
            if (pages[i] == page) return true;
        }
        return false;
    }

    bool push_unique(uintptr_t page) {
        if (page == 0) return false;
        if (!is_inline()) {
            auto [_, inserted] = spill_lookup.emplace(page);
            if (!inserted) return false;
            push_back(page);
            return true;
        }

        if (contains(page)) return false;
        if (count == capacity) {
            grow();
            spill_lookup.emplace(page);
        }
        push_back(page);
        return true;
    }

    [[nodiscard]] uintptr_t operator[](uint32_t index) const { return data()[index]; }

    [[nodiscard]] const uintptr_t* begin() const { return data(); }
    [[nodiscard]] const uintptr_t* end() const { return data() + count; }

private:
    [[nodiscard]] bool is_inline() const { return capacity == InlineCapacity; }

    [[nodiscard]] uintptr_t* data() { return is_inline() ? storage.inline_pages.data() : storage.spill; }

    [[nodiscard]] const uintptr_t* data() const { return is_inline() ? storage.inline_pages.data() : storage.spill; }

    void push_back(uintptr_t page) {
        if (count == capacity) grow();
        data()[count++] = page;
    }

    void grow() {
        const bool was_inline = is_inline();
        const uint32_t new_capacity = capacity * 2;
        const uintptr_t* old_pages = data();
        auto* new_pages = new uintptr_t[new_capacity];
        std::copy_n(old_pages, count, new_pages);
        if (!was_inline) {
            delete[] storage.spill;
        }
        storage.spill = new_pages;
        capacity = new_capacity;
        if (!was_inline) return;

        spill_lookup.clear();
        spill_lookup.reserve(new_capacity);
        for (uint32_t i = 0; i < count; ++i) {
            spill_lookup.emplace(new_pages[i]);
        }
    }

    void reset() {
        if (!is_inline()) delete[] storage.spill;
        storage.inline_pages = {};
        spill_lookup = {};
        count = 0;
        capacity = InlineCapacity;
    }

    void copy_from(const HostPageSet& other) {
        count = other.count;
        capacity = other.capacity;
        if (other.is_inline()) {
            storage.inline_pages = other.storage.inline_pages;
            spill_lookup = {};
            return;
        }

        spill_lookup = other.spill_lookup;
        storage.spill = new uintptr_t[capacity];
        std::copy_n(other.storage.spill, count, storage.spill);
    }

    void move_from(HostPageSet&& other) {
        count = other.count;
        capacity = other.capacity;
        spill_lookup = std::move(other.spill_lookup);
        if (other.is_inline()) {
            storage.inline_pages = other.storage.inline_pages;
        } else {
            storage.spill = other.storage.spill;
        }

        other.storage.inline_pages = {};
        other.spill_lookup = {};
        other.count = 0;
        other.capacity = InlineCapacity;
    }
};

struct CodeCacheState {
    static constexpr size_t kDefaultBlockBudgetBytes = 64ull * 1024ull * 1024ull;

    struct BlockHostPageRef {
        uintptr_t host_page_base = 0;
        uint32_t page_slot = 0;
    };

    struct PageBlockRef {
        uint32_t block_index = 0;
        uint32_t host_page_index = 0;
    };

    struct BlockCacheEntry {
        BlockCacheKey cache_key{};
        BasicBlock* block = nullptr;
        std::vector<BlockHostPageRef> host_pages;
    };

    struct PageRegionEntry {
        uint32_t eip;
        BasicBlock* block;
        uint16_t revision;
        HandlerFunc handler;
        HandlerFunc fallback = nullptr;
    };

    struct PageRegionFallback {
        BasicBlock* block;
        HandlerFunc handler;
    };

    struct PageRegions {
        ankerl::unordered_dense::map<uint32_t, std::vector<PageRegionFallback>> live;
        uint64_t invalidation_epoch = 0;
    };

    std::pmr::monotonic_buffer_resource block_pool;
    BlockPageHashIndex<BlockCacheKey> block_cache;
    std::vector<BlockCacheEntry> block_entries;
    std::vector<BasicBlock*> all_blocks;
    BasicBlock dummy_invalid_block;
    ankerl::unordered_dense::map<uintptr_t, std::vector<PageBlockRef>> page_to_blocks;
    ankerl::unordered_dense::map<BasicBlock*, uint16_t> pending_page_profile;
    std::unique_ptr<PageRegions> page_regions;
    size_t requested_block_bytes = 0;
    size_t block_budget_bytes = kDefaultBlockBudgetBytes;
    uint64_t flush_count = 0;
    uint64_t generation = 0;
    uint64_t reset_request_epoch = 0;
    uint64_t llvm_cache_epoch = 0;
    bool pending_flush = false;
    bool pending_budget_flush = false;
    bool has_weighted_profile = false;
    bool has_jit_trace = false;
    std::shared_ptr<const CpuRunOptions> run_options;

    CodeCacheState() { InitializeInvalidBasicBlock(dummy_invalid_block); }
    ~CodeCacheState() {
#if FIBERCPU_LLVM_JIT
        if (has_weighted_profile) RetireLLVMWeightedProfile(*this);
        if (has_jit_trace) RetireLLVMJitTrace(*this);
#endif
    }

    void InvalidateBlock(BasicBlock& block) {
        if (page_regions) {
            auto& live = page_regions->live;
            const auto region = live.find(block.start_eip() & ~PAGE_MASK);
            if (region != live.end()) {
                // A block may be revalidated individually. Restore every
                // independently executable entry before any member can do that.
                for (const auto& member : region->second) {
                    member.block->entry = member.handler;
                    if (member.block->chain.has_region_context) member.block->SetRegionContext(nullptr);
                }
                live.erase(region);
            }
        }
        block.Invalidate();
    }

    void InvalidateAll() {
        if (page_regions) ++page_regions->invalidation_epoch;
        for (auto& entry : block_entries) {
            if (entry.block != nullptr) {
                InvalidateBlock(*entry.block);
            }
        }
        block_cache.clear();
        block_entries.clear();
        page_to_blocks.clear();
    }

    void ResetReachability() { InvalidateAll(); }

    void HardResetReleaseAll(uint64_t generation_increment = 1, uint64_t flush_count_increment = 0) {
        const size_t preserved_budget_bytes = block_budget_bytes;
        const uint64_t next_flush_count = flush_count + flush_count_increment;
        const uint64_t next_generation = generation + generation_increment;
        const uint64_t next_reset_request_epoch = reset_request_epoch;
        const uint64_t previous_llvm_epoch = llvm_cache_epoch;
        auto preserved_run_options = run_options;

        this->~CodeCacheState();
        new (this) CodeCacheState();
        block_budget_bytes = preserved_budget_bytes;
        flush_count = next_flush_count;
        generation = next_generation;
        reset_request_epoch = next_reset_request_epoch;
        llvm_cache_epoch = previous_llvm_epoch;
        run_options = std::move(preserved_run_options);
    }

    void SetBlockBudgetBytes(size_t bytes) { block_budget_bytes = bytes == 0 ? kDefaultBlockBudgetBytes : bytes; }

    [[nodiscard]] bool ShouldFlushForAllocation(size_t alloc_size) const {
        if (block_budget_bytes == 0) return false;
        return alloc_size > std::numeric_limits<size_t>::max() - requested_block_bytes ||
               requested_block_bytes + alloc_size > block_budget_bytes;
    }

    void NoteAllocatedBlockBytes(size_t alloc_size) {
        if (alloc_size > std::numeric_limits<size_t>::max() - requested_block_bytes) {
            requested_block_bytes = std::numeric_limits<size_t>::max();
            return;
        }

        requested_block_bytes += alloc_size;
    }

    void RememberAllocatedBlock(BasicBlock* block) {
        if (block != nullptr) all_blocks.push_back(block);
    }

    BasicBlock* LookupBlock(const BlockCacheKey& key) {
        return const_cast<BasicBlock*>(std::as_const(*this).LookupBlock(key));
    }

    const BasicBlock* LookupBlock(const BlockCacheKey& key) const {
        const auto* index = block_cache.find(key);
        return index ? block_entries[*index].block : nullptr;
    }

#if FIBERCPU_BLOCK_COMPILATION
    bool InstallPageRegion(uint32_t guest_page, uintptr_t host_page, std::span<const PageRegionEntry> members) {
        if (!page_regions || members.size() < 2 || page_regions->live.contains(guest_page)) return false;

        std::vector<PageRegionFallback> fallback;
        fallback.reserve(members.size());
        std::optional<uint32_t> previous;
        for (const auto& member : members) {
            if ((member.eip & ~PAGE_MASK) != guest_page || (previous && member.eip <= *previous) || !member.block ||
                !member.handler)
                return false;
            previous = member.eip;
            const auto* index = block_cache.find(BlockCacheKey{host_page, member.eip});
            if (!index) return false;
            const auto& entry = block_entries[*index];
            if (entry.block != member.block || entry.host_pages.size() != 1 ||
                entry.host_pages.front().host_page_base != host_page)
                return false;
            const auto& block = *entry.block;
            if (!block.is_valid() || block.start_eip() != member.eip ||
                block.invalidation_revision != member.revision || !block.entry || !block.inst_count() ||
                block.end_eip <= member.eip || ((block.end_eip - 1) & ~PAGE_MASK) != guest_page)
                return false;
            fallback.push_back({member.block, member.fallback ? member.fallback : block.entry});
        }

        // The scheduler owns publication. Workers supply code and immutable
        // snapshots. No guest code or callback runs between these stores.
        const auto installed = page_regions->live.emplace(guest_page, std::move(fallback)).first;
        for (const auto& member : members) {
            if (member.block->chain.has_region_context) member.block->SetRegionContext(installed->second.data());
            member.block->entry = member.handler;
        }
        return true;
    }
#endif

    void EnsureBlockHostPagesRegistered(const BlockCacheKey& cache_key, const HostPageSet& host_page_bases,
                                        HostPageSet* affected_host_pages = nullptr) {
        const auto* index = block_cache.find(cache_key);
        if (!index) return;
        auto& entry = block_entries[*index];
        if (!entry.host_pages.empty()) return;
        RegisterBlockHostPages(*index, host_page_bases);
        if (affected_host_pages) {
            for (uintptr_t host_page_base : host_page_bases) {
                affected_host_pages->push_unique(host_page_base);
            }
        }
    }

    void RegisterBlockHostPages(uint32_t block_index, const HostPageSet& host_page_bases) {
        auto& entry = block_entries[block_index];
        entry.host_pages.resize(host_page_bases.size());
        for (uint32_t i = 0; i < host_page_bases.size(); ++i) {
            const uintptr_t host_page_base = host_page_bases[i];
            auto& page_refs = page_to_blocks[host_page_base];
            const uint32_t page_slot = static_cast<uint32_t>(page_refs.size());
            page_refs.push_back(PageBlockRef{block_index, i});
            entry.host_pages[i] = BlockHostPageRef{host_page_base, page_slot};
        }
    }

    void RemoveBlockHostPagesByIndex(uint32_t block_index, HostPageSet* affected_host_pages = nullptr) {
        auto& entry = block_entries[block_index];
        for (int32_t i = static_cast<int32_t>(entry.host_pages.size()) - 1; i >= 0; --i) {
            const auto host_page_ref = entry.host_pages[static_cast<size_t>(i)];
            if (affected_host_pages) {
                affected_host_pages->push_unique(host_page_ref.host_page_base);
            }
            auto page_it = page_to_blocks.find(host_page_ref.host_page_base);
            if (page_it == page_to_blocks.end()) continue;

            auto& page_refs = page_it->second;
            const uint32_t remove_slot = host_page_ref.page_slot;
            if (remove_slot >= page_refs.size()) continue;

            const uint32_t last_slot = static_cast<uint32_t>(page_refs.size() - 1);
            if (remove_slot != last_slot) {
                const PageBlockRef moved_ref = page_refs[last_slot];
                page_refs[remove_slot] = moved_ref;
                block_entries[moved_ref.block_index].host_pages[moved_ref.host_page_index].page_slot = remove_slot;
            }

            page_refs.pop_back();
            if (page_refs.empty()) {
                page_to_blocks.erase(page_it);
            }
        }

        entry.host_pages.clear();
    }

    void RemoveBlockHostPages(const BlockCacheKey& cache_key, HostPageSet* affected_host_pages = nullptr) {
        if (const auto* index = block_cache.find(cache_key)) {
            RemoveBlockHostPagesByIndex(*index, affected_host_pages);
        }
    }

    void FixupMovedBlockIndex(uint32_t block_index) {
        auto& entry = block_entries[block_index];
        for (uint32_t i = 0; i < entry.host_pages.size(); ++i) {
            const auto host_page_ref = entry.host_pages[i];
            auto page_it = page_to_blocks.find(host_page_ref.host_page_base);
            if (page_it == page_to_blocks.end()) continue;

            auto& page_refs = page_it->second;
            if (host_page_ref.page_slot >= page_refs.size()) continue;

            auto& page_ref = page_refs[host_page_ref.page_slot];
            page_ref.block_index = block_index;
            page_ref.host_page_index = i;
        }
    }

    BasicBlock* CacheDecodedBlock(const BlockCacheKey& cache_key, const HostPageSet& host_page_bases, BasicBlock* block,
                                  HostPageSet* affected_host_pages = nullptr) {
        auto [index, inserted] = block_cache.try_emplace(cache_key, static_cast<uint32_t>(block_entries.size()));
        if (inserted) {
            block_entries.push_back(BlockCacheEntry{cache_key, block, {}});
        } else {
            auto& entry = block_entries[*index];
            BasicBlock* existing = entry.block;
            if (existing != nullptr && existing->is_valid() && existing != block) {
                if (affected_host_pages) {
                    for (uintptr_t host_page_base : host_page_bases) {
                        affected_host_pages->push_unique(host_page_base);
                    }
                }
                return existing;
            }

            entry.block = block;
            RemoveBlockHostPagesByIndex(*index, affected_host_pages);
        }

        RegisterBlockHostPages(*index, host_page_bases);
        if (affected_host_pages) {
            for (uintptr_t host_page_base : host_page_bases) {
                affected_host_pages->push_unique(host_page_base);
            }
        }
        return block;
    }

    void EraseBlock(const BlockCacheKey& cache_key, HostPageSet* affected_host_pages = nullptr) {
        const auto* index = block_cache.find(cache_key);
        if (!index) return;

        const uint32_t block_index = *index;
        auto& entry = block_entries[block_index];
        if (entry.block != nullptr) {
            if (page_regions) ++page_regions->invalidation_epoch;
            InvalidateBlock(*entry.block);
        }

        RemoveBlockHostPagesByIndex(block_index, affected_host_pages);
        block_cache.erase(cache_key);

        const uint32_t last_index = static_cast<uint32_t>(block_entries.size() - 1);
        if (block_index != last_index) {
            const BlockCacheKey moved_key = block_entries[last_index].cache_key;
            block_entries[block_index] = std::move(block_entries[last_index]);
            if (auto* moved_index = block_cache.find(moved_key)) {
                *moved_index = block_index;
            }
            FixupMovedBlockIndex(block_index);
        }

        block_entries.pop_back();
    }

    void InvalidateHostPage(uintptr_t host_page_base, HostPageSet* affected_host_pages = nullptr) {
        if (page_regions && page_to_blocks.contains(host_page_base)) ++page_regions->invalidation_epoch;
        while (true) {
            auto it = page_to_blocks.find(host_page_base);
            if (it == page_to_blocks.end() || it->second.empty()) return;

            const PageBlockRef block_ref = it->second.back();
            auto& entry = block_entries[block_ref.block_index];
            if (entry.block != nullptr) {
                InvalidateBlock(*entry.block);
            }
            RemoveBlockHostPagesByIndex(block_ref.block_index, affected_host_pages);
        }
    }

    void InvalidateHostPages(const HostPageSet& host_page_bases, HostPageSet* affected_host_pages = nullptr) {
        for (uintptr_t host_page_base : host_page_bases) {
            InvalidateHostPage(host_page_base, affected_host_pages);
        }
    }

    size_t CountValidBlocks() const {
        size_t count = 0;
        for (const auto& entry : block_entries) {
            if (entry.block && entry.block->is_valid()) {
                count++;
            }
        }
        return count;
    }
};

struct MmuCore {
    struct GuestPageSet {
        static constexpr uint32_t InlineCapacity = 2;

        union Storage {
            std::array<uint32_t, InlineCapacity> inline_pages;
            uint32_t* spill;

            Storage() : inline_pages{} {}
        } storage;

        uint32_t size = 0;
        uint32_t capacity = InlineCapacity;

        GuestPageSet() = default;

        GuestPageSet(const GuestPageSet& other) { copy_from(other); }

        GuestPageSet& operator=(const GuestPageSet& other) {
            if (this != &other) {
                reset();
                copy_from(other);
            }
            return *this;
        }

        GuestPageSet(GuestPageSet&& other) noexcept { move_from(std::move(other)); }

        GuestPageSet& operator=(GuestPageSet&& other) noexcept {
            if (this != &other) {
                reset();
                move_from(std::move(other));
            }
            return *this;
        }

        ~GuestPageSet() { reset(); }

        [[nodiscard]] bool empty() const { return size == 0; }

        [[nodiscard]] bool contains(uint32_t page) const {
            const uint32_t* pages = data();
            for (uint32_t i = 0; i < size; ++i) {
                if (pages[i] == page) return true;
            }
            return false;
        }

        bool push_unique(uint32_t page) {
            if (contains(page)) return false;
            push_back(page);
            return true;
        }

        bool erase(uint32_t page) {
            uint32_t* pages = data();
            for (uint32_t i = 0; i < size; ++i) {
                if (pages[i] != page) continue;
                pages[i] = pages[size - 1];
                --size;
                return true;
            }
            return false;
        }

        [[nodiscard]] const uint32_t* begin() const { return data(); }
        [[nodiscard]] const uint32_t* end() const { return data() + size; }

    private:
        [[nodiscard]] bool is_inline() const { return capacity == InlineCapacity; }

        [[nodiscard]] uint32_t* data() { return is_inline() ? storage.inline_pages.data() : storage.spill; }

        [[nodiscard]] const uint32_t* data() const { return is_inline() ? storage.inline_pages.data() : storage.spill; }

        void push_back(uint32_t page) {
            if (size == capacity) grow();
            data()[size++] = page;
        }

        void grow() {
            const uint32_t new_capacity = capacity * 2;
            auto* new_pages = new uint32_t[new_capacity];
            std::copy_n(data(), size, new_pages);
            if (!is_inline()) delete[] storage.spill;
            storage.spill = new_pages;
            capacity = new_capacity;
        }

        void reset() {
            if (!is_inline()) delete[] storage.spill;
            storage.inline_pages = {};
            size = 0;
            capacity = InlineCapacity;
        }

        void copy_from(const GuestPageSet& other) {
            size = other.size;
            capacity = other.capacity;
            if (other.is_inline()) {
                storage.inline_pages = other.storage.inline_pages;
                return;
            }

            storage.spill = new uint32_t[capacity];
            std::copy_n(other.storage.spill, size, storage.spill);
        }

        void move_from(GuestPageSet&& other) {
            size = other.size;
            capacity = other.capacity;
            if (other.is_inline()) {
                storage.inline_pages = other.storage.inline_pages;
            } else {
                storage.spill = other.storage.spill;
            }

            other.storage.inline_pages = {};
            other.size = 0;
            other.capacity = InlineCapacity;
        }
    };

    std::atomic<uint32_t> ref_count{1};
    std::unique_ptr<PageDirectory> page_directory;
    uintptr_t identity = 0;
    uint32_t state_flags = 0;
    CodeCacheState code_cache;
    // Execution is serialized by the host scheduler. Callback-time cache resets
    // may invalidate reachability, but must not release this core's block pool.
    bool execution_active = false;
    struct HostPageAliases {
        uint32_t exec_count = 0;
        GuestPageSet guest_pages;
    };
    struct RuntimeTlbShootdownRing {
        static constexpr size_t kCapacity = 1024;

        std::array<uint32_t, kCapacity> guest_pages{};
        uint64_t write_seq = 0;
    };
    ankerl::unordered_dense::map<uintptr_t, HostPageAliases> host_page_aliases;
    RuntimeTlbShootdownRing runtime_tlb_shootdown_ring;
    std::shared_ptr<LLVMAsyncState> llvm_async;
};

class Mmu {
public:
    // Fast non-owning alias for access paths.
    PageDirectory* page_dir = nullptr;

private:
    MmuCore* core_ = nullptr;
    // Core that an execve replaced while a native run was active. Kept alive
    // (and marked non-executing) until that run unwinds, so translated blocks
    // still on the host stack remain readable.
    MmuCore* deferred_exec_core_ = nullptr;
    SoftTlb tlb;
    struct BlockLookupCacheEntry {
        uintptr_t host_page_base = 0;
        uint32_t guest_eip = 0;
        BasicBlock* block = nullptr;
    };
    static constexpr size_t kBlockLookupCacheSize = 128;
    static constexpr size_t kBlockLookupCacheMask = kBlockLookupCacheSize - 1;
    mutable std::array<BlockLookupCacheEntry, kBlockLookupCacheSize> block_lookup_cache{};
    mutable uint64_t seen_code_cache_generation_ = 0;
    uint64_t runtime_tlb_read_seq = 0;
    uint64_t runtime_tlb_publish_begin_seq = 0;
    uint32_t runtime_tlb_publish_batch_depth = 0;

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
    class RuntimeTlbPublishBatchScope {
    public:
        explicit RuntimeTlbPublishBatchScope(Mmu* mmu) : mmu_(mmu) {
            if (mmu_ != nullptr) mmu_->begin_runtime_tlb_publish_batch();
        }

        RuntimeTlbPublishBatchScope(const RuntimeTlbPublishBatchScope&) = delete;
        RuntimeTlbPublishBatchScope& operator=(const RuntimeTlbPublishBatchScope&) = delete;

        RuntimeTlbPublishBatchScope(RuntimeTlbPublishBatchScope&& other) noexcept : mmu_(other.mmu_) {
            other.mmu_ = nullptr;
        }

        RuntimeTlbPublishBatchScope& operator=(RuntimeTlbPublishBatchScope&& other) noexcept {
            if (this == &other) return *this;
            if (mmu_ != nullptr) mmu_->end_runtime_tlb_publish_batch();
            mmu_ = other.mmu_;
            other.mmu_ = nullptr;
            return *this;
        }

        ~RuntimeTlbPublishBatchScope() {
            if (mmu_ != nullptr) mmu_->end_runtime_tlb_publish_batch();
        }

    private:
        Mmu* mmu_ = nullptr;
    };

    void remove_host_page_alias(GuestAddr guest_page, HostAddr page, Property perms) {
        if (!core_ || !page) return;

        auto it = core_->host_page_aliases.find(reinterpret_cast<uintptr_t>(page));
        if (it == core_->host_page_aliases.end()) return;

        auto& guest_pages = it->second.guest_pages;
        guest_pages.erase(guest_page);
        if (has_property(perms, Property::Exec) && it->second.exec_count > 0) it->second.exec_count--;
        if (guest_pages.empty()) core_->host_page_aliases.erase(it);
    }

    void add_host_page_alias(GuestAddr guest_page, HostAddr page, Property perms) {
        if (!core_ || !page) return;

        auto& state = core_->host_page_aliases[reinterpret_cast<uintptr_t>(page)];
        state.guest_pages.push_unique(guest_page);
        if (has_property(perms, Property::Exec)) state.exec_count++;
    }

    [[nodiscard]] bool host_page_has_live_blocks(HostAddr page) const {
        if (!core_ || page == nullptr) return false;

        auto it = core_->code_cache.page_to_blocks.find(reinterpret_cast<uintptr_t>(page));
        return it != core_->code_cache.page_to_blocks.end() && !it->second.empty();
    }

    void refresh_smc_armed_for_host_page(HostAddr page) {
        if (!core_ || page == nullptr) return;

        auto* dir = current_page_directory();
        if (!dir) return;

        auto it = core_->host_page_aliases.find(reinterpret_cast<uintptr_t>(page));
        if (it == core_->host_page_aliases.end()) return;

        const bool arm_write_slow = it->second.exec_count != 0 && host_page_has_live_blocks(page);
        // Keep the page-table bit as the single source of truth for write-fast-path
        // eligibility so read-path refill never needs to consult alias metadata.
        for (uint32_t guest_page : it->second.guest_pages) {
            const uint32_t l1_idx = guest_page >> 22;
            const uint32_t l2_idx = (guest_page >> 12) & 0x3FF;
            auto& chunk = dir->l1_directory[l1_idx];
            if (!chunk || chunk->pages[l2_idx] != page) continue;

            const auto old_perms = chunk->permissions[l2_idx];
            auto perms = old_perms & ~Property::ForceWriteSlow;
            const bool is_write_alias = has_property(perms, Property::Write);
            if (arm_write_slow && is_write_alias) perms = perms | Property::ForceWriteSlow;

            if (old_perms == perms) continue;
            chunk->permissions[l2_idx] = perms;
            if (!is_write_alias) continue;
            tlb.flush_page(guest_page);
            publish_runtime_tlb_flush_deduped(guest_page);
        }
    }

    void refresh_smc_armed_for_host_pages(const HostPageSet& host_page_bases) {
        for (uintptr_t host_page_base : host_page_bases) {
            refresh_smc_armed_for_host_page(reinterpret_cast<HostAddr>(host_page_base));
        }
    }

    void refresh_smc_armed_for_requested_and_affected_host_pages(const HostPageSet& requested_host_pages,
                                                                 const HostPageSet& affected_host_pages) {
        for (uintptr_t host_page_base : requested_host_pages) {
            refresh_smc_armed_for_host_page(reinterpret_cast<HostAddr>(host_page_base));
        }

        for (uintptr_t host_page_base : affected_host_pages) {
            if (requested_host_pages.contains(host_page_base)) continue;
            refresh_smc_armed_for_host_page(reinterpret_cast<HostAddr>(host_page_base));
        }
    }

#if FIBERCPU_BLOCK_COMPILATION
    void invalidate_code_before_permission_change(HostAddr page, Property old_perms, Property new_perms) {
        if (!page || !code_cache().page_regions || !has_property(old_perms, Property::Exec)) return;
        constexpr auto region_permissions = Property::Exec | Property::Write;
        if ((old_perms & region_permissions) == (new_perms & region_permissions)) return;
        const auto backing = reinterpret_cast<uintptr_t>(page);
        invalidate_code_cache_host_pages(&backing, 1);
    }
#endif

    void update_host_page_alias(GuestAddr guest_page, HostAddr old_page, Property old_perms, HostAddr new_page,
                                Property new_perms) {
        remove_host_page_alias(guest_page, old_page, old_perms);
        add_host_page_alias(guest_page, new_page, new_perms);
        refresh_smc_armed_for_host_page(old_page);
        refresh_smc_armed_for_host_page(new_page);
    }

private:
    EmuState* get_state();
    [[nodiscard]] bool owning_engine_is_executing();

    // Updated signal_fault to take context
    [[nodiscard]] EmuStatus signal_fault(uint32_t addr, Property access);
    void sync_dirty(GuestAddr vaddr);

    static size_t block_lookup_cache_slot(uintptr_t host_page_base, uint32_t guest_eip) {
        uint64_t hash = static_cast<uint64_t>(host_page_base);
        hash ^= static_cast<uint64_t>(guest_eip) * 0x9e3779b97f4a7c15ULL;
        hash ^= hash >> 32;
        return static_cast<size_t>(hash) & kBlockLookupCacheMask;
    }

    void clear_block_lookup_cache() const {
        for (auto& entry : block_lookup_cache) {
            entry = {};
        }
    }

    void sync_code_cache_generation() const {
        const uint64_t current_generation = core_ ? core_->code_cache.generation : 0;
        if (seen_code_cache_generation_ == current_generation) return;
        clear_block_lookup_cache();
        seen_code_cache_generation_ = current_generation;
    }

    void on_code_cache_hard_reset();

    void invalidate_block_lookup_cache_entry(uintptr_t host_page_base, uint32_t guest_eip) const {
        auto& entry = block_lookup_cache[block_lookup_cache_slot(host_page_base, guest_eip)];
        if (entry.host_page_base == host_page_base && entry.guest_eip == guest_eip) {
            entry = {};
        }
    }

    void invalidate_block_lookup_cache_page(uintptr_t host_page_base) const {
        for (auto& entry : block_lookup_cache) {
            if (entry.host_page_base == host_page_base) {
                entry = {};
            }
        }
    }

    void remember_block_lookup_cache(uintptr_t host_page_base, uint32_t guest_eip, BasicBlock* block) const {
        if (block == nullptr) return;
        sync_code_cache_generation();
        block_lookup_cache[block_lookup_cache_slot(host_page_base, guest_eip)] =
            BlockLookupCacheEntry{host_page_base, guest_eip, block};
    }

    BasicBlock* try_lookup_block_cache(uintptr_t host_page_base, uint32_t guest_eip) const {
        sync_code_cache_generation();
        const auto& entry = block_lookup_cache[block_lookup_cache_slot(host_page_base, guest_eip)];
        if (entry.host_page_base != host_page_base || entry.guest_eip != guest_eip) return nullptr;
        return entry.block;
    }

    // Slow Path: Resolve address, permissions and faults
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

    static void IndexClonedHostPages(MmuCore& core) {
        auto* dir = core.page_directory.get();
        for (uint32_t l1_idx = 0; l1_idx < dir->l1_directory.size(); ++l1_idx) {
            const auto& chunk = dir->l1_directory[l1_idx];
            if (!chunk) continue;

            for (uint32_t l2_idx = 0; l2_idx < chunk->permissions.size(); ++l2_idx) {
                const auto perms = chunk->permissions[l2_idx];
                auto* page = chunk->pages[l2_idx];
                if (!page) continue;

                chunk->permissions[l2_idx] = perms & ~Property::ForceWriteSlow;

                const uint32_t guest_page = (l1_idx << 22) | (l2_idx << 12);
                auto& state = core.host_page_aliases[reinterpret_cast<uintptr_t>(page)];
                state.guest_pages.push_unique(guest_page);
                if (has_property(perms, Property::Exec)) state.exec_count++;
            }
        }
    }

    static MmuCore* create_core(std::unique_ptr<PageDirectory> page_directory, const MmuCore* parent = nullptr) {
        auto* core = new MmuCore();
        core->page_directory = std::move(page_directory);
        core->identity = next_identity();
        core->state_flags = 0;
#if FIBERCPU_LLVM_JIT
        LLVMJitMmuCreated();
        core->code_cache.llvm_cache_epoch = LLVMJitCacheEpoch();
#endif
        if (parent) {
            core->code_cache.block_budget_bytes = parent->code_cache.block_budget_bytes;
            core->code_cache.run_options = parent->code_cache.run_options;
        }
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
#if FIBERCPU_LLVM_JIT
            LLVMJitMmuDestroyed();
#endif
        }
    }

    void reset_runtime_tlb_tracking() {
        const uint64_t write_seq = core_ ? core_->runtime_tlb_shootdown_ring.write_seq : 0;
        runtime_tlb_read_seq = write_seq;
        runtime_tlb_publish_begin_seq = write_seq;
        runtime_tlb_publish_batch_depth = 0;
    }

    void begin_runtime_tlb_publish_batch() {
        if (runtime_tlb_publish_batch_depth++ == 0) {
            runtime_tlb_publish_begin_seq = core_ ? core_->runtime_tlb_shootdown_ring.write_seq : 0;
        }
    }

    void end_runtime_tlb_publish_batch() {
        if (runtime_tlb_publish_batch_depth == 0) return;
        runtime_tlb_publish_batch_depth--;
        if (runtime_tlb_publish_batch_depth == 0) {
            runtime_tlb_publish_begin_seq = core_ ? core_->runtime_tlb_shootdown_ring.write_seq : 0;
        }
    }

    void publish_runtime_tlb_flush_deduped(uint32_t guest_page) {
        if (!core_) return;

        // Apply sibling engines' pending translations before publishing this
        // one. Otherwise the read cursor below skips them and this engine can
        // keep using a stale translation for the rest of the current Run.
        sync_runtime_tlb_shootdowns();

        auto& ring = core_->runtime_tlb_shootdown_ring;
        const uint64_t write_seq = ring.write_seq;
        const uint64_t capacity = MmuCore::RuntimeTlbShootdownRing::kCapacity;
        if (runtime_tlb_publish_batch_depth != 0) {
            uint64_t window_begin = runtime_tlb_publish_begin_seq;
            const uint64_t ring_begin = write_seq > capacity ? write_seq - capacity : 0;
            if (window_begin < ring_begin) window_begin = ring_begin;

            for (uint64_t seq = window_begin; seq < write_seq; ++seq) {
                if (ring.guest_pages[seq % capacity] == guest_page) return;
            }
        }

        ring.guest_pages[write_seq % capacity] = guest_page;
        ring.write_seq = write_seq + 1;
        runtime_tlb_read_seq = ring.write_seq;
    }

    void publish_runtime_tlb_flush_all() {
        if (!core_) return;

        // See publish_runtime_tlb_flush_deduped: do not skip sibling events.
        sync_runtime_tlb_shootdowns();

        auto& ring = core_->runtime_tlb_shootdown_ring;
        ring.write_seq += MmuCore::RuntimeTlbShootdownRing::kCapacity + 1;
        runtime_tlb_read_seq = ring.write_seq;
        if (runtime_tlb_publish_batch_depth == 0) {
            runtime_tlb_publish_begin_seq = ring.write_seq;
        }
    }

    void publish_runtime_tlb_flush_range_or_all(uint32_t start, uint64_t end_addr) {
        if (!core_ || end_addr <= start) return;

        constexpr size_t capacity = MmuCore::RuntimeTlbShootdownRing::kCapacity;
        const uint64_t span_pages = (end_addr - start) / PAGE_SIZE;
        if (span_pages > capacity) {
            publish_runtime_tlb_flush_all();
            return;
        }
        if (span_pages == 1) {
            publish_runtime_tlb_flush_deduped(start);
            return;
        }

        // Apply sibling invalidations before advancing the local read cursor.
        // The range itself is unique. Mark overlaps with any earlier alias
        // invalidations once instead of rescanning the ring for every page.
        sync_runtime_tlb_shootdowns();
        auto& ring = core_->runtime_tlb_shootdown_ring;
        auto write_seq = ring.write_seq;
        std::bitset<capacity> already_published;
        if (runtime_tlb_publish_batch_depth != 0) {
            const auto ring_begin = write_seq > capacity ? write_seq - capacity : 0;
            const auto begin = std::max(runtime_tlb_publish_begin_seq, ring_begin);
            for (auto seq = begin; seq < write_seq; ++seq) {
                const auto page = ring.guest_pages[seq % capacity];
                if (page >= start && page < end_addr) already_published[(page - start) / PAGE_SIZE] = true;
            }
        }

        // If these entries overwrite a marked event, siblings that have not
        // consumed it are more than capacity entries behind and flush all.
        for (size_t index = 0; index < span_pages; ++index) {
            if (!already_published[index]) {
                ring.guest_pages[write_seq % capacity] = start + static_cast<uint32_t>(index) * PAGE_SIZE;
                ++write_seq;
            }
        }
        ring.write_seq = write_seq;
        runtime_tlb_read_seq = write_seq;
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
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
        reset_runtime_tlb_tracking();
    }

public:
    void sync_runtime_tlb_shootdowns() {
        if (!core_) return;

        const auto& ring = core_->runtime_tlb_shootdown_ring;
        const uint64_t write_seq = ring.write_seq;
        if (runtime_tlb_read_seq == write_seq) return;

        const uint64_t capacity = MmuCore::RuntimeTlbShootdownRing::kCapacity;
        if (write_seq - runtime_tlb_read_seq > capacity) {
            tlb.flush();
            runtime_tlb_read_seq = write_seq;
            return;
        }

        for (uint64_t seq = runtime_tlb_read_seq; seq < write_seq; ++seq) {
            tlb.flush_page(ring.guest_pages[seq % capacity]);
        }
        runtime_tlb_read_seq = write_seq;
    }

    [[nodiscard]] RuntimeTlbPublishBatchScope scoped_runtime_tlb_publish_batch() {
        return RuntimeTlbPublishBatchScope(this);
    }

    // Safe resolution without faulting
    [[nodiscard]] HostAddr resolve_safe_for_read(GuestAddr addr, Property access = Property::Read) {
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
        if (!has_property(current_perm, access)) return nullptr;

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

        if (has_property(current_perm, Property::ForceWriteSlow)) invalidate_code_cache_page(addr);

        return page_base + offset;
    }

public:
    Mmu() {
        core_ = create_core(std::make_unique<PageDirectory>());
        page_dir = core_->page_directory.get();
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_->code_cache.generation;
        reset_runtime_tlb_tracking();
    }

    Mmu(const Mmu& other) {
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        retain_core(core_);
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
        reset_runtime_tlb_tracking();
    }

    Mmu(Mmu&& other) noexcept {
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        other.core_ = nullptr;
        other.page_dir = nullptr;
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
        reset_runtime_tlb_tracking();
        other.clear_block_lookup_cache();
        other.seen_code_cache_generation_ = 0;
        other.reset_runtime_tlb_tracking();
    }

    Mmu& operator=(const Mmu& other) {
        if (this == &other) return *this;
        retain_core(other.core_);
        auto* old_core = core_;
        core_ = other.core_;
        page_dir = core_ ? core_->page_directory.get() : nullptr;
        release_core(old_core);
        tlb.flush();
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
        reset_runtime_tlb_tracking();
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
        clear_block_lookup_cache();
        seen_code_cache_generation_ = core_ ? core_->code_cache.generation : 0;
        reset_runtime_tlb_tracking();
        other.clear_block_lookup_cache();
        other.seen_code_cache_generation_ = 0;
        other.reset_runtime_tlb_tracking();
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

    static MmuCore* CloneCoreMappings(MmuCore* core) {
        if (!core || !core->page_directory) return CreateEmptyCore();

        auto cloned = std::make_unique<PageDirectory>();
        const auto& source = core->page_directory->l1_directory;
        for (size_t i = 0; i < source.size(); ++i) {
            if (source[i]) cloned->l1_directory[i] = std::make_unique<PageTableChunk>(*source[i]);
        }

        auto* cloned_core = create_core(std::move(cloned), core);
        IndexClonedHostPages(*cloned_core);
        return cloned_core;
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

#if FIBERCPU_BLOCK_COMPILATION
    struct PageRegionStamp {
        uintptr_t mmu_identity;
        uintptr_t host_page;
        uint32_t guest_page;
        uint64_t cache_generation;
        uint64_t invalidation_epoch;
        uint64_t arena_epoch;
        bool operator==(const PageRegionStamp&) const = default;
    };

    [[nodiscard]] std::optional<PageRegionStamp> page_region_stamp(uint32_t eip) {
        if (!core_ || code_cache_flush_pending()) return std::nullopt;
        const auto perms = get_property(eip);
        if (!has_property(perms, Property::Exec) || has_property(perms, Property::Write)) return std::nullopt;
        const auto backing = try_get_host_page_base(eip);
        if (!backing) return std::nullopt;
        auto& cache = code_cache();
        if (!cache.page_regions) cache.page_regions = std::make_unique<CodeCacheState::PageRegions>();
        return PageRegionStamp{core_->identity,
                               *backing,
                               eip & ~uint32_t(PAGE_MASK),
                               cache.generation,
                               cache.page_regions->invalidation_epoch,
                               cache.llvm_cache_epoch};
    }

    [[nodiscard]] bool publish_page_region(const PageRegionStamp& snapshot,
                                           std::span<const CodeCacheState::PageRegionEntry> members) {
        if (page_region_stamp(snapshot.guest_page) != snapshot) return false;
        return code_cache().InstallPageRegion(snapshot.guest_page, snapshot.host_page, members);
    }
#endif

    [[nodiscard]] HostPageSet collect_host_page_bases_for_guest_range(uint32_t addr, uint32_t size) const {
        HostPageSet host_page_bases;
        if (size == 0) return host_page_bases;

        const uint64_t start_page = static_cast<uint64_t>(addr) & ~static_cast<uint64_t>(PAGE_MASK);
        const uint64_t last_addr = std::min(uint64_t{addr} + size, uint64_t{1} << 32) - 1;
        const uint64_t end_page = last_addr & ~static_cast<uint64_t>(PAGE_MASK);
        for (uint64_t page = start_page;; page += PAGE_SIZE) {
            if (auto host_page_base = try_get_host_page_base(static_cast<uint32_t>(page))) {
                host_page_bases.push_unique(*host_page_base);
            }
            if (page == end_page) break;
        }
        return host_page_bases;
    }

    [[nodiscard]] BasicBlock* invalid_code_block() { return &code_cache().dummy_invalid_block; }

    [[nodiscard]] const BasicBlock* invalid_code_block() const { return &code_cache().dummy_invalid_block; }

    [[nodiscard]] void* try_allocate_block_bytes(size_t size, uint32_t guest_eip);

    [[nodiscard]] bool code_cache_epoch_current() const {
#if FIBERCPU_LLVM_JIT
        return code_cache().llvm_cache_epoch == LLVMJitCacheEpoch();
#else
        return true;
#endif
    }

    [[nodiscard]] bool code_cache_flush_pending() const {
#if FIBERCPU_LLVM_JIT
        if (LLVMJitCacheFlushPending() || !code_cache_epoch_current()) return true;
#endif
        return code_cache().pending_flush;
    }

    void begin_execution() { core_->execution_active = true; }
    void end_execution() {
        if (core_) core_->execution_active = false;
        if (deferred_exec_core_ != nullptr) {
            // The run that borrowed the old core's blocks is over.
            deferred_exec_core_->execution_active = false;
            release_core(deferred_exec_core_);
            deferred_exec_core_ = nullptr;
        }
    }

    // execve replaces the address space from inside a guest syscall. The old
    // core cannot be released yet because the currently executing block chain
    // still lives in its code cache, so keep it referenced until end_execution.
    [[nodiscard]] bool attach_core_for_exec(MmuCore* core, bool add_ref = true) {
        if (core == nullptr) return false;
        if (core == core_) return true;

        MmuCore* old_core = core_;
        if (old_core != nullptr && old_core->execution_active) {
            retain_core(old_core);
            if (deferred_exec_core_ != nullptr && deferred_exec_core_ != old_core) release_core(deferred_exec_core_);
            deferred_exec_core_ = old_core;
        }

        bind_core(core, add_ref);
#if FIBERCPU_LLVM_JIT
        if (!code_cache_epoch_current()) flush_pending_code_cache_before_run();
#endif
        return true;
    }

    void flush_pending_code_cache_before_run() {
        auto& cache = code_cache();
        if (core_->execution_active) return;
        bool global_flush = false;
#if FIBERCPU_LLVM_JIT
        const auto epoch = LLVMJitCacheEpoch();
        global_flush = cache.llvm_cache_epoch != epoch && cache.requested_block_bytes != 0;
        cache.llvm_cache_epoch = epoch;
#endif
        if (!cache.pending_flush && !global_flush) return;
        cache.HardResetReleaseAll(1, cache.pending_budget_flush || global_flush ? 1 : 0);
        on_code_cache_hard_reset();
    }

    void remember_allocated_block(BasicBlock* block) { code_cache().RememberAllocatedBlock(block); }

    [[nodiscard]] BasicBlock* lookup_cached_block(uint32_t eip) {
        sync_code_cache_generation();
        auto host_page_base = try_get_host_page_base(eip);
        if (!host_page_base) return nullptr;

        if (BasicBlock* cached = try_lookup_block_cache(*host_page_base, eip)) {
            return cached;
        }

        BasicBlock* block = code_cache().LookupBlock(BlockCacheKey{*host_page_base, eip});
        remember_block_lookup_cache(*host_page_base, eip, block);
        return block;
    }

    [[nodiscard]] const BasicBlock* lookup_cached_block(uint32_t eip) const {
        sync_code_cache_generation();
        auto host_page_base = try_get_host_page_base(eip);
        if (!host_page_base) return nullptr;

        if (BasicBlock* cached = try_lookup_block_cache(*host_page_base, eip)) {
            return cached;
        }

        const BasicBlock* block = code_cache().LookupBlock(BlockCacheKey{*host_page_base, eip});
        remember_block_lookup_cache(*host_page_base, eip, const_cast<BasicBlock*>(block));
        return block;
    }

    [[nodiscard]] BasicBlock* cache_decoded_block(uint32_t cache_eip, BasicBlock* block) {
        sync_code_cache_generation();
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto cache_key = make_block_cache_key(cache_eip);
        if (!cache_key) return block;

        const uint32_t last_guest_addr =
            block && block->end_eip > cache_eip ? static_cast<uint32_t>(block->end_eip - 1) : cache_eip;
        auto host_page_bases = collect_host_page_bases_for_guest_range(cache_eip, last_guest_addr - cache_eip + 1);
        HostPageSet affected_host_pages;
        BasicBlock* const candidate = block;
        block = code_cache().CacheDecodedBlock(*cache_key, host_page_bases, block, &affected_host_pages);
        // InvalidateHostPage unregisters a block's pages but leaves its pointer
        // in the cache entry. A later decode can revalidate that same block and
        // then publish a different replacement block; the replacement path
        // returns the existing block without touching registrations. Restore
        // them from the returned block's own range, or later writes to its code
        // pages would never invalidate it again. Cold decode path only, and
        // not taken when the new candidate was published normally.
        if (block != nullptr && block != candidate) {
            const uint32_t block_last = block->end_eip > cache_eip ? block->end_eip - 1 : cache_eip;
            auto block_pages = collect_host_page_bases_for_guest_range(cache_eip, block_last - cache_eip + 1);
            code_cache().EnsureBlockHostPagesRegistered(*cache_key, block_pages, &affected_host_pages);
        }
        remember_block_lookup_cache(cache_key->host_page_base, cache_key->guest_eip, block);
        refresh_smc_armed_for_host_pages(affected_host_pages);
        return block;
    }

    void reset_code_cache() {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        clear_block_lookup_cache();
        if (core_->execution_active) {
            auto& cache = code_cache();
            cache.InvalidateAll();
            ++cache.reset_request_epoch;
            cache.pending_flush = true;
            return;
        }
        code_cache().HardResetReleaseAll(1, 0);
        on_code_cache_hard_reset();
    }

    void erase_cached_block(uint32_t guest_eip) {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto cache_key = make_block_cache_key(guest_eip);
        if (cache_key) {
            invalidate_block_lookup_cache_entry(cache_key->host_page_base, cache_key->guest_eip);
            HostPageSet affected_host_pages;
            code_cache().EraseBlock(*cache_key, &affected_host_pages);
            refresh_smc_armed_for_host_pages(affected_host_pages);
        }
    }

    void invalidate_code_cache_page(uint32_t guest_addr) {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto host_page_base = try_get_host_page_base(guest_addr);
        if (host_page_base) {
            HostPageSet requested_host_pages;
            requested_host_pages.push_unique(*host_page_base);
            invalidate_block_lookup_cache_page(*host_page_base);
            HostPageSet affected_host_pages;
            code_cache().InvalidateHostPage(*host_page_base, &affected_host_pages);
            refresh_smc_armed_for_requested_and_affected_host_pages(requested_host_pages, affected_host_pages);
        }
    }

    void invalidate_code_cache_range(uint32_t addr, uint32_t size) {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto host_page_bases = collect_host_page_bases_for_guest_range(addr, size);
        for (uintptr_t host_page_base : host_page_bases) {
            invalidate_block_lookup_cache_page(host_page_base);
        }
        HostPageSet affected_host_pages;
        code_cache().InvalidateHostPages(host_page_bases, &affected_host_pages);
        refresh_smc_armed_for_requested_and_affected_host_pages(host_page_bases, affected_host_pages);
    }

    void invalidate_code_cache_host_pages(const HostPageSet& host_page_bases) {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        for (uintptr_t host_page_base : host_page_bases) {
            invalidate_block_lookup_cache_page(host_page_base);
        }
        HostPageSet affected_host_pages;
        code_cache().InvalidateHostPages(host_page_bases, &affected_host_pages);
        refresh_smc_armed_for_requested_and_affected_host_pages(host_page_bases, affected_host_pages);
    }

    void invalidate_code_cache_host_pages(const uintptr_t* host_page_bases, size_t count) {
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        if (!host_page_bases || count == 0) return;
        HostPageSet requested_host_pages;
        for (size_t i = 0; i < count; ++i) {
            if (host_page_bases[i] == 0) continue;
            invalidate_block_lookup_cache_page(host_page_bases[i]);
            requested_host_pages.push_unique(host_page_bases[i]);
        }
        if (requested_host_pages.empty()) return;
        HostPageSet affected_host_pages;
        code_cache().InvalidateHostPages(requested_host_pages, &affected_host_pages);
        refresh_smc_armed_for_requested_and_affected_host_pages(requested_host_pages, affected_host_pages);
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

    void protect_mapped_range(GuestAddr addr, uint32_t size, uint8_t perms_raw) {
        if (size == 0) return;
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto* dir = current_page_directory();
        const auto perms = static_cast<Property>(perms_raw) & Property::RWX;
        const uint32_t start = addr & ~PAGE_MASK;
        const uint64_t limit = std::min(uint64_t{addr} + size, uint64_t{1} << 32);
        const uint64_t end = (limit + PAGE_MASK) & ~uint64_t{PAGE_MASK};

        for (uint64_t curr = start; curr < end; curr += PAGE_SIZE) {
            const auto guest_page = static_cast<GuestAddr>(curr);
            auto& chunk = dir->l1_directory[guest_page >> 22];
            const uint32_t slot = (guest_page >> 12) & 0x3FF;
            if (!chunk || !chunk->pages[slot]) continue;

            auto* page = chunk->pages[slot];
            auto& entry_perms = chunk->permissions[slot];
            const auto old_perms = entry_perms;
            const auto new_perms = perms | (old_perms & Property::Dirty);
#if FIBERCPU_BLOCK_COMPILATION
            invalidate_code_before_permission_change(page, old_perms, new_perms);
#endif
            entry_perms = new_perms;
            update_host_page_alias(guest_page, page, old_perms, page, entry_perms);
        }
        tlb.flush();
        publish_runtime_tlb_flush_range_or_all(start, end);
    }

    bool map_host_page(GuestAddr addr, HostAddr host_page, uint8_t perms_raw) {
        if ((addr & PAGE_MASK) || !host_page) return false;
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto* dir = current_page_directory();
        auto perms = static_cast<Property>(perms_raw) & Property::RWX;
        auto& chunk = dir->l1_directory[addr >> 22];
        const uint32_t slot = (addr >> 12) & 0x3FF;
        if (!chunk) chunk = std::make_unique<PageTableChunk>();

        auto* old_page = chunk->pages[slot];
        const auto old_perms = chunk->permissions[slot];
        if (old_page == host_page) {
            perms = perms | (old_perms & Property::Dirty);
#if FIBERCPU_BLOCK_COMPILATION
            invalidate_code_before_permission_change(old_page, old_perms, perms);
#endif
        } else if (old_page && has_property(old_perms, Property::Exec)) {
            HostPageSet affected_host_pages;
            code_cache().InvalidateHostPage(reinterpret_cast<uintptr_t>(old_page), &affected_host_pages);
            refresh_smc_armed_for_host_pages(affected_host_pages);
        }

        chunk->pages[slot] = host_page;
        chunk->permissions[slot] = perms;
        update_host_page_alias(addr, old_page, old_perms, host_page, perms);
        tlb.flush_page(addr);
        publish_runtime_tlb_flush_deduped(addr);
        return true;
    }

    void munmap(GuestAddr addr, uint32_t size) {
        if (size == 0) return;
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        auto* dir = current_page_directory();
        const uint32_t start = addr & ~PAGE_MASK;
        const uint64_t limit = std::min(uint64_t{addr} + size, uint64_t{1} << 32);
        const uint64_t end = (limit + PAGE_MASK) & ~uint64_t{PAGE_MASK};

        for (uint64_t curr = start; curr < end; curr += PAGE_SIZE) {
            const auto guest_page = static_cast<GuestAddr>(curr);
            auto& chunk = dir->l1_directory[guest_page >> 22];
            const uint32_t slot = (guest_page >> 12) & 0x3FF;
            if (!chunk || !chunk->pages[slot]) continue;

            auto* old_page = chunk->pages[slot];
            const auto old_perms = chunk->permissions[slot];
            chunk->pages[slot] = nullptr;
            chunk->permissions[slot] = Property::None;
            remove_host_page_alias(guest_page, old_page, old_perms);
            refresh_smc_armed_for_host_page(old_page);
        }
        tlb.flush();
        publish_runtime_tlb_flush_range_or_all(start, end);
    }

    // API: reset_memory - Replace page directory contents with a fresh empty one.
    // Discards the guest mappings while callers retain their backing pages.
    // Keep core identity stable so C# MMU handles remain valid.
    [[nodiscard]] bool reset_memory() {
        if (core_ && core_->execution_active) return false;
        [[maybe_unused]] auto runtime_tlb_batch = scoped_runtime_tlb_publish_batch();
        if (!core_) {
            bind_core(CreateEmptyCore(), false);
            return true;
        }
        core_->page_directory = std::make_unique<PageDirectory>();
        page_dir = core_->page_directory.get();
        core_->code_cache.HardResetReleaseAll(1, 0);
        on_code_cache_hard_reset();
        core_->host_page_aliases.clear();
        tlb.flush();
        publish_runtime_tlb_flush_all();
        return true;
    }

    void set_code_cache_budget_bytes(size_t bytes) { code_cache().SetBlockBudgetBytes(bytes); }

    [[nodiscard]] uintptr_t page_directory_identity() const { return core_ ? core_->identity : 0; }

    void flush_tlb_only() { tlb.flush(); }

    [[nodiscard]] MmuCore* detach_core() {
        if (owning_engine_is_executing()) return nullptr;
        if (!core_) {
            bind_core(CreateEmptyCore(), false);
        }

        auto* detached = RetainCore(core_);
        bind_core(create_core(std::make_unique<PageDirectory>(), core_), false);
        return detached;
    }

    [[nodiscard]] bool attach_core(MmuCore* core, bool add_ref = true) {
        if (owning_engine_is_executing()) return false;
        if (!core) {
            bind_core(CreateEmptyCore(), false);
            return true;
        }
        bind_core(core, add_ref);
        return true;
    }

    // Internal helper for resolution (TLB or Slow) without hooks
    [[nodiscard]] FORCE_INLINE MemResult<HostAddr> resolve_ptr(GuestAddr addr, Property req_perm);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read_no_utlb(GuestAddr addr);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<void> write_no_utlb(GuestAddr addr, T val);

    // Returns a page-local span backed by an already usable TLB entry.
    // The caller must ensure its entire span stays inside this page.
    template <bool Write>
    [[nodiscard]] HostAddr try_bulk_page(GuestAddr address) {
        if (mem_hook) return nullptr;
        const auto index = (address >> PAGE_SHIFT) & TLB_INDEX_MASK;
        const auto& entry = Write ? tlb.write_tlb[index] : tlb.read_tlb[index];
        if ((Write ? entry.WriteTag() : entry.ReadTag()) != (address & ~PAGE_MASK)) return nullptr;
        return reinterpret_cast<HostAddr>(entry.addend + address);
    }

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<T> read_tlb_only(GuestAddr addr, MicroTLB* utlb);

    template <typename T>
    [[nodiscard]] FORCE_INLINE MemResult<HostAddr> try_write_target_tlb(GuestAddr addr, MicroTLB* utlb);

    template <typename T, bool Store, bool FlushOnMiss = true>
    [[nodiscard]] FORCE_INLINE MemResult<std::conditional_t<Store, void, HostAddr>> write_tlb_impl(GuestAddr addr,
                                                                                                   T val,
                                                                                                   MicroTLB* utlb);

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

    // Replay-only access when both pages already have valid L2 entries.
    template <bool Store>
    [[nodiscard]] bool try_cross_page_dword_tlb(GuestAddr addr, std::byte* value);

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
