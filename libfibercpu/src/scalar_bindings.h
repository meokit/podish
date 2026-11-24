#pragma once

#include "dispatch.h"
#include "scalar_partial_config.h"

namespace fiberish {

struct ScalarBindingEntry {
    uint16_t key;
    HandlerFunc handler;
};

struct ExactScalarBinding {
    DecodedOperandShape shape;
    HandlerFunc handler;
};

#if FIBERCPU_HAVE_SCALAR_PARTIALS
struct PartialScalarBinding {
    struct Key {
        DecodedOperandShape shape;
        uint8_t slots;
        auto operator<=>(const Key&) const = default;
    };
    Key key;
    HandlerFunc handler;
};

using PartialScalarBindings = std::array<std::span<const PartialScalarBinding>, 3>;
#endif

struct ScalarBindingTable {
    const ScalarBindingEntry* entries;
    size_t count;
    uint8_t memory_mask;
    uint8_t policies;
    std::array<HandlerFunc, 2> fallback;
    std::span<const ExactScalarBinding> exact;
#if FIBERCPU_HAVE_SCALAR_PARTIALS
    const PartialScalarBindings* partial;
#endif
};

enum ScalarBindingPolicy : uint8_t {
    kBindWidth = 1,
    kBindFlags = 2,
    kRequireWidth = 4,
};

// Keep the bit positions in sync with scalar_policy_key in scalar_modrm.py.
inline constexpr uint16_t kUnboundModRMKey = 1u << 14;

template <LogicFunc Target, uint16_t Key, unsigned Keep>
constexpr ScalarBindingEntry MakeScalarBinding() {
    using Policy = OperandSpec::WithModRM<OperandSpec::SlotBound<((Key >> 8) & 3)>, uint8_t(Key), Keep>;
    static_assert(OperandSpec::FixedModRM<Policy>);
    return {Key, DispatchWrapper<Target, Policy>};
}

}  // namespace fiberish
