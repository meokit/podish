#pragma once

#include <cstddef>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>

namespace fiberish {

// One process-wide budget covers code and nearby non-executable data.
// Both address ranges stay reserved for the lifetime of the arena.
class ExecutableArena final : public std::enable_shared_from_this<ExecutableArena> {
    struct Pool {
        uint8_t* base;
        std::map<size_t, size_t> free_ranges;
    };

public:
    // Each of the two ranges can span the configured budget. Their entire
    // address window must fit in the range of an AArch64 Page21 relocation.
    static constexpr size_t kMaxLLVMCapacity = size_t{1} << 31;

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
        uint8_t* Data() const { return pool_.base + offset_; }
        size_t Size() const { return size_; }

    private:
        friend class ExecutableArena;
        Region(std::shared_ptr<ExecutableArena> arena, Pool& pool, size_t offset, size_t size,
               Lifetime lifetime);

        std::shared_ptr<ExecutableArena> arena_;
        Pool& pool_;
        const size_t offset_;
        const size_t size_;
        const Lifetime lifetime_;
    };

    static std::shared_ptr<ExecutableArena> Shared(size_t requested_bytes = 0);
    static Usage SharedUsage();
    ~ExecutableArena();
    std::unique_ptr<Region> Reserve(size_t bytes, Lifetime lifetime);
    std::unique_ptr<Region> ReserveData(size_t bytes);
    bool FitsFreshGeneration(size_t code_bytes, size_t data_bytes) const;

private:
    ExecutableArena(uint8_t* code, uint8_t* data, size_t capacity);
    std::unique_ptr<Region> Reserve(Pool& pool, size_t bytes, Lifetime lifetime);
    void Release(Pool& pool, size_t offset, size_t size, Lifetime lifetime);

    Pool code_;
    Pool data_;
    const size_t capacity_;
    mutable std::mutex mutex_;
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
