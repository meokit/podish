// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpGroup1_EbIb(EmuState* state, DecodedOp* op);

void OpGroup1_EvIz(EmuState* state, DecodedOp* op);

void OpGroup3_Ev(EmuState* state, DecodedOp* op);

void OpGroup4_Eb(EmuState* state, DecodedOp* op);

void OpGroup5_Ev(EmuState* state, DecodedOp* op);

void OpGroup9(EmuState* state, DecodedOp* op);

void OpXadd_Rm_R(EmuState* state, DecodedOp* op);

void OpCdq(EmuState* state, DecodedOp* op);

void OpCwde(EmuState* state, DecodedOp* op);

void OpUd2(EmuState* state, DecodedOp* op);

void OpDecodeFault(EmuState* state, DecodedOp* op);

} // namespace x86emu
