// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace x86emu {

void OpAdd_Sse(EmuState* state, DecodedOp* op);

void OpSub_Sse(EmuState* state, DecodedOp* op);

void OpMul_Sse(EmuState* state, DecodedOp* op);

void OpDiv_Sse(EmuState* state, DecodedOp* op);

void OpAnd_Sse(EmuState* state, DecodedOp* op);

void OpAndn_Sse(EmuState* state, DecodedOp* op);

void OpOr_Sse(EmuState* state, DecodedOp* op);

void OpXor_Sse(EmuState* state, DecodedOp* op);

void OpCmp_Sse(EmuState* state, DecodedOp* op);

void OpMaxMin_Sse(EmuState* state, DecodedOp* op);

void OpMovAp_Sse(EmuState* state, DecodedOp* op);

simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred);

simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred);

simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred);

simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred);

// New SSE operations
void OpSqrt_Sse(EmuState* state, DecodedOp* op);

void OpUcomis_Unified(EmuState* state, DecodedOp* op);

void OpShuf_Unified(EmuState* state, DecodedOp* op);

void OpUnpckl_Unified(EmuState* state, DecodedOp* op);
void OpUnpckh_Unified(EmuState* state, DecodedOp* op);

} // namespace x86emu
