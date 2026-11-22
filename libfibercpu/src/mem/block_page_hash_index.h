#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <utility>

#include "types.h"

namespace fiberish::mem {

// Guest page directory with a separate offset table for each cached Host backing.
template <typename Key>
class BlockPageHashIndex {
    static constexpr unsigned directory_bits = 10;
    static constexpr unsigned directory_size = 1u << directory_bits;
    static constexpr unsigned initial_capacity = 2;

    struct Backing {
        uintptr_t host_page_base;
        std::unique_ptr<Backing> next;
        // An offset plus one fits in 16 bits. Zero terminates an unsuccessful probe.
        std::unique_ptr<uint16_t[]> tags = std::make_unique<uint16_t[]>(initial_capacity);
        std::unique_ptr<uint32_t[]> indices = std::make_unique<uint32_t[]>(initial_capacity);
        unsigned mask = initial_capacity - 1;
        unsigned count = 0;

        explicit Backing(uintptr_t host_page_base) : host_page_base(host_page_base) {}

        ~Backing() {
            // A heavily remapped Guest page can retain many Host backings.
            while (next) {
                auto successor = std::move(next->next);
                next = std::move(successor);
            }
        }

        unsigned home(uint16_t tag) const {
            const unsigned offset = tag - 1;
            return (offset ^ (offset >> 6)) & mask;
        }

        unsigned probe(uint16_t tag) const {
            for (auto position = home(tag);; position = (position + 1) & mask) {
                const auto current = tags[position];
                if (current == 0 || current == tag) return position;
            }
        }

        const uint32_t* find(uint16_t tag) const {
            const auto position = probe(tag);
            return tags[position] ? &indices[position] : nullptr;
        }

        void grow() {
            const unsigned old_capacity = mask + 1;
            auto enlarged_tags = std::make_unique<uint16_t[]>(old_capacity * 2);
            auto enlarged_indices = std::make_unique<uint32_t[]>(old_capacity * 2);
            auto old_tags = std::exchange(tags, std::move(enlarged_tags));
            auto old_indices = std::exchange(indices, std::move(enlarged_indices));
            mask = old_capacity * 2 - 1;
            for (unsigned position = 0; position < old_capacity; ++position) {
                const auto tag = old_tags[position];
                if (!tag) continue;
                const auto target = probe(tag);
                tags[target] = tag;
                indices[target] = old_indices[position];
            }
        }

        std::pair<uint32_t*, bool> try_emplace(uint16_t tag, uint32_t index) {
            auto position = probe(tag);
            if (tags[position]) return {&indices[position], false};

            // Keep the load at or below 75% and leave empty slots to stop a miss.
            if ((count + 1) * 4 > (mask + 1) * 3) {
                grow();
                position = probe(tag);
            }
            tags[position] = tag;
            indices[position] = index;
            ++count;
            return {&indices[position], true};
        }

        bool erase(uint16_t tag) {
            auto hole = probe(tag);
            if (!tags[hole]) return false;

            // Shift any later slot whose cyclic probe interval crosses the hole.
            for (auto position = (hole + 1) & mask; tags[position]; position = (position + 1) & mask) {
                const auto start = home(tags[position]);
                if (((hole - start) & mask) < ((position - start) & mask)) {
                    tags[hole] = tags[position];
                    indices[hole] = indices[position];
                    hole = position;
                }
            }
            tags[hole] = 0;
            indices[hole] = 0;
            --count;
            return true;
        }
    };

    struct Level2 {
        std::array<std::unique_ptr<Backing>, directory_size> pages{};
        unsigned count = 0;
    };

    std::array<std::unique_ptr<Level2>, directory_size> root_{};

    static unsigned root_slot(uint32_t eip) { return eip >> (PAGE_SHIFT + directory_bits); }
    static unsigned page_slot(uint32_t eip) { return (eip >> PAGE_SHIFT) & (directory_size - 1); }
    static uint16_t offset_tag(uint32_t eip) { return static_cast<uint16_t>((eip & PAGE_MASK) + 1); }

    static std::unique_ptr<Backing>& backing_link(std::unique_ptr<Backing>& page, uintptr_t host_page_base) {
        auto* link = &page;
        while (*link && (*link)->host_page_base != host_page_base) link = &(*link)->next;
        return *link;
    }

public:
    const uint32_t* find(const Key& key) const {
        const auto* level = root_[root_slot(key.guest_eip)].get();
        if (!level) return nullptr;
        const auto* backing = level->pages[page_slot(key.guest_eip)].get();
        while (backing && backing->host_page_base != key.host_page_base) backing = backing->next.get();
        return backing ? backing->find(offset_tag(key.guest_eip)) : nullptr;
    }

    uint32_t* find(const Key& key) { return const_cast<uint32_t*>(std::as_const(*this).find(key)); }

    std::pair<uint32_t*, bool> try_emplace(const Key& key, uint32_t index) {
        auto& level = root_[root_slot(key.guest_eip)];
        const auto page_index = page_slot(key.guest_eip);
        if (!level) {
            auto new_level = std::make_unique<Level2>();
            new_level->pages[page_index] = std::make_unique<Backing>(key.host_page_base);
            new_level->count = 1;
            level = std::move(new_level);
            return level->pages[page_index]->try_emplace(offset_tag(key.guest_eip), index);
        }

        auto& page = level->pages[page_index];
        auto& link = backing_link(page, key.host_page_base);
        if (!link) {
            const bool first_backing = !page;
            link = std::make_unique<Backing>(key.host_page_base);
            if (first_backing) ++level->count;
        }
        return link->try_emplace(offset_tag(key.guest_eip), index);
    }

    void erase(const Key& key) {
        auto& level = root_[root_slot(key.guest_eip)];
        if (!level) return;
        auto& page = level->pages[page_slot(key.guest_eip)];
        auto& link = backing_link(page, key.host_page_base);
        if (!link || !link->erase(offset_tag(key.guest_eip)) || link->count) return;

        auto successor = std::move(link->next);
        link = std::move(successor);
        if (!page && --level->count == 0) level.reset();
    }

    void clear() { root_ = {}; }
};

}  // namespace fiberish::mem
