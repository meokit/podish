#pragma once

#include <atomic>
#include <condition_variable>
#include <cstddef>
#include <mutex>

namespace fiberish {

class AsyncSignals {
    std::mutex mutex_;
    std::condition_variable idle_;
    size_t active_ = 0;
    bool closing_ = false;

public:
    std::atomic<bool> ready{false};

    bool Begin() {
        std::lock_guard lock(mutex_);
        if (closing_) return false;
        ++active_;
        return true;
    }

    void End() {
        std::lock_guard lock(mutex_);
        if (--active_ == 0) idle_.notify_one();
    }

    void Close() {
        std::unique_lock lock(mutex_);
        closing_ = true;
        idle_.wait(lock, [&] { return active_ == 0; });
    }
};

}  // namespace fiberish
