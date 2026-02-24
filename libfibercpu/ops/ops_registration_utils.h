#pragma once
#include "../dispatch.h"
#include "../specialization.h"

namespace fiberish {

// Helper to register SSE ops (0x66 prefix)
template <LogicFunc Target>
inline void RegisterSseOp(int idx) {
    SpecCriteria criteria;
    criteria.prefix_mask = prefix::OPSIZE;
    criteria.prefix_val = prefix::OPSIZE;

    RegisterSpecializedHandler(idx, criteria, (HandlerFunc)DispatchWrapper<Target>);
}

}  // namespace fiberish
