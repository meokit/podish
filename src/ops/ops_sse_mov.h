// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

// SSE/SSE2 Mov Ops
void OpMov_Sse_Load(EmuState* state, DecodedOp* op);
void OpMov_Sse_Store(EmuState* state, DecodedOp* op);

void OpMovd_Load(EmuState* state, DecodedOp* op);
void OpMovd_Store(EmuState* state, DecodedOp* op);

void OpMovq_Load(EmuState* state, DecodedOp* op);
void OpMovq_Store(EmuState* state, DecodedOp* op);

void OpMovdqa_Load(EmuState* state, DecodedOp* op);
void OpMovdqa_Store(EmuState* state, DecodedOp* op);

void OpMovdqu_Load(EmuState* state, DecodedOp* op);
void OpMovdqu_Store(EmuState* state, DecodedOp* op);

void OpMovhpd(EmuState* state, DecodedOp* op);
void OpMovhps(EmuState* state, DecodedOp* op);
void OpMovlpd(EmuState* state, DecodedOp* op);
void OpMovlps(EmuState* state, DecodedOp* op);

void OpMovmskps(EmuState* state, DecodedOp* op);

void OpGroup_Mov6F(EmuState* state, DecodedOp* op);
void OpGroup_Mov7F(EmuState* state, DecodedOp* op);
void OpGroup_Mov12(EmuState* state, DecodedOp* op);
void OpGroup_Mov13(EmuState* state, DecodedOp* op);
void OpGroup_Mov16(EmuState* state, DecodedOp* op);
void OpGroup_Mov17(EmuState* state, DecodedOp* op);

} // namespace x86emu
