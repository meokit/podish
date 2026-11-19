#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>

namespace fiberish {

// Both online compilers share one MAP_JIT mapping. Reservations are returned
// when their last owner drops them, and never move after publication.
class ExecutableArena final : public std::enable_shared_from_this<ExecutableArena> {
public:
    class Region {
    public:
        ~Region();
        Region(const Region&) = delete;
        Region& operator=(const Region&) = delete;
        uint8_t* Data() const { return data_; }
        size_t Size() const { return size_; }

    private:
        friend class ExecutableArena;
        Region(std::shared_ptr<ExecutableArena> arena, uint8_t* data, size_t size);

        std::shared_ptr<ExecutableArena> arena_;
        uint8_t* const data_;
        const size_t size_;
    };

    static std::shared_ptr<ExecutableArena> Shared();
    ~ExecutableArena();
    std::unique_ptr<Region> Reserve(size_t bytes);

private:
    explicit ExecutableArena(uint8_t* base);
    void Release(uint8_t* data, size_t size);

    uint8_t* const base_;
    std::mutex mutex_;
    std::map<size_t, size_t> free_ranges_;
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
