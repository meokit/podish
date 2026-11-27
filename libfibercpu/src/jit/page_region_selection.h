#pragma once

#include <algorithm>
#include <span>

#include <llvm/ADT/SmallVector.h>

#include "mem/page_region.h"

namespace fiberish {

inline constexpr size_t kMaxRegionInstructions = 192;

// Candidates are sorted by guest address and exclude blocks covered by installed regions.
template <typename Candidate, typename Block, typename HasEdge, typename AddInstructions>
auto SelectPageRegion(std::span<const Candidate> available, const Block& root, HasEdge has_edge,
                      AddInstructions add_instructions) {
    constexpr auto max_members = mem::PageRegion<Block>::MaxMembers;
    llvm::SmallVector<const Candidate*, max_members> selected;
    const auto entry = std::ranges::find(available, &root, &Candidate::block);
    if (entry == available.end()) return selected;

    auto instructions = add_instructions(*entry, {});
    if (instructions.count() > kMaxRegionInstructions) return selected;
    selected.push_back(&*entry);
    for (size_t i = 0; i < selected.size() && selected.size() < max_members; ++i) {
        const auto& source = *selected[i];
        for (const auto& candidate : available) {
            if (std::ranges::find(selected, &candidate) != selected.end()) continue;
            if (!has_edge(source, candidate) && !has_edge(candidate, source)) continue;
            const auto extended = add_instructions(candidate, instructions);
            if (extended.count() > kMaxRegionInstructions) continue;
            selected.push_back(&candidate);
            instructions = extended;
            if (selected.size() == max_members) break;
        }
    }
    if (selected.size() < 2) selected.clear();
    std::ranges::sort(selected);
    return selected;
}

}  // namespace fiberish
