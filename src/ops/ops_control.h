// Control Flow
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpJmp_Rel(EmuState* state, DecodedOp* op);

void OpJcc_Rel(EmuState* state, DecodedOp* op);

void OpCall_Rel(EmuState* state, DecodedOp* op);

void OpRet(EmuState* state, DecodedOp* op);

void OpInt(EmuState* state, DecodedOp* op);

void OpInt3(EmuState* state, DecodedOp* op);

void OpHlt(EmuState* state, DecodedOp* op);

void OpNop(EmuState* state, DecodedOp* op);

void OpCmov_GvEv(EmuState* state, DecodedOp* op);

} // namespace x86emu
