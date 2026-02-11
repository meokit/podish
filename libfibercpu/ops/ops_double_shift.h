// Double-Shift Instructions
// SHLD/SHRD operation declarations

#pragma once

#include "../common.h"
#include "../decoder.h"
#include "../state.h"

namespace fiberish {
namespace op {

LogicFlow OpShld_EvGvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpShld_EvGvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpShrd_EvGvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpShrd_EvGvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterDoubleShiftOps();
}  // namespace fiberish
