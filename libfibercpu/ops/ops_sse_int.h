#pragma once
#include "../common.h"
#include "../decoder.h"
#include "../state.h"

namespace fiberish {
namespace op {

LogicFlow OpPand_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPandn_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPor_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPxor_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmuludq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmaddwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddusb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddusw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddsb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPaddsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubusb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubusw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubsb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsubsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmullw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmulhw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmulhuw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPavgb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPavgw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsadbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpeqb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpeqw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpeqd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpgtb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpgtw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPcmpgtd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmaxub_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPminub_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmaxsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPminsw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpPsllw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPslld_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsllq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsraw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrad_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrlw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrld_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrlq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpPsllw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPslld_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsllq_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsraw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrad_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrlw_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrld_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrlq_Sse_Group(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpPslldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPsrldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPshufd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPshufhw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPshuflw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpckhbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpckhwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpckhdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpckhqdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpcklbw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpcklwd_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpckldq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPunpcklqdq_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPacksswb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPackssdw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPackuswb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPextrw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPinsrw_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPmovmskb_Sse(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpGroup_Pshuf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Sse_Shift_Imm_W(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Sse_Shift_Imm_D(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_Sse_Shift_Imm_Q(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterSseIntOps();
}  // namespace fiberish
