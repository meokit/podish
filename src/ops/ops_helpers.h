// Shared Helper Functions
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

uint8_t GetReg8(EmuState* state, uint8_t reg_idx);

uint32_t ReadModRM(EmuState* state, DecodedOp* op, bool is_byte);

} // namespace x86emu
