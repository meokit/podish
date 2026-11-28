#pragma once

#include <cstddef>
#include <cstdint>

namespace fiberish {

struct BasicBlock;
struct EmuState;
namespace mem {
struct CodeCacheState;
}

bool WasmJitEnabled();
void SetWasmJitEnabled(bool enabled);
void SetWasmJitHotThreshold(uint16_t threshold);
void SetWasmJitPoolSize(uint32_t pool_size);
void SetWasmJitStatsEnabled(bool enabled);

uint16_t WasmJitHotThreshold(const EmuState& state);
void CompileHotWasmBlock(EmuState* state, BasicBlock* block);
void WasmJitOnBlockInvalidate(BasicBlock& block);
void WasmJitRetireCodeCache(const mem::CodeCacheState& cache);
void WasmJitRecordExecutedInsts(uint64_t insts);
void WasmJitResetPool();
void WasmJitResetStats();
int WasmJitDumpStats(char* buffer, size_t buffer_size);

}  // namespace fiberish
