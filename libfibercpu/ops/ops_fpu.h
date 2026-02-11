// FPU Instructions
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpFpu_D8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_D9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DA(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DB(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DC(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DD(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DE(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpFpu_DF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterFpuOps();
}  // namespace fiberish
