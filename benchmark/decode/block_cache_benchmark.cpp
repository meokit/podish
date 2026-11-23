#include "mem/mmu.h"

#include <algorithm>
#include <bit>
#include <charconv>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <numeric>
#include <random>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace {
using fiberish::mem::BlockCacheKey;
using fiberish::mem::BlockCacheKeyHash;
using BlockEntries = std::vector<fiberish::mem::CodeCacheState::BlockCacheEntry>;
constexpr uint32_t missing = std::numeric_limits<uint32_t>::max();
constexpr size_t query_count = 65536;

struct RotateMultiply {
    using is_avalanching = void;

    uint64_t operator()(const BlockCacheKey& key) const noexcept {
        // Separate the low page bits of the host and guest before combining.
        const auto combined = std::rotl(uint64_t(key.host_page_base), 29) ^ key.guest_eip;
        const auto product = combined * 0x9e3779b97f4a7c15ULL;
        return product ^ (product >> 32);
    }
};

struct FoldedMultiply {
    using is_avalanching = void;

    uint64_t operator()(const BlockCacheKey& key) const noexcept {
        const auto combined = uint64_t(key.host_page_base) ^ (uint64_t(key.guest_eip) << 32);
        return ankerl::unordered_dense::hash<uint64_t>{}(combined);
    }
};

template <typename Map>
class MapIndex {
    Map map_;

public:
    explicit MapIndex(size_t count) {
        map_.reserve(count);
    }
    void insert(BlockCacheKey key, uint32_t index) {
        map_.try_emplace(key, index);
    }
    void erase(BlockCacheKey key) {
        map_.erase(key);
    }

    uint32_t lookup(BlockCacheKey key) const {
        auto it = map_.find(key);
        return it == map_.end() ? missing : it->second;
    }
};

// Each bucket holds both complete key fields and the block entry index.
template <typename Hash>
class FlatIndex {
    struct Bucket {
        uintptr_t host_page_base = 0;
        uint32_t guest_eip = 0;
        uint32_t index = missing;

        bool matches(BlockCacheKey key) const {
            return host_page_base == key.host_page_base && guest_eip == key.guest_eip;
        }
    };
    std::vector<Bucket> buckets_;
    size_t mask_;
    unsigned shift_;

    size_t home(BlockCacheKey key) const {
        return uint64_t(Hash{}(key)) >> shift_;
    }
    size_t find_slot(BlockCacheKey key) const {
        size_t slot = home(key);
        while (buckets_[slot].index != missing && !buckets_[slot].matches(key))
            slot = (slot + 1) & mask_;
        return slot;
    }

public:
    explicit FlatIndex(size_t count)
        : buckets_(std::bit_ceil(count + (count + 2) / 3))
        , mask_(buckets_.size() - 1)
        , shift_(64 - std::countr_zero(buckets_.size())) {}

    uint32_t lookup(BlockCacheKey key) const {
        return buckets_[find_slot(key)].index;
    }
    void insert(BlockCacheKey key, uint32_t index) {
        auto& bucket = buckets_[find_slot(key)];
        if (bucket.index == missing)
            bucket = {key.host_page_base, key.guest_eip, index};
    }

    void erase(BlockCacheKey key) {
        size_t hole = find_slot(key);
        if (buckets_[hole].index == missing)
            return;
        for (size_t slot = (hole + 1) & mask_; buckets_[slot].index != missing; slot = (slot + 1) & mask_) {
            auto& bucket = buckets_[slot];
            const size_t start = home({bucket.host_page_base, bucket.guest_eip});
            // Move entries whose probe sequence would cross the hole.
            if (((hole - start) & mask_) < ((slot - start) & mask_)) {
                buckets_[hole] = bucket;
                hole = slot;
            }
        }
        buckets_[hole].index = missing;
    }
};

struct Workload {
    std::vector<BlockCacheKey> keys;
    std::vector<BlockCacheKey> misses;
    std::vector<uint32_t> query_indices;
    std::vector<uint32_t> chain_order;
    std::vector<fiberish::BasicBlock> blocks;
    BlockEntries entries;

    Workload(uint32_t count, std::string_view layout)
        : chain_order(count)
        , blocks(count)
        , entries(count) {
        std::mt19937 random(0xc091face);
        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t page = i / 16;
            const uint32_t offset = (i % 16) * 19;
            uint32_t host_page = layout == "aliased" ? page / 2 : page;
            if (layout == "scattered")
                host_page = (host_page * 2654435761u) & 0xffffu;
            const uint32_t guest_page = layout == "remapped" ? page / 4 : page;
            const auto host = static_cast<uintptr_t>(0x106350000ULL + uint64_t(host_page) * 4096);
            const uint32_t eip = 0x8048000 + guest_page * 4096 + offset;
            keys.push_back({host, eip});
            misses.push_back(i % 2 ? BlockCacheKey{host, eip ^ 0x20000000} : BlockCacheKey{host + 0x20000000, eip});
            entries[i].block = &blocks[i];
        }
        for (size_t i = 0; i < query_count; ++i)
            query_indices.push_back(random() % count);
        std::iota(chain_order.begin(), chain_order.end(), 0);
        std::shuffle(chain_order.begin(), chain_order.end(), random);
        // Store the next query index in the synthetic block header.
        for (uint32_t i = 0; i < count; ++i) {
            blocks[chain_order[i]].set_start_eip(chain_order[(i + 1) % count]);
        }
    }
};

template <typename Function>
double time_per_operation(size_t operations, Function function) {
    const auto begin = std::chrono::steady_clock::now();
    function();
    return std::chrono::duration<double, std::nano>(std::chrono::steady_clock::now() - begin).count() / operations;
}

template <typename Index>
void benchmark(std::string_view name, const Workload& work, std::string_view layout, size_t rounds) {
    const auto count = work.keys.size();
    const auto report = [&](std::string_view operation, double ns) {
        std::printf("%.*s,%.*s,%zu,%.*s,%.3f\n", int(name.size()), name.data(), int(layout.size()), layout.data(),
                    count, int(operation.size()), operation.data(), ns);
    };
    Index index(count);
    const double insert_ns = time_per_operation(count, [&] {
        for (uint32_t i = 0; i < count; ++i)
            index.insert(work.keys[i], i);
    });
    for (uint32_t i = 0; i < count; ++i) {
        if (index.lookup(work.keys[i]) != i || index.lookup(work.misses[i]) != missing)
            std::abort();
    }

    for (std::string_view query : {"hit", "mixed", "miss"}) {
        std::vector<BlockCacheKey> keys;
        keys.reserve(work.query_indices.size());
        uint64_t expected = 0;
        for (uint32_t i : work.query_indices) {
            const bool hit = query == "hit" || (query == "mixed" && keys.size() % 8 != 0);
            keys.push_back(hit ? work.keys[i] : work.misses[i]);
            if (hit)
                expected += reinterpret_cast<uintptr_t>(work.entries[i].block);
        }
        uint64_t checksum = 0;
        const double ns = time_per_operation(keys.size() * rounds, [&] {
            for (size_t round = 0; round < rounds; ++round) {
                // Shift the starting point so each pass has a different order.
                for (size_t j = 0; j < keys.size(); ++j) {
                    const uint32_t slot = index.lookup(keys[(j + round) & (keys.size() - 1)]);
                    if (slot != missing)
                        checksum += reinterpret_cast<uintptr_t>(work.entries[slot].block);
                }
            }
        });
        if (checksum != expected * rounds)
            std::abort();
        report(query, ns);
    }

    uint32_t cursor = work.chain_order.front();
    const auto dependent_lookups = work.query_indices.size() * rounds;
    const double dependent_ns = time_per_operation(dependent_lookups, [&] {
        for (size_t i = 0; i < dependent_lookups; ++i) {
            const auto slot = index.lookup(work.keys[cursor]);
            if (slot == missing)
                std::abort();
            cursor = work.entries[slot].block->start_eip();
        }
    });
    if (cursor != work.chain_order[dependent_lookups % count])
        std::abort();
    report("dependent_hit", dependent_ns);

    const double churn_ns = time_per_operation(work.query_indices.size() * 2, [&] {
        for (auto i : work.query_indices) {
            index.erase(work.keys[i]);
            index.insert(work.misses[i], i);
            index.erase(work.misses[i]);
            index.insert(work.keys[i], i);
        }
    });
    for (uint32_t i = 0; i < count; ++i) {
        if (index.lookup(work.keys[i]) != i || index.lookup(work.misses[i]) != missing)
            std::abort();
    }
    report("insert", insert_ns);
    report("erase_insert", churn_ns);
}

template <typename Hash>
using DenseIndex = MapIndex<ankerl::unordered_dense::map<BlockCacheKey, uint32_t, Hash>>;
} // namespace

int main(int argc, char** argv) {
    size_t rounds = 30;
    if (argc > 1) {
        const std::string_view count(argv[1]);
        const auto end = count.data() + count.size();
        const auto [next, error] = std::from_chars(count.data(), end, rounds);
        if (argc > 2 || error != std::errc{} || next != end || !rounds ||
            rounds > std::numeric_limits<size_t>::max() / query_count) {
            std::fprintf(stderr, "Usage: block_cache_benchmark [passes per lookup measurement]\n");
            return 1;
        }
    }
    std::puts("implementation,keys,entries,operation,ns");
    for (std::string_view layout : {"linear", "aliased", "remapped", "scattered"}) {
        for (uint32_t count : {256, 3000, 48000}) {
            const Workload work(count, layout);
            benchmark<DenseIndex<BlockCacheKeyHash>>("dense_current", work, layout, rounds);
            benchmark<DenseIndex<RotateMultiply>>("dense_rotate", work, layout, rounds);
            benchmark<DenseIndex<FoldedMultiply>>("dense_folded", work, layout, rounds);
            benchmark<FlatIndex<RotateMultiply>>("flat_rotate", work, layout, rounds);
            benchmark<MapIndex<std::unordered_map<BlockCacheKey, uint32_t, RotateMultiply>>>("std_rotate", work, layout,
                                                                                             rounds);
        }
    }
}
