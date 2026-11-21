#pragma once

#include "jit/llvm_block_jit.h"

namespace fiberish {

bool LinkedBlockCacheEnabled();
bool LinkedPageRegionsEnabled();
bool TryLinkedBlockCache(EmuState& state, BasicBlock& block);
uint64_t LinkedBlockHitCount();
uint64_t LinkedPageRegionHitCount();

}  // namespace fiberish
