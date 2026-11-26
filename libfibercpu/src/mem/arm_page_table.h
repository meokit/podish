#pragma once

#include <algorithm>
#include <array>
#include <memory>
#include <utility>

#include "types.h"

namespace fiberish::mem {

// 4 KiB pages over the 39-bit EL0 address range. Allocate lower tables only
// for populated subranges, including when reserving a large PROT_NONE VMA.
class ArmPageTable {
public:
    struct Entry {
        HostAddr page = nullptr;
        Property permissions = Property::None;
    };

private:
    static constexpr unsigned IndexBits = 9;
    static constexpr unsigned Entries = 1u << IndexBits;
    static constexpr unsigned MiddleShift = PAGE_SHIFT + IndexBits;
    static constexpr unsigned RootShift = MiddleShift + IndexBits;
    static_assert(ArmTaskSize == (uint64_t{1} << (RootShift + IndexBits)));

    // The i386 range occupies the first four complete root entries.
    static constexpr unsigned CompatRootEntries = unsigned((uint64_t{1} << 32) >> RootShift);
    using Leaf = std::array<Entry, Entries>;
    using Middle = std::array<std::unique_ptr<Leaf>, Entries>;
    using Root = std::array<std::unique_ptr<Middle>, Entries>;
    Root root_{};

    static constexpr unsigned Index(uint64_t address, unsigned shift) {
        return static_cast<unsigned>(address >> shift) & (Entries - 1);
    }

public:
    ArmPageTable() = default;

    ArmPageTable(const ArmPageTable& source) {
        for (unsigned i = 0; i < Entries; ++i) {
            if (!source.root_[i]) continue;
            const auto& src = *source.root_[i];
            auto& dest = *(root_[i] = std::make_unique<Middle>());
            for (unsigned j = 0; j < Entries; ++j)
                if (src[j]) dest[j] = std::make_unique<Leaf>(*src[j]);
        }
    }

    void ReplaceCompatMappings(ArmPageTable& source) {
        for (unsigned i = 0; i < CompatRootEntries; ++i) root_[i] = std::move(source.root_[i]);
    }

    void DropCompatMappings() {
        for (unsigned i = 0; i < CompatRootEntries; ++i) root_[i].reset();
    }

    Entry* Find(uint64_t address) {
        if (address >= ArmTaskSize) return nullptr;
        auto& middle = root_[Index(address, RootShift)];
        if (!middle) return nullptr;
        auto& leaf = (*middle)[Index(address, MiddleShift)];
        return leaf ? &(*leaf)[Index(address, PAGE_SHIFT)] : nullptr;
    }

    Entry& Ensure(uint64_t address) {
        auto& middle = root_[Index(address, RootShift)];
        if (!middle) middle = std::make_unique<Middle>();
        auto& leaf = (*middle)[Index(address, MiddleShift)];
        if (!leaf) leaf = std::make_unique<Leaf>();
        return (*leaf)[Index(address, PAGE_SHIFT)];
    }

    template <class Visitor>
    void VisitMapped(uint64_t addr, uint64_t size, Visitor visitor, bool prune = false) {
        if (!size || addr >= ArmTaskSize) return;

        const uint64_t start = addr & ~uint64_t{PAGE_MASK};
        const uint64_t end = (addr + std::min(size, ArmTaskSize - addr) + PAGE_MASK) & ~uint64_t{PAGE_MASK};
        for (auto current = start; current < end;) {
            auto& middle = root_[Index(current, RootShift)];
            const auto middle_end = std::min(end, ((current >> RootShift) + 1) << RootShift);
            if (!middle) {
                current = middle_end;
                continue;
            }
            while (current < middle_end) {
                auto& leaf = (*middle)[Index(current, MiddleShift)];
                const auto leaf_end = std::min(middle_end, ((current >> MiddleShift) + 1) << MiddleShift);
                if (!leaf) {
                    current = leaf_end;
                    continue;
                }
                for (; current < leaf_end; current += PAGE_SIZE) {
                    auto& entry = (*leaf)[Index(current, PAGE_SHIFT)];
                    if (entry.page && !visitor(current, entry)) return;
                }
                if (prune && std::none_of(leaf->begin(), leaf->end(), [](const Entry& e) { return e.page; }))
                    leaf.reset();
            }
            if (prune && std::none_of(middle->begin(), middle->end(), [](const auto& leaf) { return !!leaf; }))
                middle.reset();
        }
    }
};

}  // namespace fiberish::mem
