#pragma once
#include <cstdint>
#include <vector>
#include "decoder.h"

namespace fiberish {
void RegisterJitBlocks();
HandlerFunc FindJitBlock(DecodedOp* ops);
}  // namespace fiberish
