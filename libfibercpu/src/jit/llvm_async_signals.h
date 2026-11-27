#pragma once

#include <atomic>

namespace fiberish {

class AsyncSignals {
    std::atomic<bool> closed_{false};

public:
    std::atomic<bool> ready{false};

    bool IsClosed() const noexcept { return closed_.load(std::memory_order_acquire); }

    void Cancel() noexcept { closed_.store(true, std::memory_order_release); }
};

}  // namespace fiberish
