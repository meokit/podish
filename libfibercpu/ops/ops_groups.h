// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpGroup1_EbIb_Add_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Add_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Or_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Or_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Adc_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Adc_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Sbb_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Sbb_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_And_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_And_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Sub_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Sub_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Xor_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Xor_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Cmp_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EbIb_Cmp_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup1_EvIz_Add_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Add_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Add_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Add_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Sub_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Sub_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Sub_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Sub_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Cmp_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Cmp_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Cmp_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Cmp_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup1_EvIb_Add_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Add_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Add_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Add_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Sub_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Sub_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Sub_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Sub_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Cmp_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Cmp_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Cmp_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Cmp_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup3_Ev_Not_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Not_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Not_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Not_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Neg_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Neg_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Neg_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Neg_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Mul_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Mul_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Mul_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Mul_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Imul_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Imul_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Imul_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Imul_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Div_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Div_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Div_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Div_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Idiv_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Idiv_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Idiv_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Idiv_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup5_Ev_Inc_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Inc_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Inc_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Inc_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Dec_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Dec_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Dec_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Dec_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Call_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Call_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Call_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Call_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Jmp_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Jmp_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Jmp_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Jmp_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Push_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Push_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Push_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Push_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup3_Eb_Not_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Not_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Neg_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Neg_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Mul_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Mul_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Imul_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Imul_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Div_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Div_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Idiv_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Idiv_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup4_Eb_Inc_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup4_Eb_Inc_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup4_Eb_Dec_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup4_Eb_Dec_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpGroup1_EbIb_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIz_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup1_EvIb_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Eb_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup3_Ev_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup4_Eb_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpGroup5_Ev_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpCdq(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCwde(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXadd_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXadd_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpUd2_Groups(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterGroupOps();
}  // namespace fiberish
