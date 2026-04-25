#include "ops_mmx_impl.h"

namespace fiberish {
void RegisterMmxOps() {
    using namespace op;

    // =========================================================================================
    // EMMS - Empty MMX State
    // =========================================================================================
    g_Handlers[0x177] = DispatchWrapper<OpEmms>;  // 0F 77: EMMS

    // =========================================================================================
    // Data Movement
    // =========================================================================================
    g_Handlers[0x16E] = DispatchWrapper<OpMovd_ToMmx>;    // 0F 6E: MOVD r/m32, mm
    g_Handlers[0x17E] = DispatchWrapper<OpMovd_FromMmx>;  // 0F 7E: MOVD mm, r/m32
    g_Handlers[0x16F] = DispatchWrapper<OpMovq_ToMmx>;    // 0F 6F: MOVQ mm/m64, mm
    g_Handlers[0x17F] = DispatchWrapper<OpMovq_FromMmx>;  // 0F 7F: MOVQ mm, mm/m64

    // =========================================================================================
    // Arithmetic Operations
    // =========================================================================================
    g_Handlers[0x1FC] = DispatchWrapper<OpPaddb_Mmx>;  // 0F FC: PADDB
    g_Handlers[0x1FD] = DispatchWrapper<OpPaddw_Mmx>;  // 0F FD: PADDW
    g_Handlers[0x1FE] = DispatchWrapper<OpPaddd_Mmx>;  // 0F FE: PADDD
    g_Handlers[0x1D4] = DispatchWrapper<OpPaddq_Mmx>;  // 0F D4: PADDQ
    g_Handlers[0x1F8] = DispatchWrapper<OpPsubb_Mmx>;  // 0F F8: PSUBB
    g_Handlers[0x1F9] = DispatchWrapper<OpPsubw_Mmx>;  // 0F F9: PSUBW
    g_Handlers[0x1FA] = DispatchWrapper<OpPsubd_Mmx>;  // 0F FA: PSUBD
    g_Handlers[0x1FB] = DispatchWrapper<OpPsubq_Mmx>;  // 0F FB: PSUBQ

    // Saturated Arithmetic
    g_Handlers[0x1DC] = DispatchWrapper<OpPaddusb_Mmx>;  // 0F DC: PADDUSB
    g_Handlers[0x1DD] = DispatchWrapper<OpPaddusw_Mmx>;  // 0F DD: PADDUSW
    g_Handlers[0x1EC] = DispatchWrapper<OpPaddsb_Mmx>;   // 0F EC: PADDSB
    g_Handlers[0x1ED] = DispatchWrapper<OpPaddsw_Mmx>;   // 0F ED: PADDSW
    g_Handlers[0x1D8] = DispatchWrapper<OpPsubusb_Mmx>;  // 0F D8: PSUBUSB
    g_Handlers[0x1D9] = DispatchWrapper<OpPsubusw_Mmx>;  // 0F D9: PSUBUSW
    g_Handlers[0x1E8] = DispatchWrapper<OpPsubsb_Mmx>;   // 0F E8: PSUBSB
    g_Handlers[0x1E9] = DispatchWrapper<OpPsubsw_Mmx>;   // 0F E9: PSUBSW

    // Multiply
    g_Handlers[0x1D5] = DispatchWrapper<OpPmullw_Mmx>;   // 0F D5: PMULLW
    g_Handlers[0x1E5] = DispatchWrapper<OpPmulhw_Mmx>;   // 0F E5: PMULHW
    g_Handlers[0x1E4] = DispatchWrapper<OpPmulhuw_Mmx>;  // 0F E4: PMULHUW
    g_Handlers[0x1F4] = DispatchWrapper<OpPmuludq_Mmx>;  // 0F F4: PMULUDQ
    g_Handlers[0x1F5] = DispatchWrapper<OpPmaddwd_Mmx>;  // 0F F5: PMADDWD

    // =========================================================================================
    // Logical Operations
    // =========================================================================================
    g_Handlers[0x1DB] = DispatchWrapper<OpPand_Mmx>;   // 0F DB: PAND
    g_Handlers[0x1DF] = DispatchWrapper<OpPandn_Mmx>;  // 0F DF: PANDN
    g_Handlers[0x1EB] = DispatchWrapper<OpPor_Mmx>;    // 0F EB: POR
    g_Handlers[0x1EF] = DispatchWrapper<OpPxor_Mmx>;   // 0F EF: PXOR

    // =========================================================================================
    // Compare Operations
    // =========================================================================================
    g_Handlers[0x174] = DispatchWrapper<OpPcmpeqb_Mmx>;  // 0F 74: PCMPEQB
    g_Handlers[0x175] = DispatchWrapper<OpPcmpeqw_Mmx>;  // 0F 75: PCMPEQW
    g_Handlers[0x176] = DispatchWrapper<OpPcmpeqd_Mmx>;  // 0F 76: PCMPEQD
    g_Handlers[0x164] = DispatchWrapper<OpPcmpgtb_Mmx>;  // 0F 64: PCMPGTB
    g_Handlers[0x165] = DispatchWrapper<OpPcmpgtw_Mmx>;  // 0F 65: PCMPGTW
    g_Handlers[0x166] = DispatchWrapper<OpPcmpgtd_Mmx>;  // 0F 66: PCMPGTD
    g_Handlers[0x1DA] = DispatchWrapper<OpPminub_Mmx>;   // 0F DA: PMINUB
    g_Handlers[0x1DE] = DispatchWrapper<OpPmaxub_Mmx>;   // 0F DE: PMAXUB
    g_Handlers[0x1EA] = DispatchWrapper<OpPminsw_Mmx>;   // 0F EA: PMINSW
    g_Handlers[0x1EE] = DispatchWrapper<OpPmaxsw_Mmx>;   // 0F EE: PMAXSW

    // =========================================================================================
    // Shift Operations (Register)
    // =========================================================================================
    g_Handlers[0x1F1] = DispatchWrapper<OpPsllw_Mmx>;  // 0F F1: PSLLW mm, mm/m64
    g_Handlers[0x1F2] = DispatchWrapper<OpPslld_Mmx>;  // 0F F2: PSLLD mm, mm/m64
    g_Handlers[0x1F3] = DispatchWrapper<OpPsllq_Mmx>;  // 0F F3: PSLLQ mm, mm/m64
    g_Handlers[0x1D1] = DispatchWrapper<OpPsrlw_Mmx>;  // 0F D1: PSRLW mm, mm/m64
    g_Handlers[0x1D2] = DispatchWrapper<OpPsrld_Mmx>;  // 0F D2: PSRLD mm, mm/m64
    g_Handlers[0x1D3] = DispatchWrapper<OpPsrlq_Mmx>;  // 0F D3: PSRLQ mm, mm/m64
    g_Handlers[0x1E1] = DispatchWrapper<OpPsraw_Mmx>;  // 0F E1: PSRAW mm, mm/m64
    g_Handlers[0x1E2] = DispatchWrapper<OpPsrad_Mmx>;  // 0F E2: PSRAD mm, mm/m64

    // =========================================================================================
    // Shift Operations (Immediate) - Group 12/13/14
    // =========================================================================================
    g_Handlers[0x171] = DispatchWrapper<OpGroup_Mmx_Shift_Imm_W>;  // 0F 71: Group 12
    g_Handlers[0x172] = DispatchWrapper<OpGroup_Mmx_Shift_Imm_D>;  // 0F 72: Group 13
    g_Handlers[0x173] = DispatchWrapper<OpGroup_Mmx_Shift_Imm_Q>;  // 0F 73: Group 14

    // =========================================================================================
    // Pack/Unpack Operations
    // =========================================================================================
    g_Handlers[0x170] = DispatchWrapper<OpPshufw_Mmx>;     // 0F 70: PSHUFW mm, mm/m64, imm8
    g_Handlers[0x163] = DispatchWrapper<OpPacksswb_Mmx>;   // 0F 63: PACKSSWB
    g_Handlers[0x167] = DispatchWrapper<OpPackuswb_Mmx>;   // 0F 67: PACKUSWB
    g_Handlers[0x16B] = DispatchWrapper<OpPackssdw_Mmx>;   // 0F 6B: PACKSSDW
    g_Handlers[0x168] = DispatchWrapper<OpPunpckhbw_Mmx>;  // 0F 68: PUNPCKHBW
    g_Handlers[0x169] = DispatchWrapper<OpPunpckhwd_Mmx>;  // 0F 69: PUNPCKHWD
    g_Handlers[0x16A] = DispatchWrapper<OpPunpckhdq_Mmx>;  // 0F 6A: PUNPCKHDQ
    g_Handlers[0x160] = DispatchWrapper<OpPunpcklbw_Mmx>;  // 0F 60: PUNPCKLBW
    g_Handlers[0x161] = DispatchWrapper<OpPunpcklwd_Mmx>;  // 0F 61: PUNPCKLWD
    g_Handlers[0x162] = DispatchWrapper<OpPunpckldq_Mmx>;  // 0F 62: PUNPCKLDQ

    // MMX extensions
    g_Handlers[0x1E0] = DispatchWrapper<OpPavgb_Mmx>;     // 0F E0: PAVGB
    g_Handlers[0x1E3] = DispatchWrapper<OpPavgw_Mmx>;     // 0F E3: PAVGW
    g_Handlers[0x1F6] = DispatchWrapper<OpPsadbw_Mmx>;    // 0F F6: PSADBW
    g_Handlers[0x1C4] = DispatchWrapper<OpPinsrw_Mmx>;    // 0F C4: PINSRW
    g_Handlers[0x1C5] = DispatchWrapper<OpPextrw_Mmx>;    // 0F C5: PEXTRW
    g_Handlers[0x1D7] = DispatchWrapper<OpPmovmskb_Mmx>;  // 0F D7: PMOVMSKB
    g_Handlers[0x1E7] = DispatchWrapper<OpMovntq_Mmx>;    // 0F E7: MOVNTQ
    g_Handlers[0x1F7] = DispatchWrapper<OpMaskmovq_Mmx>;  // 0F F7: MASKMOVQ
}

}  // namespace fiberish
