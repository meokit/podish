#include "superopcodes.h"

namespace fiberish {

#if !FIBERCPU_HAVE_GENERATED_SUPEROPCODES
HandlerFunc FindSuperOpcode(const DecodedOp* ops) {
    (void)ops;
    return nullptr;
}
#endif

void ApplySuperOpcodesToBlockOps(DecodedOp* ops, uint32_t op_count) {
#if !FIBERCPU_ENABLE_SUPEROPCODES
    (void)ops;
    (void)op_count;
    return;
#else
    if (!ops || op_count < 2) return;

    for (uint32_t i = 0; i + 1 < op_count; ++i) {
        if (HandlerFunc superopcode = FindSuperOpcode(&ops[i])) {
            ops[i].handler = superopcode;
        }
    }
#endif
}

}  // namespace fiberish
