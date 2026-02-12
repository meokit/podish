#pragma once
#include <cstdint>
#include <vector>
#include "decoder.h"

namespace fiberish {
// 默认实现为 nop，防止链接错误
void RegisterJitBlocks();
HandlerFunc FindJitBlock(const std::vector<void*>& handlers);
}  // namespace fiberish
