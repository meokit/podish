#include "ops_data_mov_impl.h"

namespace fiberish {
void RegisterDataMovOps() {
    using namespace op;

    g_Handlers[0x87] = DispatchWrapper<OpXchg_EvGv>;  // XCHG r/m32, r32
    g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
    g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;

    // Specialized 32-bit MOV
    g_Handlers[OP_MOV_RR_STORE] = DispatchWrapper<OpMov_EvGv_Reg>;
    g_Handlers[OP_MOV_RM_STORE] = DispatchWrapper<OpMov_EvGv_Mem>;
    g_Handlers[OP_MOV_RR_LOAD] = DispatchWrapper<OpMov_GvEv_Reg>;
    g_Handlers[OP_MOV_MR_LOAD] = DispatchWrapper<OpMov_GvEv_Mem>;

    // Register Specialized Load/Store helpers
    // Store (EvGv_Mem)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        DispatchRegistrar<OpMov_Store_Eax>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        DispatchRegistrar<OpMov_Store_Ecx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        DispatchRegistrar<OpMov_Store_Edx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        DispatchRegistrar<OpMov_Store_Ebx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpMov_Store_Esp>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpMov_Store_Ebp>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        DispatchRegistrar<OpMov_Store_Esi>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpMov_Store_Edi>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }

    // Load (GvEv_Mem)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        DispatchRegistrar<OpMov_Load_Eax>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        DispatchRegistrar<OpMov_Load_Ecx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        DispatchRegistrar<OpMov_Load_Edx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        DispatchRegistrar<OpMov_Load_Ebx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpMov_Load_Esp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpMov_Load_Ebp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        DispatchRegistrar<OpMov_Load_Esi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpMov_Load_Edi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }

    // Key MOV Patterns Specialization
    // 1. MOV EBP, ESP
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        c.rm_mask = 7;
        c.rm_val = 4;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Ebp_Esp>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }
    // 2. MOV ECX, EAX
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        c.rm_mask = 7;
        c.rm_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Ecx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }
    // 3. MOV EDX, EAX
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        c.rm_mask = 7;
        c.rm_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Edx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // EvGv_Reg Specializations (Dst=Reg, Src=Reg) - Store Reg to Reg?
    // OpMov_EvGv_Reg is for 0x89 (MOV r/m, r) -> if mod=3, it's Reg -> Reg.
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Eax>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ecx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Edx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ebx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Esp>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ebp>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Esi>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Edi>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }

    g_Handlers[0x88] = DispatchWrapper<OpMov_EbGb>;  // MOV r/m8, r8
    g_Handlers[0x8A] = DispatchWrapper<OpMov_GbEb>;  // MOV r8, r/m8
    g_Handlers[0xC6] = DispatchWrapper<OpMov_EbIb>;  // MOV r/m8, imm8
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0xB0 + i] = DispatchWrapper<OpMov_RegImm8>;
        g_Handlers[0xB8 + i] = DispatchWrapper<OpMov_RegImm>;
    }
    g_Handlers[0xC7] = DispatchWrapper<OpMov_EvIz>;  // MOV r/m32, imm32
    g_Handlers[0xA0] = DispatchWrapper<OpMov_Moffs_Load_Byte>;
    g_Handlers[0xA1] = DispatchWrapper<OpMov_Moffs_Load_Word>;
    g_Handlers[0xA2] = DispatchWrapper<OpMov_Moffs_Store_Byte>;
    g_Handlers[0xA3] = DispatchWrapper<OpMov_Moffs_Store_Word>;
    g_Handlers[0xA4] = DispatchWrapper<OpMovs_Byte>;
    g_Handlers[0xA5] = DispatchWrapper<OpMovs_Word>;
    g_Handlers[0xAA] = DispatchWrapper<OpStos_Byte>;
    g_Handlers[0xAB] = DispatchWrapper<OpStos_Word>;
    g_Handlers[0xAC] = DispatchWrapper<OpLods_Byte>;
    g_Handlers[0xAD] = DispatchWrapper<OpLods_Word>;
    g_Handlers[0xAE] = DispatchWrapper<OpScas_Byte>;
    g_Handlers[0xAF] = DispatchWrapper<OpScas_Word>;
    g_Handlers[0xA6] = DispatchWrapper<OpCmps_Byte>;
    g_Handlers[0xA7] = DispatchWrapper<OpCmps_Word>;
    g_Handlers[0x60] = DispatchWrapper<OpPusha>;
    g_Handlers[0x61] = DispatchWrapper<OpPopa>;
    g_Handlers[0xC8] = DispatchWrapper<OpEnter>;
    g_Handlers[0xC9] = DispatchWrapper<OpLeave>;
    g_Handlers[0x86] = DispatchWrapper<OpXchg_EbGb>;
    for (int i = 1; i < 8; ++i) g_Handlers[0x90 + i] = DispatchWrapper<OpXchg_Reg>;
    g_Handlers[0x9F] = DispatchWrapper<OpLahf>;
    g_Handlers[0x9E] = DispatchWrapper<OpSahf>;
    g_Handlers[0x8D] = DispatchWrapper<OpLea>;
    {
        SpecCriteria c16;
        c16.prefix_mask = prefix::OPSIZE;
        c16.prefix_val = prefix::OPSIZE;
        DispatchRegistrar<OpLea_16>::RegisterSpecialized(0x8D, c16);
    }
    {
        SpecCriteria c32;
        c32.prefix_mask = prefix::OPSIZE;
        c32.prefix_val = 0;
        DispatchRegistrar<OpLea_32>::RegisterSpecialized(0x8D, c32);
    }
    g_Handlers[0x50] = DispatchWrapper<OpPush_Reg32_Eax>;
    g_Handlers[0x51] = DispatchWrapper<OpPush_Reg32_Ecx>;
    g_Handlers[0x52] = DispatchWrapper<OpPush_Reg32_Edx>;
    g_Handlers[0x53] = DispatchWrapper<OpPush_Reg32_Ebx>;
    g_Handlers[0x54] = DispatchWrapper<OpPush_Reg32_Esp>;
    g_Handlers[0x55] = DispatchWrapper<OpPush_Reg32_Ebp>;
    g_Handlers[0x56] = DispatchWrapper<OpPush_Reg32_Esi>;
    g_Handlers[0x57] = DispatchWrapper<OpPush_Reg32_Edi>;
    g_Handlers[0x68] = DispatchWrapper<OpPush_Imm32>;
    g_Handlers[0x6A] = DispatchWrapper<OpPush_Imm8>;
    g_Handlers[0x58] = DispatchWrapper<OpPop_Reg32_Eax>;
    g_Handlers[0x59] = DispatchWrapper<OpPop_Reg32_Ecx>;
    g_Handlers[0x5A] = DispatchWrapper<OpPop_Reg32_Edx>;
    g_Handlers[0x5B] = DispatchWrapper<OpPop_Reg32_Ebx>;
    g_Handlers[0x5C] = DispatchWrapper<OpPop_Reg32_Esp>;
    g_Handlers[0x5D] = DispatchWrapper<OpPop_Reg32_Ebp>;
    g_Handlers[0x5E] = DispatchWrapper<OpPop_Reg32_Esi>;
    g_Handlers[0x5F] = DispatchWrapper<OpPop_Reg32_Edi>;
    g_Handlers[0x8C] = DispatchWrapper<OpMov_Rm_Sreg>;
    g_Handlers[0x8E] = DispatchWrapper<OpMov_Sreg_Rm>;
    g_Handlers[0x8F] = DispatchWrapper<OpPop_Ev>;
    g_Handlers[0x1B6] = DispatchWrapper<OpMovzx_Byte>;
    g_Handlers[0x1B7] = DispatchWrapper<OpMovzx_Word>;
    g_Handlers[0x1BE] = DispatchWrapper<OpMovsx_Byte>;
    g_Handlers[0x1BF] = DispatchWrapper<OpMovsx_Word>;
    g_Handlers[0xD7] = DispatchWrapper<OpXlat>;

    SpecCriteria c16;
    c16.prefix_mask = prefix::OPSIZE;
    c16.prefix_val = prefix::OPSIZE;

    DispatchRegistrar<OpPush_Reg16_Eax>::RegisterSpecialized(0x50, c16);
    DispatchRegistrar<OpPush_Reg16_Ecx>::RegisterSpecialized(0x51, c16);
    DispatchRegistrar<OpPush_Reg16_Edx>::RegisterSpecialized(0x52, c16);
    DispatchRegistrar<OpPush_Reg16_Ebx>::RegisterSpecialized(0x53, c16);
    DispatchRegistrar<OpPush_Reg16_Esp>::RegisterSpecialized(0x54, c16);
    DispatchRegistrar<OpPush_Reg16_Ebp>::RegisterSpecialized(0x55, c16);
    DispatchRegistrar<OpPush_Reg16_Esi>::RegisterSpecialized(0x56, c16);
    DispatchRegistrar<OpPush_Reg16_Edi>::RegisterSpecialized(0x57, c16);

    DispatchRegistrar<OpPop_Reg16_Eax>::RegisterSpecialized(0x58, c16);
    DispatchRegistrar<OpPop_Reg16_Ecx>::RegisterSpecialized(0x59, c16);
    DispatchRegistrar<OpPop_Reg16_Edx>::RegisterSpecialized(0x5A, c16);
    DispatchRegistrar<OpPop_Reg16_Ebx>::RegisterSpecialized(0x5B, c16);
    DispatchRegistrar<OpPop_Reg16_Esp>::RegisterSpecialized(0x5C, c16);
    DispatchRegistrar<OpPop_Reg16_Ebp>::RegisterSpecialized(0x5D, c16);
    DispatchRegistrar<OpPop_Reg16_Esi>::RegisterSpecialized(0x5E, c16);
    DispatchRegistrar<OpPop_Reg16_Edi>::RegisterSpecialized(0x5F, c16);
}

}  // namespace fiberish
