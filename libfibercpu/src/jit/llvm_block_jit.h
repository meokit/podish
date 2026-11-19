#pragma once

#include "decoder.h"

namespace fiberish {

enum class LLVMBlockExit : uint8_t { Dispatch, Fallthrough, Branch };

uint16_t LLVMJitHotThreshold();
void CompileHotLLVMBlock(EmuState* state, BasicBlock* block);
void PollLLVMBlockCompilations(EmuState* state);
uint64_t LLVMJitBlockCount();
uint64_t LLVMJitLiveBlockCount();
uint64_t LLVMJitCacheLoadCount();
int BuildLLVMJitCache(const char* profile_path, const char* cache_path, bool linked = false);

}  // namespace fiberish
