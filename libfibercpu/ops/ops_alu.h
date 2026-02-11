// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpAdd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_EvGv_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Specialized

LogicFlow OpAdd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdd_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpOr_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpOr_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAdc_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAdc_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSbb_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSbb_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpAnd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpAnd_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpSub_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSub_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpXor_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXor_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpInc_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpInc_Reg_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpDec_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpDec_Reg_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpCmp_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmp_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpTest_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpTest_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpTest_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterAluOps();
}  // namespace fiberish
