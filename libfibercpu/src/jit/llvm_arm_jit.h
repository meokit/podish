#pragma once

#include "aarch64/aarch64.h"

namespace fiberish {
struct EmuState;
}

namespace fiberish::aarch64::detail {
extern "C" FIBERCPU_AARCH64_HANDLER_ABI int64_t FiberArmJitRetry(A64_HANDLER_PARAMS);
extern "C" FIBERCPU_AARCH64_HANDLER_ABI int64_t FiberArmJitRefetch(A64_HANDLER_PARAMS);
extern "C" FIBERCPU_AARCH64_HANDLER_ABI int64_t FiberArmJitLeave(A64_HANDLER_PARAMS);
}  // namespace fiberish::aarch64::detail

namespace fiberish {
void InitializeLLVMArmBlock(EmuState& state, aarch64::detail::BasicBlock& block);
void PollLLVMArmCompilations(EmuState& state);
void ObserveLLVMArmHotBlock(EmuState& state, aarch64::detail::BasicBlock& block);
}  // namespace fiberish
