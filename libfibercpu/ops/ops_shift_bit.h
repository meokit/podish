// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpGroup2_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Eb1(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EbCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// SHL Specializations
LogicFlow OpGroup2_EvIb_Shl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Shl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Shl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvIb_Shl_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Shl_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Shl_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// SHR Specializations
LogicFlow OpGroup2_EvIb_Shr(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Shr(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Shr(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvIb_Shr_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Shr_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Shr_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// SAR Specializations
LogicFlow OpGroup2_EvIb_Sar(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Sar(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Sar(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvIb_Sar_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_Ev1_Sar_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup2_EvCl_Sar_ModReg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpBt_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup8_EvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBtr_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBts_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBtc_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBsr_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBsf_Tzcnt_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpBswap_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterShiftBitOps();
}  // namespace fiberish
