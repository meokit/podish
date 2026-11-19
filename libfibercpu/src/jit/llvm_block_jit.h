#pragma once

#include <shared_mutex>

#include "decoder.h"

namespace fiberish {

enum class LLVMBlockExit : uint8_t { Dispatch, Fallthrough, Branch };

class ScopedLLVMJitExecution {
public:
    ScopedLLVMJitExecution();
    ~ScopedLLVMJitExecution();

private:
    std::shared_lock<std::shared_mutex> lock_;
};

bool LLVMJitCacheFlushPending();
uint64_t LLVMJitCacheEpoch();
uint16_t LLVMJitHotThreshold();
void CompileHotLLVMBlock(EmuState* state, BasicBlock* block);
void PollLLVMBlockCompilations(EmuState* state);
uint64_t LLVMJitBlockCount();
uint64_t LLVMJitLiveBlockCount();
int BuildLinkedLLVMBlockCache(const char* profile_path, const char* cache_path);

}  // namespace fiberish
