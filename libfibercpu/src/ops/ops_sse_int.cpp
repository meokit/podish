#include "ops_registration_utils.h"
#include "ops_sse_int_impl.h"

namespace fiberish {
void RegisterSseIntOps() {
    using namespace op;
    RegisterSseOp<OpPand_Sse>(0x1DB);
    RegisterSseOp<OpPandn_Sse>(0x1DF);
    RegisterSseOp<OpPor_Sse>(0x1EB);
    RegisterSseOp<OpPxor_Sse>(0x1EF);
    RegisterSseOp<OpPaddb_Sse>(0x1FC);
    RegisterSseOp<OpPaddw_Sse>(0x1FD);
    RegisterSseOp<OpPaddd_Sse>(0x1FE);
    RegisterSseOp<OpPaddq_Sse>(0x1D4);
    RegisterSseOp<OpPsubb_Sse>(0x1F8);
    RegisterSseOp<OpPsubw_Sse>(0x1F9);
    RegisterSseOp<OpPsubd_Sse>(0x1FA);
    RegisterSseOp<OpPsubq_Sse>(0x1FB);
    RegisterSseOp<OpPmuludq_Sse>(0x1F4);
    RegisterSseOp<OpPmaddwd_Sse>(0x1F5);
    RegisterSseOp<OpPcmpeqb_Sse>(0x174);
    RegisterSseOp<OpPcmpeqw_Sse>(0x175);
    RegisterSseOp<OpPcmpeqd_Sse>(0x176);
    RegisterSseOp<OpPcmpgtb_Sse>(0x164);
    RegisterSseOp<OpPcmpgtw_Sse>(0x165);
    RegisterSseOp<OpPcmpgtd_Sse>(0x166);
    RegisterSseOp<OpPmaxub_Sse>(0x1DE);
    RegisterSseOp<OpPminub_Sse>(0x1DA);
    RegisterSseOp<OpPmaxsw_Sse>(0x1EE);
    RegisterSseOp<OpPminsw_Sse>(0x1EA);
    RegisterSseOp<OpPsllw_Sse>(0x1F1);
    RegisterSseOp<OpPslld_Sse>(0x1F2);
    RegisterSseOp<OpPsllq_Sse>(0x1F3);
    RegisterSseOp<OpPsraw_Sse>(0x1E1);
    RegisterSseOp<OpPsrad_Sse>(0x1E2);
    RegisterSseOp<OpPsrlw_Sse>(0x1D1);
    RegisterSseOp<OpPsrld_Sse>(0x1D2);
    RegisterSseOp<OpPsrlq_Sse>(0x1D3);

    // 0F 70: PSHUFD (66) / PSHUFLW (F2) / PSHUFHW (F3)
    RegisterSseOp<OpGroup_Pshuf>(0x170);  // 66 0F 70

    SpecCriteria criteria_pshuf_f2;
    criteria_pshuf_f2.prefix_mask = prefix::REPNE;  // REPNE (F2)
    criteria_pshuf_f2.prefix_val = prefix::REPNE;
    RegisterSpecializedHandler(0x170, criteria_pshuf_f2, (HandlerFunc)DispatchWrapper<OpGroup_Pshuf>);

    SpecCriteria criteria_pshuf_f3;
    criteria_pshuf_f3.prefix_mask = prefix::REP;  // REP (F3)
    criteria_pshuf_f3.prefix_val = prefix::REP;
    RegisterSpecializedHandler(0x170, criteria_pshuf_f3, (HandlerFunc)DispatchWrapper<OpGroup_Pshuf>);

    RegisterSseOp<OpGroup_Sse_Shift_Imm_W>(0x171);
    RegisterSseOp<OpGroup_Sse_Shift_Imm_D>(0x172);
    RegisterSseOp<OpGroup_Sse_Shift_Imm_Q>(0x173);
    RegisterSseOp<OpPunpckhbw_Sse>(0x168);
    RegisterSseOp<OpPunpckhwd_Sse>(0x169);
    RegisterSseOp<OpPunpckhdq_Sse>(0x16A);
    RegisterSseOp<OpPunpckhqdq_Sse>(0x16D);
    RegisterSseOp<OpPunpcklbw_Sse>(0x160);
    RegisterSseOp<OpPunpcklwd_Sse>(0x161);
    RegisterSseOp<OpPunpckldq_Sse>(0x162);
    RegisterSseOp<OpPunpcklqdq_Sse>(0x16C);
    RegisterSseOp<OpPacksswb_Sse>(0x163);
    RegisterSseOp<OpPackssdw_Sse>(0x16B);
    RegisterSseOp<OpPackuswb_Sse>(0x167);
    RegisterSseOp<OpPmullw_Sse>(0x1D5);
    RegisterSseOp<OpPmulhw_Sse>(0x1E5);
    RegisterSseOp<OpPmulhuw_Sse>(0x1E4);
    RegisterSseOp<OpPaddusb_Sse>(0x1DC);
    RegisterSseOp<OpPaddusw_Sse>(0x1DD);
    RegisterSseOp<OpPaddsb_Sse>(0x1EC);
    RegisterSseOp<OpPaddsw_Sse>(0x1ED);
    RegisterSseOp<OpPsubusb_Sse>(0x1D8);
    RegisterSseOp<OpPsubusw_Sse>(0x1D9);
    RegisterSseOp<OpPsubsb_Sse>(0x1E8);
    RegisterSseOp<OpPsubsw_Sse>(0x1E9);
    RegisterSseOp<OpPavgb_Sse>(0x1E0);
    RegisterSseOp<OpPavgw_Sse>(0x1E3);
    RegisterSseOp<OpPsadbw_Sse>(0x1F6);
    RegisterSseOp<OpPextrw_Sse>(0x1C5);
    RegisterSseOp<OpPinsrw_Sse>(0x1C4);
    RegisterSseOp<OpPmovmskb_Sse>(0x1D7);
}

}  // namespace fiberish
