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

// Missing ALU Declarations
void OpAdd_AlImm(EmuState* state, DecodedOp* op);
void OpAdd_EaxImm(EmuState* state, DecodedOp* op);

void OpOr_EbGb(EmuState* state, DecodedOp* op);
void OpOr_GbEb(EmuState* state, DecodedOp* op);
void OpOr_GvEv(EmuState* state, DecodedOp* op);
void OpOr_AlImm(EmuState* state, DecodedOp* op);
void OpOr_EaxImm(EmuState* state, DecodedOp* op);

void OpAdc_AlImm(EmuState* state, DecodedOp* op);
void OpAdc_EaxImm(EmuState* state, DecodedOp* op);

void OpSbb_EbGb(EmuState* state, DecodedOp* op);
void OpSbb_EvGv(EmuState* state, DecodedOp* op);
void OpSbb_GbEb(EmuState* state, DecodedOp* op);
void OpSbb_GvEv(EmuState* state, DecodedOp* op);
void OpSbb_AlImm(EmuState* state, DecodedOp* op);
void OpSbb_EaxImm(EmuState* state, DecodedOp* op);

void OpAnd_AlImm(EmuState* state, DecodedOp* op);
void OpAnd_EaxImm(EmuState* state, DecodedOp* op);

void OpSub_EbGb(EmuState* state, DecodedOp* op);
void OpSub_GbEb(EmuState* state, DecodedOp* op);
void OpSub_GvEv(EmuState* state, DecodedOp* op);
void OpSub_AlImm(EmuState* state, DecodedOp* op);
void OpSub_EaxImm(EmuState* state, DecodedOp* op);

void OpXor_EbGb(EmuState* state, DecodedOp* op);
void OpXor_GbEb(EmuState* state, DecodedOp* op);
void OpXor_GvEv(EmuState* state, DecodedOp* op);
void OpXor_AlImm(EmuState* state, DecodedOp* op);
void OpXor_EaxImm(EmuState* state, DecodedOp* op);

void OpCmp_AlImm(EmuState* state, DecodedOp* op);
void OpCmp_EaxImm(EmuState* state, DecodedOp* op);

void OpTest_EbGb(EmuState* state, DecodedOp* op);
void OpTest_AlImm(EmuState* state, DecodedOp* op);
void OpTest_EaxImm(EmuState* state, DecodedOp* op);

void OpInc_Reg(EmuState* state, DecodedOp* op);

void OpDec_Reg(EmuState* state, DecodedOp* op);

} // namespace x86emu
