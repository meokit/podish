// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {

simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred);
simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred);
simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred);
simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred);

namespace op {

LogicFlow OpAdd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMul_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpDiv_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAndn_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMin_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMax_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovAp_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovAp_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSqrt_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpUcomis_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpComis_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpRcp_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpRsqrt_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpShuf_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpUnpckl_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpUnpckh_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterSseFpOps();

}  // namespace fiberish
