#pragma once

#include "jit/llvm_block_jit.h"

namespace fiberish {

bool LinkedBlockCacheEnabled();
bool TryLinkedBlockCache(EmuState& state, BasicBlock& block);
uint64_t LinkedBlockHitCount();

}  // namespace fiberish
