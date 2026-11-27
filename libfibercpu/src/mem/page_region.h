#pragma once

#include <cstddef>
#include <cstdint>

namespace fiberish::mem {

// Region wrappers pass the member ordinal in the low bits of the table address.
template <typename BlockT, size_t Capacity = 12>
struct alignas(16) PageRegion {
    static constexpr size_t MaxMembers = Capacity;
    static constexpr size_t MemberMask = 15;
    static_assert(MaxMembers <= MemberMask + 1);

    BlockT* members[MaxMembers]{};
    size_t count = 0;
    uint64_t guest_page = 0;
};

}  // namespace fiberish::mem
