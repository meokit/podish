#pragma once
#include "../common.h"
#include "../state.h"
#include "../decoder.h"

namespace x86emu {

// Logical
void OpPand_Sse(EmuState* state, DecodedOp* op);
void OpPandn_Sse(EmuState* state, DecodedOp* op);
void OpPor_Sse(EmuState* state, DecodedOp* op);
void OpPxor_Sse(EmuState* state, DecodedOp* op);

// Arithmetic
void OpPaddb_Sse(EmuState* state, DecodedOp* op);
void OpPaddw_Sse(EmuState* state, DecodedOp* op);
void OpPaddd_Sse(EmuState* state, DecodedOp* op);
void OpPaddq_Sse(EmuState* state, DecodedOp* op);
void OpPsubb_Sse(EmuState* state, DecodedOp* op);
void OpPsubw_Sse(EmuState* state, DecodedOp* op);
void OpPsubd_Sse(EmuState* state, DecodedOp* op);
void OpPsubq_Sse(EmuState* state, DecodedOp* op);
void OpPmuludq_Sse(EmuState* state, DecodedOp* op);

// Comparison
void OpPcmpeqb_Sse(EmuState* state, DecodedOp* op);
void OpPcmpeqw_Sse(EmuState* state, DecodedOp* op);
void OpPcmpeqd_Sse(EmuState* state, DecodedOp* op);
void OpPcmpgtb_Sse(EmuState* state, DecodedOp* op);
void OpPcmpgtw_Sse(EmuState* state, DecodedOp* op);
void OpPcmpgtd_Sse(EmuState* state, DecodedOp* op);

// Max/Min
void OpPmaxub_Sse(EmuState* state, DecodedOp* op);
void OpPminub_Sse(EmuState* state, DecodedOp* op);
void OpPmaxsw_Sse(EmuState* state, DecodedOp* op);
void OpPminsw_Sse(EmuState* state, DecodedOp* op);

// Shift
void OpPsllw_Sse(EmuState* state, DecodedOp* op);
void OpPslld_Sse(EmuState* state, DecodedOp* op);
void OpPsllq_Sse(EmuState* state, DecodedOp* op);
void OpPsraw_Sse(EmuState* state, DecodedOp* op);
void OpPsrad_Sse(EmuState* state, DecodedOp* op);
void OpPsrlw_Sse(EmuState* state, DecodedOp* op);
void OpPsrld_Sse(EmuState* state, DecodedOp* op);
void OpPsrlq_Sse(EmuState* state, DecodedOp* op);
void OpPslldq_Sse(EmuState* state, DecodedOp* op);
void OpPsrldq_Sse(EmuState* state, DecodedOp* op);

// Shuffle/Pack/Unpack
void OpPshufd_Sse(EmuState* state, DecodedOp* op);
void OpPshufhw_Sse(EmuState* state, DecodedOp* op);
void OpPshuflw_Sse(EmuState* state, DecodedOp* op);
void OpPunpckhbw_Sse(EmuState* state, DecodedOp* op);
void OpPunpckhwd_Sse(EmuState* state, DecodedOp* op);
void OpPunpckhdq_Sse(EmuState* state, DecodedOp* op);
void OpPunpckhqdq_Sse(EmuState* state, DecodedOp* op);
void OpPunpcklbw_Sse(EmuState* state, DecodedOp* op);
void OpPunpcklwd_Sse(EmuState* state, DecodedOp* op);
void OpPunpckldq_Sse(EmuState* state, DecodedOp* op);
void OpPunpcklqdq_Sse(EmuState* state, DecodedOp* op);
void OpPacksswb_Sse(EmuState* state, DecodedOp* op);
void OpPackssdw_Sse(EmuState* state, DecodedOp* op);
void OpPackuswb_Sse(EmuState* state, DecodedOp* op);

// Move / Extraction / Insertion
void OpPextrw_Sse(EmuState* state, DecodedOp* op);
void OpPinsrw_Sse(EmuState* state, DecodedOp* op);
void OpPmovmskb_Sse(EmuState* state, DecodedOp* op);

// Groups
void OpGroup_Pshuf(EmuState* state, DecodedOp* op);
void OpGroup_Sse_Shift_Imm_W(EmuState* state, DecodedOp* op); // 0F 71
void OpGroup_Sse_Shift_Imm_D(EmuState* state, DecodedOp* op); // 0F 72
void OpGroup_Sse_Shift_Imm_Q(EmuState* state, DecodedOp* op); // 0F 73

} // namespace x86emu
