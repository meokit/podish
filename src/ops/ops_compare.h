// Comparison & Test
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpCmp_EbGb(EmuState* state, DecodedOp* op);

void OpCmp_EvGv(EmuState* state, DecodedOp* op);

void OpCmp_GbEb(EmuState* state, DecodedOp* op);

void OpCmp_GvEv(EmuState* state, DecodedOp* op);

void OpTest_EvGv(EmuState* state, DecodedOp* op);

} // namespace x86emu
