// Multiplication & Division
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpImul_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpImul_GvEvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpImul_GvEvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterMulDivOps();
}  // namespace fiberish
