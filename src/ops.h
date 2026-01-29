#pragma once

#include "common.h"
#include "decoder.h"
#include "state.h"

namespace x86emu {

// Handlers
void OpNop(EmuState* state, DecodedOp* op);
void OpNotImplemented(EmuState* state, DecodedOp* op);

// Basic Instruction Handlers
void OpMov_EvGv(EmuState* state, DecodedOp* op);
void OpMov_GvEv(EmuState* state, DecodedOp* op);
void OpLea(EmuState* state, DecodedOp* op);
void OpPush_Reg(EmuState* state, DecodedOp* op);
void OpPush_Imm(EmuState* state, DecodedOp* op);
void OpPop_Reg(EmuState* state, DecodedOp* op);
void OpHlt(EmuState* state, DecodedOp* op);

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];

} // namespace x86emu
