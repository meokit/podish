// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpMov_Sse_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Sse_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovd_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovq_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovd_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovq_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovdqa_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovdqa_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovdqu_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovdqu_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovhpd_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovhpd_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovhps_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovhps_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovlpd_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovlpd_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovlps_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovlps_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpDup_Sse_Lo(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpDup_Sse_Hi(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovmsk_Unified(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovnt_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovntdq(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovnti(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMaskmovdqu(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpGroup_Mov6F(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Mov7F(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Mov12(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Mov13(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Mov16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Mov17(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterSseMovOps();
}  // namespace fiberish
