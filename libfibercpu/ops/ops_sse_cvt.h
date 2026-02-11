// SSE/SSE2 Type Conversions
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpCvt_2A(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCvt_2C(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCvt_2D(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCvt_5A(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCvt_5B(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCvt_E6(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterSseCvtOps();
}  // namespace fiberish
