// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpMov_Sse_Load(EmuState* state, DecodedOp* op);

void OpMov_Sse_Store(EmuState* state, DecodedOp* op);

void OpMovd_Load(EmuState* state, DecodedOp* op);

void OpMovd_Store(EmuState* state, DecodedOp* op);

void OpMovq_Load(EmuState* state, DecodedOp* op);

void OpMovq_Store(EmuState* state, DecodedOp* op);

} // namespace x86emu
