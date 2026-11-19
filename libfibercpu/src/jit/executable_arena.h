#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <optional>

namespace fiberish {

// One process-wide budget covers the MAP_JIT reservations and LLVM's
// separately mapped resident data. Executable addresses stay stable until
// their reservation is released.
class ExecutableArena final : public std::enable_shared_from_this<ExecutableArena> {
public:
    struct Usage {
        size_t capacity;
        size_t total;
        size_t llvm;
    };

    enum class Lifetime { Generation, Permanent };

    class Region {
    public:
        ~Region();
        Region(const Region&) = delete;
        Region& operator=(const Region&) = delete;
        Region(Region&& other) noexcept;
        uint8_t* Data() const { return data_; }
        size_t Size() const { return size_; }

    private:
        friend class ExecutableArena;
        Region(std::shared_ptr<ExecutableArena> arena, uint8_t* data, size_t size, Lifetime lifetime);

        std::shared_ptr<ExecutableArena> arena_;
        uint8_t* const data_;
        const size_t size_;
        const Lifetime lifetime_;
    };

    static std::shared_ptr<ExecutableArena> Shared(size_t requested_bytes = 0);
    static Usage SharedUsage();
    ~ExecutableArena();
    std::unique_ptr<Region> Reserve(size_t bytes, Lifetime lifetime);
    std::optional<Region> ReserveData(size_t bytes);
    bool FitsFreshGeneration(size_t code_bytes, size_t data_bytes) const;

private:
    ExecutableArena(uint8_t* base, size_t capacity);
    void Release(uint8_t* data, size_t size, Lifetime lifetime);

    uint8_t* const base_;
    const size_t capacity_;
    mutable std::mutex mutex_;
    std::map<size_t, size_t> free_ranges_;
    size_t used_bytes_ = 0;
    size_t permanent_bytes_ = 0;
};

class ScopedJitWrite {
public:
    ScopedJitWrite();
    ~ScopedJitWrite();
    ScopedJitWrite(const ScopedJitWrite&) = delete;
    ScopedJitWrite& operator=(const ScopedJitWrite&) = delete;
};

void FlushJitInstructions(void* address, size_t bytes);

}  // namespace fiberish
