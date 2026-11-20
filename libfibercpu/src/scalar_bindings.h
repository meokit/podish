#pragma once

#include "dispatch.h"

namespace fiberish {

struct ScalarBindingEntry {
    uint16_t key;
    HandlerFunc handler;
};

struct ScalarBindingTable {
    const ScalarBindingEntry* entries;
    size_t count;
    uint8_t memory_mask;
    std::array<HandlerFunc, 2> fallback;
};

template <LogicFunc Target, uint16_t Key, unsigned Keep>
constexpr ScalarBindingEntry MakeScalarBinding() {
    using Policy = OperandSpec::WithModRM<OperandSpec::SlotBound<(Key >> 8)>, uint8_t(Key), Keep>;
    static_assert(OperandSpec::FixedModRM<Policy>);
    return {Key, DispatchWrapper<Target, Policy>};
}

}  // namespace fiberish
