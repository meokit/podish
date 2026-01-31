#include "ops.h"
#include "dispatch.h"

namespace x86emu {

// Sentinel Handler
ATTR_PRESERVE_NONE
void OpExitBlock(EmuState* state, DecodedOp* op) {
    // End of Threaded Dispatch Chain.
    // Returns to X86_Run loop.
}

// Global dispatch table
// This is initialized by HandlerInit static constructor below
HandlerFunc g_Handlers[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i=0; i<1024; ++i) g_Handlers[i] = nullptr;
        
        // 1. Set NOP
        g_Handlers[0x90] = DispatchWrapper<OpNop>;
        
        // Sentinel (1023)
        // Must match DecodeBlock sentinel index
        extern void OpExitBlock(EmuState* state, DecodedOp* op);
        g_Handlers[1023] = OpExitBlock;
        
        // 2. Set MOV
        g_Handlers[0x87] = DispatchWrapper<OpXchg_EvGv>; // XCHG r/m32, r32
        g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
        g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;
        
        // 8-bit MOV
        g_Handlers[0x88] = DispatchWrapper<OpMov_EbGb>;  // MOV r/m8, r8
        g_Handlers[0x8A] = DispatchWrapper<OpMov_GbEb>;  // MOV r8, r/m8
        g_Handlers[0xC6] = DispatchWrapper<OpMov_EbIb>;  // MOV r/m8, imm8
        
        for(int i=0; i<8; ++i) {
            g_Handlers[0xB0+i] = DispatchWrapper<OpMov_RegImm8>;
            g_Handlers[0xB8+i] = DispatchWrapper<OpMov_RegImm>;
        }
        g_Handlers[0xC7] = DispatchWrapper<OpMov_EvIz>; // MOV r/m32, imm32
        
        // MOV moffs (A0-A3)
        g_Handlers[0xA0] = DispatchWrapper<OpMov_Moffs_Load>;
        g_Handlers[0xA1] = DispatchWrapper<OpMov_Moffs_Load>;
        g_Handlers[0xA2] = DispatchWrapper<OpMov_Moffs_Store>;
        g_Handlers[0xA3] = DispatchWrapper<OpMov_Moffs_Store>;
        
        // 3. Set LEA
        g_Handlers[0x8D] = DispatchWrapper<OpLea>;
        
        // 4. Set PUSH
        for (int i=0; i<8; ++i) g_Handlers[0x50+i] = DispatchWrapper<OpPush_Reg>;
        g_Handlers[0x68] = DispatchWrapper<OpPush_Imm>;
        g_Handlers[0x6A] = DispatchWrapper<OpPush_Imm>;
        
        // 5. Set POP
        for (int i=0; i<8; ++i) g_Handlers[0x58+i] = DispatchWrapper<OpPop_Reg>;
        
        // 6. Set HLT
        g_Handlers[0xF4] = DispatchWrapper<OpHlt>;
        
        // Control Flow
        g_Handlers[0x9C] = DispatchWrapper<OpPushf>;
        g_Handlers[0x9D] = DispatchWrapper<OpPopf>;
        
        g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel>; // JMP rel32
        g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel>; // JMP rel8
        g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>; // CALL rel32
        g_Handlers[0xC3] = DispatchWrapper<OpRet>;      // RET
        g_Handlers[0xCD] = DispatchWrapper<OpInt>;      // INT imm8
        g_Handlers[0xCC] = DispatchWrapper<OpInt3>;     // INT3
        
        g_Handlers[0xF5] = DispatchWrapper<OpCmc>;      // CMC
        g_Handlers[0xF8] = DispatchWrapper<OpClc>;      // CLC
        g_Handlers[0xF9] = DispatchWrapper<OpStc>;      // STC
        g_Handlers[0xFA] = DispatchWrapper<OpCli>;      // CLI
        g_Handlers[0xFB] = DispatchWrapper<OpSti>;      // STI
        g_Handlers[0xFC] = DispatchWrapper<OpCld>;      // CLD
        g_Handlers[0xFD] = DispatchWrapper<OpStd>;      // STD
        
        for (int i=0; i<16; ++i) {
            g_Handlers[0x70+i] = DispatchWrapper<OpJcc_Rel>; // Jcc rel8
            g_Handlers[0x180+i] = DispatchWrapper<OpJcc_Rel>; // Jcc rel32 (0F 8x)
        }

        // 7. Arithmetic & Logic
        g_Handlers[0x00] = DispatchWrapper<OpAdd_EbGb>;
        g_Handlers[0x01] = DispatchWrapper<OpAdd_EvGv>;
        g_Handlers[0x02] = DispatchWrapper<OpAdd_GbEb>;
        g_Handlers[0x03] = DispatchWrapper<OpAdd_GvEv>;
        g_Handlers[0x04] = DispatchWrapper<OpAdd_AlImm>;
        g_Handlers[0x05] = DispatchWrapper<OpAdd_EaxImm>;
        
        g_Handlers[0x08] = DispatchWrapper<OpOr_EbGb>;
        g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv>;
        g_Handlers[0x0A] = DispatchWrapper<OpOr_GbEb>;
        g_Handlers[0x0B] = DispatchWrapper<OpOr_GvEv>;
        g_Handlers[0x0C] = DispatchWrapper<OpOr_AlImm>;
        g_Handlers[0x0D] = DispatchWrapper<OpOr_EaxImm>;
        
        g_Handlers[0x10] = DispatchWrapper<OpAdc_EbGb>;
        g_Handlers[0x11] = DispatchWrapper<OpAdc_EvGv>;
        g_Handlers[0x12] = DispatchWrapper<OpAdc_GbEb>;
        g_Handlers[0x13] = DispatchWrapper<OpAdc_GvEv>;
        g_Handlers[0x14] = DispatchWrapper<OpAdc_AlImm>;
        g_Handlers[0x15] = DispatchWrapper<OpAdc_EaxImm>;

        g_Handlers[0x18] = DispatchWrapper<OpSbb_EbGb>;
        g_Handlers[0x19] = DispatchWrapper<OpSbb_EvGv>;
        g_Handlers[0x1A] = DispatchWrapper<OpSbb_GbEb>;
        g_Handlers[0x1B] = DispatchWrapper<OpSbb_GvEv>;
        g_Handlers[0x1C] = DispatchWrapper<OpSbb_AlImm>;
        g_Handlers[0x1D] = DispatchWrapper<OpSbb_EaxImm>;
        
        g_Handlers[0x20] = DispatchWrapper<OpAnd_EbGb>;
        g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv>;
        g_Handlers[0x22] = DispatchWrapper<OpAnd_GbEb>;
        g_Handlers[0x23] = DispatchWrapper<OpAnd_GvEv>;
        g_Handlers[0x24] = DispatchWrapper<OpAnd_AlImm>;
        g_Handlers[0x25] = DispatchWrapper<OpAnd_EaxImm>;

        g_Handlers[0x28] = DispatchWrapper<OpSub_EbGb>;
        g_Handlers[0x29] = DispatchWrapper<OpSub_EvGv>;
        g_Handlers[0x2A] = DispatchWrapper<OpSub_GbEb>;
        g_Handlers[0x2B] = DispatchWrapper<OpSub_GvEv>;
        g_Handlers[0x2C] = DispatchWrapper<OpSub_AlImm>;
        g_Handlers[0x2D] = DispatchWrapper<OpSub_EaxImm>;
        
        g_Handlers[0x30] = DispatchWrapper<OpXor_EbGb>;
        g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv>;
        g_Handlers[0x32] = DispatchWrapper<OpXor_GbEb>;
        g_Handlers[0x33] = DispatchWrapper<OpXor_GvEv>;
        g_Handlers[0x34] = DispatchWrapper<OpXor_AlImm>;
        g_Handlers[0x35] = DispatchWrapper<OpXor_EaxImm>;
        
        g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;
        g_Handlers[0x39] = DispatchWrapper<OpCmp_EvGv>;
        g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;
        g_Handlers[0x3B] = DispatchWrapper<OpCmp_GvEv>;
        g_Handlers[0x3C] = DispatchWrapper<OpCmp_AlImm>;
        g_Handlers[0x3D] = DispatchWrapper<OpCmp_EaxImm>;
        
        g_Handlers[0x84] = DispatchWrapper<OpTest_EbGb>;
        g_Handlers[0x85] = DispatchWrapper<OpTest_EvGv>;
        g_Handlers[0xA8] = DispatchWrapper<OpTest_AlImm>;
        g_Handlers[0xA9] = DispatchWrapper<OpTest_EaxImm>;
        
        g_Handlers[0x80] = DispatchWrapper<OpGroup1_EbIb>;
        g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz>;
        g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIz>;
        
        g_Handlers[0x98] = DispatchWrapper<OpCwde>;
        g_Handlers[0x99] = DispatchWrapper<OpCdq>;
        
        // Group 2 (Shift/Rotate)
        g_Handlers[0xC0] = DispatchWrapper<OpGroup2_EvIb>;
        g_Handlers[0xC1] = DispatchWrapper<OpGroup2_EvIb>;
        g_Handlers[0xD0] = DispatchWrapper<OpGroup2_Ev1>;
        g_Handlers[0xD1] = DispatchWrapper<OpGroup2_Ev1>;
        g_Handlers[0xD2] = DispatchWrapper<OpGroup2_EvCl>;
        g_Handlers[0xD3] = DispatchWrapper<OpGroup2_EvCl>;
        
        // Group 5
        g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev>;
        
        // Inc/Dec
        for (int i=0; i<8; ++i) {
            g_Handlers[0x40+i] = DispatchWrapper<OpInc_Reg>;
            g_Handlers[0x48+i] = DispatchWrapper<OpDec_Reg>;
        }
        
        // Map 1 (0F xx) -> Index 0x100 + xx
        g_Handlers[0x1A3] = DispatchWrapper<OpBt_EvGv>;
        // UD2 (0F 0B) -> #UD
        g_Handlers[0x10B] = DispatchWrapper<OpUd2>;
        g_Handlers[0x1B3] = DispatchWrapper<OpBtr_EvGv>;
        g_Handlers[0x1B6] = DispatchWrapper<OpMovzx_Byte>;
        g_Handlers[0x1B7] = DispatchWrapper<OpMovzx_Word>;
        g_Handlers[0x1BA] = DispatchWrapper<OpBt_EvIb>;
        g_Handlers[0x1BD] = DispatchWrapper<OpBsr_GvEv>;
        g_Handlers[0x1BE] = DispatchWrapper<OpMovsx_Byte>;
        g_Handlers[0x1BF] = DispatchWrapper<OpMovsx_Word>;
        g_Handlers[0x1BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>; // 0F BC: BSF
        
        // Double-Shift Instructions
        g_Handlers[0x1A4] = DispatchWrapper<OpShld_EvGvIb>;  // 0F A4: SHLD r/m32, r32, imm8
        g_Handlers[0x1A5] = DispatchWrapper<OpShld_EvGvCl>;  // 0F A5: SHLD r/m32, r32, CL
        g_Handlers[0x1AC] = DispatchWrapper<OpShrd_EvGvIb>;  // 0F AC: SHRD r/m32, r32, imm8
        g_Handlers[0x1AD] = DispatchWrapper<OpShrd_EvGvCl>;  // 0F AD: SHRD r/m32, r32, CL
        
        // TZCNT (F3 0F BC) - Map 2 prefix with F3
        g_Handlers[0x2BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;
        
        for (int i=0; i<8; ++i) {
            g_Handlers[0x1C8+i] = DispatchWrapper<OpBswap_Reg>;
        }

        // New Registrations
        // CMP
        g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;
        g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;
        g_Handlers[0x3B] = DispatchWrapper<OpCmp_GvEv>;

        // CMOVcc (0F 4x)
        for (int i=0; i<16; ++i) {
            g_Handlers[0x140+i] = DispatchWrapper<OpCmov_GvEv>;
        }

        // SSE / SSE2
        g_Handlers[0x1C2] = DispatchWrapper<OpCmp_Sse>;
        g_Handlers[0x12A] = DispatchWrapper<OpCvt_2A>;
        g_Handlers[0x15A] = DispatchWrapper<OpCvt_5A>;
        g_Handlers[0x15B] = DispatchWrapper<OpCvt_5B>;
        g_Handlers[0x1E6] = DispatchWrapper<OpCvt_E6>;

        // Batch 002 (Integer)
        g_Handlers[0x69] = DispatchWrapper<OpImul_GvEvIz>;
        g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIz>;
        g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
        g_Handlers[0xF6] = DispatchWrapper<OpGroup3_Ev>;
        g_Handlers[0xF7] = DispatchWrapper<OpGroup3_Ev>;
        g_Handlers[0xFE] = DispatchWrapper<OpGroup4_Eb>;
        
        // Batch 002 (FPU)
        g_Handlers[0xD8] = DispatchWrapper<OpFpu_D8>;
        g_Handlers[0xD9] = DispatchWrapper<OpFpu_D9>;
        g_Handlers[0xDA] = DispatchWrapper<OpFpu_DA>;
        g_Handlers[0xDB] = DispatchWrapper<OpFpu_DB>;
        g_Handlers[0xDC] = DispatchWrapper<OpFpu_DC>;
        g_Handlers[0xDD] = DispatchWrapper<OpFpu_DD>;
        g_Handlers[0xDE] = DispatchWrapper<OpFpu_DE>;
        g_Handlers[0xDF] = DispatchWrapper<OpFpu_DF>;
        
        // SSE New
        g_Handlers[0x12C] = DispatchWrapper<OpCvt_2C>;
        g_Handlers[0x15E] = DispatchWrapper<OpDiv_Sse>;
        
        // Batch 003
        g_Handlers[0x1C7] = DispatchWrapper<OpGroup9>;
        g_Handlers[0x128] = DispatchWrapper<OpMovAp_Sse>;
        g_Handlers[0x129] = DispatchWrapper<OpMovAp_Sse>;
        g_Handlers[0x15F] = DispatchWrapper<OpMaxMin_Sse>;
        g_Handlers[0x15D] = DispatchWrapper<OpMaxMin_Sse>;
        
        // Add / And / Andn
        g_Handlers[0x158] = DispatchWrapper<OpAdd_Sse>;
        g_Handlers[0x159] = DispatchWrapper<OpMul_Sse>;
        g_Handlers[0x15C] = DispatchWrapper<OpSub_Sse>;
        g_Handlers[0x154] = DispatchWrapper<OpAnd_Sse>;
        g_Handlers[0x155] = DispatchWrapper<OpAndn_Sse>;
        g_Handlers[0x156] = DispatchWrapper<OpOr_Sse>;
        g_Handlers[0x157] = DispatchWrapper<OpXor_Sse>;
        
        // New SSE handlers        
        g_Handlers[0x12E] = DispatchWrapper<OpUcomis_Unified>;   // 0F 2E: UCOMISS / UCOMISD
        // g_Handlers[0x22E] REMOVED - Handled by 0x12E
        
        g_Handlers[0x151] = DispatchWrapper<OpSqrt_Sse>;        // 0F 51: SQRTPS/PD/SS/SD
        // g_Handlers[0x251] REMOVED
        
        g_Handlers[0x1C6] = DispatchWrapper<OpShuf_Unified>;    // 0F C6: SHUFPS / SHUFPD
        // g_Handlers[0x2C6] REMOVED
        
        g_Handlers[0x114] = DispatchWrapper<OpUnpckl_Unified>;  // 0F 14: UNPCKLPS / PD
        g_Handlers[0x115] = DispatchWrapper<OpUnpckh_Unified>;  // 0F 15: UNPCKHPS / PD
        // g_Handlers[0x214] / 0x215 REMOVED
        
        // Moves
        g_Handlers[0x110] = DispatchWrapper<OpMov_Sse_Load>;
        g_Handlers[0x111] = DispatchWrapper<OpMov_Sse_Store>;
        g_Handlers[0x112] = DispatchWrapper<OpGroup_Mov12>;
        g_Handlers[0x113] = DispatchWrapper<OpGroup_Mov13>;
        g_Handlers[0x116] = DispatchWrapper<OpGroup_Mov16>;
        g_Handlers[0x117] = DispatchWrapper<OpGroup_Mov17>;
        
        g_Handlers[0x11F] = DispatchWrapper<OpNop>; // Multi-byte NOP (0F 1F)
        
        g_Handlers[0x150] = DispatchWrapper<OpMovmskps>;
        
        g_Handlers[0x16E] = DispatchWrapper<OpMovd_Load>;
        g_Handlers[0x17E] = DispatchWrapper<OpMovd_Store>;
        
        g_Handlers[0x16F] = DispatchWrapper<OpGroup_Mov6F>;
        g_Handlers[0x17F] = DispatchWrapper<OpGroup_Mov7F>;
        
        // XADD
        g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Rm_R>;
        g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Rm_R>;

        // SSE Integer (Batch 005)
        g_Handlers[0x1DB] = DispatchWrapper<OpPand_Sse>;
        g_Handlers[0x1DF] = DispatchWrapper<OpPandn_Sse>;
        g_Handlers[0x1EB] = DispatchWrapper<OpPor_Sse>;
        g_Handlers[0x1EF] = DispatchWrapper<OpPxor_Sse>;
        
        g_Handlers[0x1FC] = DispatchWrapper<OpPaddb_Sse>;
        g_Handlers[0x1FD] = DispatchWrapper<OpPaddw_Sse>;
        g_Handlers[0x1FE] = DispatchWrapper<OpPaddd_Sse>;
        g_Handlers[0x1D4] = DispatchWrapper<OpPaddq_Sse>;
        
        g_Handlers[0x1F8] = DispatchWrapper<OpPsubb_Sse>;
        g_Handlers[0x1F9] = DispatchWrapper<OpPsubw_Sse>;
        g_Handlers[0x1FA] = DispatchWrapper<OpPsubd_Sse>;
        g_Handlers[0x1FB] = DispatchWrapper<OpPsubq_Sse>;
        
        g_Handlers[0x1F4] = DispatchWrapper<OpPmuludq_Sse>;
        
        g_Handlers[0x174] = DispatchWrapper<OpPcmpeqb_Sse>;
        g_Handlers[0x175] = DispatchWrapper<OpPcmpeqw_Sse>;
        g_Handlers[0x176] = DispatchWrapper<OpPcmpeqd_Sse>;
        
        g_Handlers[0x164] = DispatchWrapper<OpPcmpgtb_Sse>;
        g_Handlers[0x165] = DispatchWrapper<OpPcmpgtw_Sse>;
        g_Handlers[0x166] = DispatchWrapper<OpPcmpgtd_Sse>;
        
        g_Handlers[0x1DE] = DispatchWrapper<OpPmaxub_Sse>;
        g_Handlers[0x1DA] = DispatchWrapper<OpPminub_Sse>;
        g_Handlers[0x1EE] = DispatchWrapper<OpPmaxsw_Sse>;
        g_Handlers[0x1EA] = DispatchWrapper<OpPminsw_Sse>;
        
        g_Handlers[0x1F1] = DispatchWrapper<OpPsllw_Sse>;
        g_Handlers[0x1F2] = DispatchWrapper<OpPslld_Sse>;
        g_Handlers[0x1F3] = DispatchWrapper<OpPsllq_Sse>;
        
        g_Handlers[0x1E1] = DispatchWrapper<OpPsraw_Sse>;
        g_Handlers[0x1E2] = DispatchWrapper<OpPsrad_Sse>;
        
        g_Handlers[0x1D1] = DispatchWrapper<OpPsrlw_Sse>;
        g_Handlers[0x1D2] = DispatchWrapper<OpPsrld_Sse>;
        g_Handlers[0x1D3] = DispatchWrapper<OpPsrlq_Sse>;
        
        g_Handlers[0x170] = DispatchWrapper<OpGroup_Pshuf>;
        g_Handlers[0x171] = DispatchWrapper<OpGroup_Sse_Shift_Imm_W>;
        g_Handlers[0x172] = DispatchWrapper<OpGroup_Sse_Shift_Imm_D>;
        g_Handlers[0x173] = DispatchWrapper<OpGroup_Sse_Shift_Imm_Q>;
        
        g_Handlers[0x168] = DispatchWrapper<OpPunpckhbw_Sse>;
        g_Handlers[0x169] = DispatchWrapper<OpPunpckhwd_Sse>;
        g_Handlers[0x16A] = DispatchWrapper<OpPunpckhdq_Sse>;
        g_Handlers[0x16D] = DispatchWrapper<OpPunpckhqdq_Sse>;
        
        g_Handlers[0x160] = DispatchWrapper<OpPunpcklbw_Sse>;
        g_Handlers[0x161] = DispatchWrapper<OpPunpcklwd_Sse>;
        g_Handlers[0x162] = DispatchWrapper<OpPunpckldq_Sse>;
        g_Handlers[0x16C] = DispatchWrapper<OpPunpcklqdq_Sse>;
        
        g_Handlers[0x163] = DispatchWrapper<OpPacksswb_Sse>;
        g_Handlers[0x16B] = DispatchWrapper<OpPackssdw_Sse>;
        g_Handlers[0x167] = DispatchWrapper<OpPackuswb_Sse>;
        
        g_Handlers[0x1C5] = DispatchWrapper<OpPextrw_Sse>;
        g_Handlers[0x1C4] = DispatchWrapper<OpPinsrw_Sse>;
        g_Handlers[0x1D7] = DispatchWrapper<OpPmovmskb_Sse>;
        g_Handlers[0x1D6] = DispatchWrapper<OpMovq_Store>; // 66 0F D6: MOVQ xmm/m64, xmm
    }
};

// Static instance to trigger initialization
static HandlerInit _init;

} // namespace x86emu
