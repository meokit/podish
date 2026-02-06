// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {
void OpUd2(EmuState* state, DecodedOp* op);
void OpUd2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
void RegisterGroupOps();
}  // namespace x86emu