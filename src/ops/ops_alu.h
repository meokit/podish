// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpAdd_EbGb(EmuState* state, DecodedOp* op);

void OpAdd_EvGv(EmuState* state, DecodedOp* op);

void OpAdd_GbEb(EmuState* state, DecodedOp* op);

void OpAdd_GvEv(EmuState* state, DecodedOp* op);

void OpAdc_EbGb(EmuState* state, DecodedOp* op);

void OpAdc_EvGv(EmuState* state, DecodedOp* op);

void OpAdc_GbEb(EmuState* state, DecodedOp* op);

void OpAdc_GvEv(EmuState* state, DecodedOp* op);

void OpSub_EvGv(EmuState* state, DecodedOp* op);

void OpAnd_EbGb(EmuState* state, DecodedOp* op);

void OpAnd_EvGv(EmuState* state, DecodedOp* op);

void OpAnd_GbEb(EmuState* state, DecodedOp* op);

void OpAnd_GvEv(EmuState* state, DecodedOp* op);

void OpOr_EvGv(EmuState* state, DecodedOp* op);

void OpXor_EvGv(EmuState* state, DecodedOp* op);

void OpInc_Reg(EmuState* state, DecodedOp* op);

void OpDec_Reg(EmuState* state, DecodedOp* op);

} // namespace x86emu
