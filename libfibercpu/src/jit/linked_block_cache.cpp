#include "jit/linked_block_cache.h"

#include "jit/block_recipe.h"
#include "jit/linked_block_cache_host.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cstdlib>
#include <type_traits>

namespace fiberish {
namespace {

struct LinkedBlockEntry {
    block_recipe::BlockKeyView key;
    HandlerFunc handler;
};

struct LinkedPageMember {
    const block_recipe::BlockKeyView* key;
    HandlerFunc handler;
    uint32_t offset;
};

struct LinkedPageRegion {
    std::span<const LinkedPageMember> members;
};

#include "linked_block_cache.inc"

std::atomic<uint64_t> linked_hits{0};
std::atomic<uint64_t> linked_page_hits{0};

bool TryLinkedPageRegion(EmuState& state, BasicBlock& root, block_recipe::BlockKeyView key) {
    if (!root.chain.has_region_context || !LinkedPageRegionsEnabled()) return false;
    const auto recipe = std::ranges::lower_bound(linked_cache_keys, key);
    if (recipe == std::end(linked_cache_keys) || *recipe != key) return false;
    auto& mmu = state.mmu;
    const auto page = root.start_eip() & ~uint32_t(mem::PAGE_MASK);
    const auto offset = root.start_eip() - page;
    for (const auto& region : linked_page_regions) {
        if (!std::ranges::any_of(region.members,
                                 [&](const auto& member) { return member.offset == offset && member.key == recipe; }))
            continue;

        const auto stamp = mmu.page_region_stamp(page);
        if (!stamp || mmu.code_cache().page_regions->Contains(page)) return false;
        using Entry = mem::CodeCacheState::PageRegionEntry;
        std::vector<Entry> entries;
        entries.reserve(region.members.size());
        for (const auto& member : region.members) {
            const auto eip = page + member.offset;
            auto* block = mmu.lookup_cached_block(eip);
            if (!block || !block->is_valid() || !block->chain.has_region_context) break;
            entries.push_back({eip, block, block->invalidation_revision, member.handler});
        }
        if (entries.size() != region.members.size()) continue;

        std::vector<block_recipe::Plan> plans(entries.size());
        bool matching = true;
        for (size_t i = 0; i < plans.size(); ++i) {
            auto& plan = plans[i];
            if (!block_recipe::PlanBlock(state, *entries[i].block, true, plan) ||
                plan.Key(true) != *region.members[i].key) {
                matching = false;
                break;
            }
        }
        if (!matching || !mmu.publish_page_region(*stamp, entries)) continue;
        for (size_t i = 0; i < entries.size(); ++i) {
            block_recipe::InstallBlockHandler(*entries[i].block, plans[i].ReplayHandlers(), entries[i].handler);
            entries[i].block->chain.llvm_hot_count = 0;
        }
        linked_hits.fetch_add(entries.size(), std::memory_order_relaxed);
        linked_page_hits.fetch_add(1, std::memory_order_relaxed);
        return true;
    }
    return false;
}

}  // namespace

bool LinkedBlockCacheEnabled() {
    static const bool enabled = [] {
        const auto* value = std::getenv("FIBERCPU_LINKED_BLOCK_CACHE");
        return (!value || std::string_view(value) != "0") && LinkedBlockCacheHostCPU() == linked_cache_cpu;
    }();
    return enabled;
}

bool LinkedPageRegionsEnabled() {
    if (linked_page_regions.empty()) return false;
    static const bool enabled = [] {
        const auto* value = std::getenv("FIBERCPU_LINKED_PAGE_REGIONS");
        return (!value || std::string_view(value) != "0") && LinkedBlockCacheEnabled();
    }();
    return enabled;
}

bool TryLinkedBlockCache(EmuState& state, BasicBlock& block) {
    if (!LinkedBlockCacheEnabled() || !block_recipe::CanPromoteBlock(state, block)) return false;
    block_recipe::Plan plan;
    if (!block_recipe::PlanBlock(state, block, true, plan)) return false;
    const auto key = plan.Key(true);
    if (TryLinkedPageRegion(state, block, key)) return true;
    const auto found = std::ranges::lower_bound(linked_cache_entries, key, {}, &LinkedBlockEntry::key);
    if (found == std::ranges::end(linked_cache_entries) || found->key != key) return false;
    block_recipe::InstallBlockHandler(block, plan.ReplayHandlers(), found->handler);
    linked_hits.fetch_add(1, std::memory_order_relaxed);
    return true;
}

uint64_t LinkedBlockHitCount() { return linked_hits.load(std::memory_order_relaxed); }
uint64_t LinkedPageRegionHitCount() { return linked_page_hits.load(std::memory_order_relaxed); }

}  // namespace fiberish
