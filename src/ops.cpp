#include "ops.h"
#include "dispatch.h"

namespace x86emu {

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
        
        // 2. Set MOV
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
        g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel>; // JMP rel32
        g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel>; // JMP rel8
        g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>; // CALL rel32
        g_Handlers[0xC3] = DispatchWrapper<OpRet>;      // RET
        g_Handlers[0xCD] = DispatchWrapper<OpInt>;      // INT imm8
        g_Handlers[0xCC] = DispatchWrapper<OpInt3>;     // INT3
        
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
        
        // Moves
        g_Handlers[0x110] = DispatchWrapper<OpMov_Sse_Load>;
        g_Handlers[0x111] = DispatchWrapper<OpMov_Sse_Store>;
        g_Handlers[0x16E] = DispatchWrapper<OpMovd_Load>;
        g_Handlers[0x17E] = DispatchWrapper<OpMovd_Store>;
        
        g_Handlers[0x16F] = DispatchWrapper<OpMovq_Load>;
        g_Handlers[0x17F] = DispatchWrapper<OpMovq_Store>;
        
        // XADD
        g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Rm_R>;
        g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Rm_R>;
    }
};

// Static instance to trigger initialization
static HandlerInit _init;

} // namespace x86emu
