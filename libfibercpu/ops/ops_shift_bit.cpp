#include "ops_shift_bit_impl.h"

namespace fiberish {
void RegisterShiftBitOps() {
    using namespace op;

    g_Handlers[0xC0] = DispatchWrapper<OpGroup2_EbIb>;
    g_Handlers[0xC1] = DispatchWrapper<OpGroup2_EvIb>;
    g_Handlers[0xD0] = DispatchWrapper<OpGroup2_Eb1>;
    g_Handlers[0xD1] = DispatchWrapper<OpGroup2_Ev1>;
    g_Handlers[0xD2] = DispatchWrapper<OpGroup2_EbCl>;
    g_Handlers[0xD3] = DispatchWrapper<OpGroup2_EvCl>;

    // Specializations
    // Explicit registration for common cases to ensure they are generated

    // SHL (4)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpGroup2_EvIb_Shl>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Shl>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Shl>::RegisterSpecializedAutoBoth(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb_Shl_ModReg>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Shl_ModReg>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Shl_ModReg>::RegisterSpecializedAutoBoth(0xD3, c);
    }
    // SHR (5)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpGroup2_EvIb_Shr>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Shr>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Shr>::RegisterSpecializedAutoBoth(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb_Shr_ModReg>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Shr_ModReg>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Shr_ModReg>::RegisterSpecializedAutoBoth(0xD3, c);
    }
    // SAR (7)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpGroup2_EvIb_Sar>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Sar>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Sar>::RegisterSpecializedAutoBoth(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb_Sar_ModReg>::RegisterSpecializedAutoBoth(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1_Sar_ModReg>::RegisterSpecializedAutoBoth(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl_Sar_ModReg>::RegisterSpecializedAutoBoth(0xD3, c);
    }

    g_Handlers[0x1A3] = DispatchWrapper<OpBt_Reg>;
    g_Handlers[0x1AB] = DispatchWrapper<OpBts_Reg>;  // 0F AB
    g_Handlers[0x1B3] = DispatchWrapper<OpBtr_Reg>;
    g_Handlers[0x1BB] = DispatchWrapper<OpBtc_Reg>;      // 0F BB
    g_Handlers[0x1BA] = DispatchWrapper<OpGroup8_EvIb>;  // 0F BA (All subops)
    g_Handlers[0x1BD] = DispatchWrapper<OpBsr_GvEv>;
    g_Handlers[0x1BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;  // 0F BC: BSF
    g_Handlers[0x2BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x1C8 + i] = DispatchWrapper<OpBswap_Reg>;
    }
}

}  // namespace fiberish
