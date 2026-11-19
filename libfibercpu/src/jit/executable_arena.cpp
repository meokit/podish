#include "jit/executable_arena.h"

#include <iterator>
#include <utility>

#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <sys/mman.h>
#include <unistd.h>

namespace fiberish {
namespace {

constexpr size_t kArenaBytes = 128 * 1024 * 1024;

}  // namespace

ExecutableArena::Region::Region(std::shared_ptr<ExecutableArena> arena, uint8_t* data, size_t size)
    : arena_(std::move(arena)), data_(data), size_(size) {}

ExecutableArena::Region::~Region() { arena_->Release(data_, size_); }

ExecutableArena::ExecutableArena(uint8_t* base) : base_(base), free_ranges_{{0, kArenaBytes}} {}

ExecutableArena::~ExecutableArena() { munmap(base_, kArenaBytes); }

std::shared_ptr<ExecutableArena> ExecutableArena::Shared() {
    static const auto arena = []() -> std::shared_ptr<ExecutableArena> {
        if (!pthread_jit_write_protect_supported_np()) return {};
        void* memory =
            mmap(nullptr, kArenaBytes, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON | MAP_JIT, -1, 0);
        if (memory == MAP_FAILED) return {};
        return std::shared_ptr<ExecutableArena>(new ExecutableArena(static_cast<uint8_t*>(memory)));
    }();
    return arena;
}

std::unique_ptr<ExecutableArena::Region> ExecutableArena::Reserve(size_t bytes) {
    if (!bytes || bytes > kArenaBytes) return {};
    const size_t page_size = static_cast<size_t>(getpagesize());
    const size_t size = (bytes + page_size - 1) & ~(page_size - 1);
    std::lock_guard lock(mutex_);
    for (auto it = free_ranges_.begin(); it != free_ranges_.end(); ++it) {
        if (it->second < size) continue;
        const auto [offset, available] = *it;
        free_ranges_.erase(it);
        if (available > size) free_ranges_.emplace(offset + size, available - size);
        return std::unique_ptr<Region>(new Region(shared_from_this(), base_ + offset, size));
    }
    return {};
}

void ExecutableArena::Release(uint8_t* data, size_t size) {
    size_t offset = static_cast<size_t>(data - base_);
    std::lock_guard lock(mutex_);
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
