// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpMov_EvGv(EmuState* state, DecodedOp* op);

void OpMov_GvEv(EmuState* state, DecodedOp* op);

void OpMov_EbGb(EmuState* state, DecodedOp* op);

void OpMov_GbEb(EmuState* state, DecodedOp* op);

void OpMov_EbIb(EmuState* state, DecodedOp* op);

void OpMov_RegImm(EmuState* state, DecodedOp* op);

void OpMov_RegImm8(EmuState* state, DecodedOp* op);

void OpMov_EvIz(EmuState* state, DecodedOp* op);

void OpMov_Moffs_Load(EmuState* state, DecodedOp* op);

void OpMov_Moffs_Store(EmuState* state, DecodedOp* op);

void OpMovzx_Byte(EmuState* state, DecodedOp* op);

void OpMovzx_Word(EmuState* state, DecodedOp* op);

void OpMovsx_Byte(EmuState* state, DecodedOp* op);

void OpMovsx_Word(EmuState* state, DecodedOp* op);

void OpLea(EmuState* state, DecodedOp* op);

void OpPush_Reg(EmuState* state, DecodedOp* op);

void OpPush_Imm(EmuState* state, DecodedOp* op);

void OpPop_Reg(EmuState* state, DecodedOp* op);

} // namespace x86emu
