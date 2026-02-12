#include <iostream>
#include "jit_ops.h"

namespace fiberish {
__attribute__((weak)) HandlerFunc FindJitBlock(const std::vector<void*>& handlers) { return nullptr; }

}  // namespace fiberish