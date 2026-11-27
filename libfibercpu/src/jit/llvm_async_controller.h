#pragma once

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <memory>
#include <optional>
#include <utility>
#include <vector>

#include "jit/llvm_async_signals.h"
#include "jit/llvm_hotness.h"

namespace fiberish {

// Shared per-MMU asynchronous JIT scheduling state used by both x86 and AArch64.
// Detaches without blocking on destruction; final MMU teardown drains workers.
template <typename PendingItem, typename HotItem>
class AsyncJitController {
public:
    std::shared_ptr<AsyncSignals> signals = std::make_shared<AsyncSignals>();
    std::vector<PendingItem> pending;
    jit_hotness::HotCandidates<HotItem> candidates;
    unsigned poll_ticks = 0;

    ~AsyncJitController() {
        pending.clear();
        signals->Cancel();
    }

    void Cancel() {
        pending.clear();
        candidates.Clear();
        signals->Cancel();
        signals = std::make_shared<AsyncSignals>();
    }

    template <typename IsDone, typename Publish>
    void DrainCompleted(IsDone&& is_done, Publish&& publish) {
        if (!signals->ready.exchange(false, std::memory_order_acquire)) return;
        std::erase_if(pending, [&](const PendingItem& item) {
            if (!is_done(item)) return false;
            publish(item);
            return true;
        });
    }

    template <typename IsDone, typename Publish, typename ScheduleFn>
    void Poll(unsigned schedule_mask, IsDone&& is_done, Publish&& publish, ScheduleFn&& schedule) {
        DrainCompleted(std::forward<IsDone>(is_done), std::forward<Publish>(publish));
        if (!candidates.Empty() && (schedule_mask == 0 || (++poll_ticks & schedule_mask) == 0))
            schedule(jit_hotness::Clock::now());
    }

    template <typename IsCurrent, typename CanQueue, typename PageAvailable, typename Submit>
    void Schedule(bool flush_pending, jit_hotness::Clock::time_point now, uint16_t retry_count, IsCurrent&& is_current,
                  CanQueue&& can_queue, PageAvailable&& page_available, Submit&& submit) {
        if (flush_pending || !candidates.BeginSample(now)) return;
        candidates.Schedule(now, retry_count, std::forward<IsCurrent>(is_current), std::forward<CanQueue>(can_queue),
                            std::forward<PageAvailable>(page_available), std::forward<Submit>(submit));
    }

    template <typename IsPending, typename IsCurrent, typename EstimateBenefit>
    std::optional<jit_hotness::Observation> Observe(const HotItem& target, jit_hotness::Clock::time_point now,
                                                    uint16_t retry_count, IsPending&& is_pending,
                                                    IsCurrent&& is_current, EstimateBenefit&& estimate_benefit) {
        if (std::ranges::any_of(pending, std::forward<IsPending>(is_pending))) return std::nullopt;
        return candidates.Observe(target, now, retry_count, std::forward<IsCurrent>(is_current),
                                  std::forward<EstimateBenefit>(estimate_benefit));
    }
};

}  // namespace fiberish
