// Comparison & Test
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpCmp_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpCmp_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic

LogicFlow OpCmp_GvEv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_GvEv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic

LogicFlow OpTest_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpTest_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpTest_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic

LogicFlow OpCmpxchg_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmpxchg_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmpxchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic
LogicFlow OpCmpxchg_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// SetCC wrappers 0-15
LogicFlow OpSetcc_0(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_1(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_3(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_4(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_5(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_6(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_7(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_10(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_11(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_12(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_13(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_14(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSetcc_15(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterCompareOps();

}  // namespace fiberish
