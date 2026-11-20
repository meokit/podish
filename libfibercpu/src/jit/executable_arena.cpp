#include "jit/executable_arena.h"

#include <algorithm>
#include <iterator>
#include <limits>
#include <utility>

#include <libkern/OSCacheControl.h>
#if FIBERCPU_LLVM_JIT
#include <mach/mach.h>
#include <mach/mach_vm.h>
#endif
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

#if FIBERCPU_LLVM_JIT
constexpr uintptr_t kPage21Window = uintptr_t{1} << 32;

uint8_t* ReserveNearbyData(uint8_t* code, size_t capacity) {
    const auto code_address = reinterpret_cast<uintptr_t>(code);
    const auto code_end = code_address + capacity;
    const auto task = mach_task_self();
    const auto reserve = [&](uintptr_t address) -> uint8_t* {
        mach_vm_address_t candidate = address;
        // Do not pass VM_FLAGS_OVERWRITE. The reservation must fail if another
        // mapping occupies any part of this address range.
        if (mach_vm_map(task, &candidate, capacity, 0, VM_FLAGS_FIXED,
                        MEMORY_OBJECT_NULL, 0, false, VM_PROT_NONE,
                        VM_PROT_READ | VM_PROT_WRITE, VM_INHERIT_DEFAULT) != KERN_SUCCESS)
            return nullptr;
        return reinterpret_cast<uint8_t*>(candidate);
    };

    // Keep the entire code and data ranges within the same Page21 window.
    constexpr auto pointer_max = std::numeric_limits<uintptr_t>::max();
    const auto high = code_address <= pointer_max - kPage21Window ? code_address + kPage21Window : pointer_max;
    const auto low = code_end > kPage21Window ? code_end - kPage21Window : static_cast<uintptr_t>(getpagesize());
    if (high - code_end >= capacity) {
        if (auto* data = reserve(code_end)) return data;
    }
    if (code_address >= capacity && code_address - capacity >= low) {
        if (auto* data = reserve(code_address - capacity)) return data;
    }

    // If the adjacent slots are occupied, look for a sufficiently large gap
    // in this window. Verify ownership by reserving it at the exact address.
    const auto find_gap = [&](uintptr_t begin, uintptr_t end) -> uint8_t* {
        if (begin >= end || end - begin < capacity) return nullptr;
        for (auto cursor = begin; cursor <= end - capacity;) {
            mach_vm_address_t mapped = cursor;
            mach_vm_size_t mapped_size = 0;
            vm_region_basic_info_data_64_t info{};
            mach_msg_type_number_t count = VM_REGION_BASIC_INFO_COUNT_64;
            mach_port_t object = MACH_PORT_NULL;
            const auto result = mach_vm_region(task, &mapped, &mapped_size, VM_REGION_BASIC_INFO_64,
                                               reinterpret_cast<vm_region_info_t>(&info), &count, &object);
            if (object != MACH_PORT_NULL) mach_port_deallocate(task, object);
            if (result == KERN_INVALID_ADDRESS) return reserve(cursor);
            if (result != KERN_SUCCESS) return nullptr;
            if (mapped > cursor && mapped - cursor >= capacity) {
                if (auto* data = reserve(cursor)) return data;
            }
            if (mapped >= end || mapped_size > end - mapped) break;
            cursor = mapped + mapped_size;
        }
        return nullptr;
    };
    if (auto* data = find_gap(code_end, high)) return data;
    return find_gap(low, code_address);
}
#endif

}  // namespace

ExecutableArena::Region::Region(std::shared_ptr<ExecutableArena> arena, Pool& pool, size_t offset, size_t size,
                               Lifetime lifetime)
    : arena_(std::move(arena)), pool_(pool), offset_(offset), size_(size), lifetime_(lifetime) {}

ExecutableArena::Region::~Region() {
    if (arena_) arena_->Release(pool_, offset_, size_, lifetime_);
}

ExecutableArena::ExecutableArena(uint8_t* code, uint8_t* data, size_t capacity)
    : code_{code, {{0, capacity}}}, data_{data, {}}, capacity_(capacity) {
    if (data) data_.free_ranges.emplace(0, capacity);
}

ExecutableArena::~ExecutableArena() {
    if (data_.base) munmap(data_.base, capacity_);
    munmap(code_.base, capacity_);
}

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
#if FIBERCPU_LLVM_JIT
    if (capacity > kMaxLLVMCapacity) return {};
#endif
    const auto map_code = [capacity](void* hint) -> uint8_t* {
        void* memory = mmap(hint, capacity, PROT_READ | PROT_WRITE | PROT_EXEC,
                            MAP_PRIVATE | MAP_ANON | MAP_JIT, -1, 0);
        return memory == MAP_FAILED ? nullptr : static_cast<uint8_t*>(memory);
    };
    auto* code = map_code(nullptr);
    if (!code) return {};
    uint8_t* data = nullptr;
#if FIBERCPU_LLVM_JIT
    uintptr_t hint = 0;
    for (unsigned attempt = 0; !(data = ReserveNearbyData(code, capacity)); ++attempt) {
        // A large code map near shared libraries may leave no large enough
        // gap. Try another window while keeping at most one MAP_JIT mapping.
        const auto base = std::max(hint, reinterpret_cast<uintptr_t>(code));
        munmap(code, capacity);
        if (attempt == 3 || base > std::numeric_limits<uintptr_t>::max() - kPage21Window) return {};
        hint = base + kPage21Window;
        code = map_code(reinterpret_cast<void*>(hint));
        if (!code) return {};
    }
#endif
    state.instance = std::shared_ptr<ExecutableArena>(new ExecutableArena(code, data, capacity));
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
    return Reserve(code_, bytes, lifetime);
}

std::unique_ptr<ExecutableArena::Region> ExecutableArena::ReserveData(size_t bytes) {
    return Reserve(data_, bytes, Lifetime::Generation);
}

std::unique_ptr<ExecutableArena::Region> ExecutableArena::Reserve(Pool& pool, size_t bytes, Lifetime lifetime) {
    if (!pool.base || !bytes || bytes > capacity_) return {};
    const size_t page_size = static_cast<size_t>(getpagesize());
    const size_t size = (bytes + page_size - 1) & ~(page_size - 1);
    std::lock_guard lock(mutex_);
    if (size > capacity_ - used_bytes_) return {};
    auto& free_ranges = pool.free_ranges;
    for (auto it = free_ranges.begin(); it != free_ranges.end(); ++it) {
        if (it->second < size) continue;
        const auto [offset, available] = *it;
        free_ranges.erase(it);
        if (available > size) free_ranges.emplace(offset + size, available - size);
        used_bytes_ += size;
        if (lifetime == Lifetime::Permanent) permanent_bytes_ += size;
        return std::unique_ptr<Region>(new Region(shared_from_this(), pool, offset, size, lifetime));
    }
    return {};
}

bool ExecutableArena::FitsFreshGeneration(size_t code_bytes, size_t data_bytes) const {
    std::lock_guard lock(mutex_);
    const size_t available = capacity_ - permanent_bytes_;
    return code_bytes <= available && data_bytes <= available - code_bytes;
}

void ExecutableArena::Release(Pool& pool, size_t offset, size_t size, Lifetime lifetime) {
    if (&pool == &data_) {
        auto* address = pool.base + offset;
        mprotect(address, size, PROT_NONE);
        madvise(address, size, MADV_FREE);
    }
    std::lock_guard lock(mutex_);
    used_bytes_ -= size;
    if (lifetime == Lifetime::Permanent) permanent_bytes_ -= size;
    auto& free_ranges = pool.free_ranges;
    auto next = free_ranges.lower_bound(offset);
    if (next != free_ranges.begin()) {
        const auto prev = std::prev(next);
        if (prev->first + prev->second == offset) {
            offset = prev->first;
            size += prev->second;
            free_ranges.erase(prev);
        }
    }
    if (next != free_ranges.end() && offset + size == next->first) {
        size += next->second;
        free_ranges.erase(next);
    }
    free_ranges.emplace(offset, size);
}

ScopedJitWrite::ScopedJitWrite() { pthread_jit_write_protect_np(0); }

ScopedJitWrite::~ScopedJitWrite() { pthread_jit_write_protect_np(1); }

void FlushJitInstructions(void* address, size_t bytes) { sys_icache_invalidate(address, bytes); }

}  // namespace fiberish
