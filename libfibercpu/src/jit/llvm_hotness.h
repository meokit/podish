#pragma once

#include <algorithm>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace fiberish::jit_hotness {

using Clock = std::chrono::steady_clock;

enum class QueueResult { Submitted, Retry, Invalid, Stop };
enum class Observation { New, Refreshed, Deferred };

inline uint16_t CandidateRetryCount(uint16_t threshold) { return std::max(uint16_t{1024}, threshold); }

inline float BenefitPerCost(unsigned covered, unsigned memory_ops, unsigned saved_dispatches) {
    return float(1 + 4 * saved_dispatches + covered / 4) / (8 + covered + 2 * memory_ops);
}

// Owned and sampled by the VM thread. Only touch a saved block after the
// caller has validated its identity against the current code cache.
template <class Target>
class HotCandidates {
    static constexpr std::size_t kMaxCandidates = 128;
    static constexpr uint16_t kCandidateCounter = UINT16_MAX;
    static constexpr auto kSampleInterval = std::chrono::milliseconds(2);
    static constexpr auto kCandidateExpiry = std::chrono::milliseconds(60);

    struct Candidate {
        Target target;
        float benefit_per_cost;
        Clock::time_point sampled;
        Clock::time_point last_active;
        uint16_t counter = kCandidateCounter;
        float rate = 1;
        bool sampled_once = false;

        float Score() const { return rate * benefit_per_cost; }

        void Sample(Clock::time_point now) {
            const auto current = target.Count();
            const unsigned visits = counter >= current ? unsigned(counter) - current : 0;
            counter = current;
            if (visits) last_active = now;
            const auto window = std::max(std::chrono::milliseconds(1),
                                         std::chrono::duration_cast<std::chrono::milliseconds>(now - sampled));
            rate = 0.5f * (rate + float(visits) / window.count());
            sampled = now;
            sampled_once = true;
        }
    };

    std::vector<Candidate> candidates_;
    Clock::time_point next_sample_{};

public:
    bool Empty() const { return candidates_.empty(); }

    bool BeginSample(Clock::time_point now) {
        if (Empty() || now < next_sample_) return false;
        next_sample_ = now + kSampleInterval;
        return true;
    }

    void Clear() {
        candidates_.clear();
        next_sample_ = {};
    }

    template <class IsCurrent, class EstimateBenefit>
    Observation Observe(const Target& target, Clock::time_point now, uint16_t retry, IsCurrent is_current,
                        EstimateBenefit estimate) {
        auto existing = std::ranges::find_if(
            candidates_, [&](const Candidate& candidate) { return candidate.target.Block() == target.Block(); });
        if (existing != candidates_.end()) {
            if (existing->target == target) {
                existing->Sample(now);
                target.SetCount(existing->counter = kCandidateCounter);
                return Observation::Refreshed;
            }
            candidates_.erase(existing);
        }

        if (candidates_.size() == kMaxCandidates) {
            auto weakest = std::ranges::min_element(candidates_, {}, &Candidate::Score);
            // Give new candidates a window to establish a rate before eviction.
            if (weakest->Score() > 1.f || !weakest->sampled_once) {
                target.SetCount(retry);
                return Observation::Deferred;
            }
            if (is_current(weakest->target)) weakest->target.SetCount(retry);
            candidates_.erase(weakest);
        }
        target.SetCount(kCandidateCounter);
        candidates_.push_back({target, estimate(), now, now});
        return Observation::New;
    }

    template <class IsCurrent, class CanSubmit, class Eligible, class Submit>
    void Schedule(Clock::time_point now, uint16_t retry, IsCurrent is_current, CanSubmit can_submit, Eligible eligible,
                  Submit submit) {
        std::erase_if(candidates_, [&](Candidate& candidate) {
            if (!is_current(candidate.target)) return true;
            candidate.Sample(now);
            if (now - candidate.last_active < kCandidateExpiry) return false;
            candidate.target.SetCount(retry);
            return true;
        });

        while (!Empty() && can_submit()) {
            auto best = candidates_.end();
            for (auto it = candidates_.begin(); it != candidates_.end(); ++it) {
                if (!eligible(it->target)) continue;
                if (best == candidates_.end() || it->Score() > best->Score()) best = it;
            }
            if (best == candidates_.end()) break;
            const auto result = submit(best->target);
            if (result == QueueResult::Retry || result == QueueResult::Stop) break;
            if (result == QueueResult::Submitted) best->target.SetCount(0);
            candidates_.erase(best);
        }
    }
};

}  // namespace fiberish::jit_hotness
