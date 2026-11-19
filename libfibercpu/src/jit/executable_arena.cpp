#include "jit/executable_arena.h"

#include <iterator>
#include <limits>
#include <utility>

#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <sys/mman.h>
#include <unistd.h>

namespace fiberish {
namespace {

constexpr size_t kDefaultArenaBytes = 128 * 1024 * 1024;

struct SharedArena {
    std::mutex mutex;
    std::shared_ptr<ExecutableArena> instance;
};

SharedArena& ArenaState() {
    static SharedArena state;
    return state;
}

}  // namespace

ExecutableArena::Region::Region(std::shared_ptr<ExecutableArena> arena, uint8_t* data, size_t size, Lifetime lifetime)
    : arena_(std::move(arena)), data_(data), size_(size), lifetime_(lifetime) {}

ExecutableArena::Region::Region(Region&& other) noexcept
    : arena_(std::move(other.arena_)), data_(other.data_), size_(other.size_), lifetime_(other.lifetime_) {}

ExecutableArena::Region::~Region() {
    if (arena_) arena_->Release(data_, size_, lifetime_);
}

ExecutableArena::ExecutableArena(uint8_t* base, size_t capacity)
    : base_(base), capacity_(capacity), free_ranges_{{0, capacity}} {}

ExecutableArena::~ExecutableArena() { munmap(base_, capacity_); }

std::shared_ptr<ExecutableArena> ExecutableArena::Shared(size_t requested_bytes) {
    const size_t page_size = static_cast<size_t>(getpagesize());
    if (requested_bytes > std::numeric_limits<size_t>::max() - (page_size - 1)) return {};
    const size_t rounded = (requested_bytes + page_size - 1) & ~(page_size - 1);

    auto& state = ArenaState();
    std::lock_guard lock(state.mutex);
    if (state.instance) {
        if (rounded && rounded != state.instance->capacity_) return {};
        return state.instance;
    }

    if (!pthread_jit_write_protect_supported_np()) return {};
    const size_t capacity = rounded ? rounded : kDefaultArenaBytes;
    void* memory = mmap(nullptr, capacity, PROT_READ | PROT_WRITE | PROT_EXEC,
                        MAP_PRIVATE | MAP_ANON | MAP_JIT, -1, 0);
    if (memory == MAP_FAILED) return {};
    state.instance = std::shared_ptr<ExecutableArena>(new ExecutableArena(static_cast<uint8_t*>(memory), capacity));
    return state.instance;
}

ExecutableArena::Usage ExecutableArena::SharedUsage() {
    auto& state = ArenaState();
    std::lock_guard lock(state.mutex);
    if (!state.instance) return {};
    const auto& arena = *state.instance;
    std::lock_guard arena_lock(arena.mutex_);
    return {arena.capacity_, arena.used_bytes_, arena.used_bytes_ - arena.permanent_bytes_};
}

std::unique_ptr<ExecutableArena::Region> ExecutableArena::Reserve(size_t bytes, Lifetime lifetime) {
    if (!bytes || bytes > capacity_) return {};
    const size_t page_size = static_cast<size_t>(getpagesize());
    const size_t size = (bytes + page_size - 1) & ~(page_size - 1);
    std::lock_guard lock(mutex_);
    if (size > capacity_ - used_bytes_) return {};
    for (auto it = free_ranges_.begin(); it != free_ranges_.end(); ++it) {
        if (it->second < size) continue;
        const auto [offset, available] = *it;
        free_ranges_.erase(it);
        if (available > size) free_ranges_.emplace(offset + size, available - size);
        used_bytes_ += size;
        if (lifetime == Lifetime::Permanent) permanent_bytes_ += size;
        return std::unique_ptr<Region>(new Region(shared_from_this(), base_ + offset, size, lifetime));
    }
    return {};
}

std::optional<ExecutableArena::Region> ExecutableArena::ReserveData(size_t bytes) {
    if (bytes == 0) return Region({}, nullptr, 0, Lifetime::Generation);
    std::lock_guard lock(mutex_);
    if (bytes > capacity_ - used_bytes_) return std::nullopt;
    used_bytes_ += bytes;
    return Region(shared_from_this(), nullptr, bytes, Lifetime::Generation);
}

bool ExecutableArena::FitsFreshGeneration(size_t code_bytes, size_t data_bytes) const {
    std::lock_guard lock(mutex_);
    const size_t available = capacity_ - permanent_bytes_;
    return code_bytes <= available && data_bytes <= available - code_bytes;
}

void ExecutableArena::Release(uint8_t* data, size_t size, Lifetime lifetime) {
    std::lock_guard lock(mutex_);
    used_bytes_ -= size;
    if (lifetime == Lifetime::Permanent) permanent_bytes_ -= size;
    if (!data) return;

    size_t offset = static_cast<size_t>(data - base_);
    auto next = free_ranges_.lower_bound(offset);
    if (next != free_ranges_.begin()) {
        const auto prev = std::prev(next);
        if (prev->first + prev->second == offset) {
            offset = prev->first;
            size += prev->second;
            free_ranges_.erase(prev);
        }
    }
    if (next != free_ranges_.end() && offset + size == next->first) {
        size += next->second;
        free_ranges_.erase(next);
    }
    free_ranges_.emplace(offset, size);
}

ScopedJitWrite::ScopedJitWrite() { pthread_jit_write_protect_np(0); }

ScopedJitWrite::~ScopedJitWrite() { pthread_jit_write_protect_np(1); }

void FlushJitInstructions(void* address, size_t bytes) { sys_icache_invalidate(address, bytes); }

}  // namespace fiberish
