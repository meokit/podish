#pragma once

#include "decoder.h"

namespace fiberish {

enum class LLVMBlockExit : uint8_t { Dispatch, Fallthrough, Branch };

uint16_t LLVMJitHotThreshold();
void CompileHotLLVMBlock(EmuState* state, BasicBlock* block);
uint64_t LLVMJitBlockCount();
uint64_t LLVMJitLiveBlockCount();

}  // namespace fiberish
