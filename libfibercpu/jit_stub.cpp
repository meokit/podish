#include <iostream>
#include "jit_ops.h"

namespace fiberish {
__attribute__((weak)) HandlerFunc FindJitBlock(const DecodedOp* op) { return nullptr; }

}  // namespace fiberish