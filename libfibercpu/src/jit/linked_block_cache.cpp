#include "jit/linked_block_cache.h"

#include "jit/block_recipe.h"
#include "jit/linked_block_cache_host.h"

#include <algorithm>
#include <atomic>
#include <cstdlib>
#include <type_traits>
#include <utility>

namespace fiberish {
namespace {

#include "linked_block_cache.inc"

std::atomic<uint64_t> linked_hits{0};

}  // namespace

bool LinkedBlockCacheEnabled() {
    static const bool enabled = [] {
        const auto* value = std::getenv("FIBERCPU_LINKED_BLOCK_CACHE");
        return (!value || std::string_view(value) != "0") && LinkedBlockCacheHostCPU() == linked_cache_cpu;
    }();
    return enabled;
}

bool TryLinkedBlockCache(EmuState& state, BasicBlock& block) {
    if (!LinkedBlockCacheEnabled() || !block_recipe::CanPromoteBlock(state, block)) return false;
    block_recipe::Plan plan;
    if (!block_recipe::PlanBlock(state, block, true, plan)) return false;
    const auto key = block_recipe::BlockKey(plan.Steps(), true, plan.exit);
    const auto found = std::ranges::lower_bound(linked_cache_entries, std::string_view(key), {},
                                                &std::pair<std::string_view, HandlerFunc>::first);
    if (found == std::ranges::end(linked_cache_entries) || found->first != key) return false;
    block_recipe::InstallBlockHandler(block, plan.ReplayHandlers(), found->second);
    linked_hits.fetch_add(1, std::memory_order_relaxed);
    return true;
}

uint64_t LinkedBlockHitCount() { return linked_hits.load(std::memory_order_relaxed); }

}  // namespace fiberish
