// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
void Helper_Group2(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count, bool is_byte);
void RegisterShiftBitOps();
}  // namespace x86emu