#pragma once

#include <shared_mutex>

#include "decoder.h"
#include "runtime_options.h"

namespace fiberish {

// The low two bits give the slot count. A branch link slot has no immediate.
inline constexpr unsigned kLLVMBranchLinkSlot = 4 | 2;

enum class LLVMBlockExit : uint8_t { Dispatch, Fallthrough, Branch };

class ScopedLLVMJitExecution {
public:
    explicit ScopedLLVMJitExecution(const EmuState& state);
    ~ScopedLLVMJitExecution();

private:
    std::shared_lock<std::shared_mutex> lock_;
};

bool LLVMJitCacheFlushPending();
uint64_t LLVMJitCacheEpoch();
uint16_t LLVMJitHotThreshold(const EmuState& state);
void CompileHotLLVMBlock(EmuState* state, BasicBlock* block);
void PollLLVMBlockCompilations(EmuState* state);
bool LLVMPageRegionsEnabled(const EmuState& state);
bool LLVMJitTraceEnabled();
void TraceLLVMJitRetirement(const BasicBlock& block);
uint64_t LLVMJitBlockCount();
uint64_t LLVMJitLiveBlockCount();
bool LLVMBlockJitSupported();
bool ConfigureLLVMBlockJit(const SharedJitOptions& settings, std::string& error);
bool LLVMWeightedProfileEnabled(const EmuState& state);
std::shared_ptr<WeightedProfile> CreateLLVMWeightedProfile(bool same_page);
bool WriteWeightedLLVMProfile(WeightedProfile& profile, const char* path, std::string& error);
int BuildLinkedLLVMBlockCache(const char* profile_path, const char* cache_path, uint32_t limit, const char* opt_level,
                              const char* pass_pipeline, const char* region_profile = nullptr);

}  // namespace fiberish
