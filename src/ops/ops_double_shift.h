// Double-Shift Instructions
// SHLD/SHRD operation declarations

#pragma once

#include "../common.h"
#include "../decoder.h"
#include "../state.h"

namespace x86emu {

// SHLD - Double Precision Shift Left
void OpShld_EvGvIb(EmuState* state, DecodedOp* op);  // 0F A4
void OpShld_EvGvCl(EmuState* state, DecodedOp* op);  // 0F A5

// SHRD - Double Precision Shift Right  
void OpShrd_EvGvIb(EmuState* state, DecodedOp* op);  // 0F AC
void OpShrd_EvGvCl(EmuState* state, DecodedOp* op);  // 0F AD

} // namespace x86emu
