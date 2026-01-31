// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void Helper_Group2(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count, bool is_byte);

void OpGroup2_EvIb(EmuState* state, DecodedOp* op);

void OpGroup2_Ev1(EmuState* state, DecodedOp* op);

void OpGroup2_EvCl(EmuState* state, DecodedOp* op);

void OpBt_EvGv(EmuState* state, DecodedOp* op);

void OpGroup8_EvIb(EmuState* state, DecodedOp* op);

void OpBtr_EvGv(EmuState* state, DecodedOp* op);
void OpBts_EvGv(EmuState* state, DecodedOp* op);
void OpBtc_EvGv(EmuState* state, DecodedOp* op);

void OpBsr_GvEv(EmuState* state, DecodedOp* op);

void OpBsf_Tzcnt_GvEv(EmuState* state, DecodedOp* op);

void OpBswap_Reg(EmuState* state, DecodedOp* op);

} // namespace x86emu
