#include "ops_alu_impl.h"

namespace fiberish {
void RegisterAluOps() {
    using namespace op;

    g_Handlers[0x00] = DispatchWrapper<OpAdd_EbGb>;
    DispatchRegistrar<OpAdd_EbGb_NF>::RegisterNF(0x00);

    // 01: ADD r/m16/32, r16/32
    DispatchRegistrar<OpAdd_EvGv>::Register(0x01);
    DispatchRegistrar<OpAdd_EvGv_NF>::RegisterNF(0x01);

    // Specialization: ADD EAX, r32 (Mod=3, RM=0)
    // OpAdd_EvGv_Eax
    SpecCriteria criteria;
    criteria.mod_mask = 0xC0;
    criteria.mod_val = 0xC0;  // Mod=3 (Reg)
    criteria.rm_mask = 0x07;
    criteria.rm_val = 0x00;  // RM=0 (EAX)
    DispatchRegistrar<OpAdd_EvGv_Eax>::RegisterSpecialized(0x01, criteria);

    g_Handlers[0x02] = DispatchWrapper<OpAdd_GbEb>;
    DispatchRegistrar<OpAdd_GbEb_NF>::RegisterNF(0x02);

    g_Handlers[0x03] = DispatchWrapper<OpAdd_GvEv>;
    DispatchRegistrar<OpAdd_GvEv_NF>::RegisterNF(0x03);

    g_Handlers[0x04] = DispatchWrapper<OpAdd_AlImm>;
    DispatchRegistrar<OpAdd_AlImm_NF>::RegisterNF(0x04);

    g_Handlers[0x05] = DispatchWrapper<OpAdd_EaxImm>;
    DispatchRegistrar<OpAdd_EaxImm_NF>::RegisterNF(0x05);

    g_Handlers[0x08] = DispatchWrapper<OpOr_EbGb>;
    DispatchRegistrar<OpOr_EbGb_NF>::RegisterNF(0x08);

    g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv>;
    DispatchRegistrar<OpOr_EvGv_NF>::RegisterNF(0x09);

    g_Handlers[0x0A] = DispatchWrapper<OpOr_GbEb>;
    DispatchRegistrar<OpOr_GbEb_NF>::RegisterNF(0x0A);

    g_Handlers[0x0B] = DispatchWrapper<OpOr_GvEv>;
    DispatchRegistrar<OpOr_GvEv_NF>::RegisterNF(0x0B);

    g_Handlers[0x0C] = DispatchWrapper<OpOr_AlImm>;
    DispatchRegistrar<OpOr_AlImm_NF>::RegisterNF(0x0C);

    g_Handlers[0x0D] = DispatchWrapper<OpOr_EaxImm>;
    DispatchRegistrar<OpOr_EaxImm_NF>::RegisterNF(0x0D);

    // ADC/SBB
    g_Handlers[0x10] = DispatchWrapper<OpAdc_EbGb>;
    DispatchRegistrar<OpAdc_EbGb_NF>::RegisterNF(0x10);

    g_Handlers[0x11] = DispatchWrapper<OpAdc_EvGv>;
    DispatchRegistrar<OpAdc_EvGv_NF>::RegisterNF(0x11);

    g_Handlers[0x12] = DispatchWrapper<OpAdc_GbEb>;
    DispatchRegistrar<OpAdc_GbEb_NF>::RegisterNF(0x12);

    g_Handlers[0x13] = DispatchWrapper<OpAdc_GvEv>;
    DispatchRegistrar<OpAdc_GvEv_NF>::RegisterNF(0x13);

    g_Handlers[0x14] = DispatchWrapper<OpAdc_AlImm>;
    DispatchRegistrar<OpAdc_AlImm_NF>::RegisterNF(0x14);

    g_Handlers[0x15] = DispatchWrapper<OpAdc_EaxImm>;
    DispatchRegistrar<OpAdc_EaxImm_NF>::RegisterNF(0x15);

    g_Handlers[0x18] = DispatchWrapper<OpSbb_EbGb>;
    DispatchRegistrar<OpSbb_EbGb_NF>::RegisterNF(0x18);

    g_Handlers[0x19] = DispatchWrapper<OpSbb_EvGv>;
    DispatchRegistrar<OpSbb_EvGv_NF>::RegisterNF(0x19);

    g_Handlers[0x1A] = DispatchWrapper<OpSbb_GbEb>;
    DispatchRegistrar<OpSbb_GbEb_NF>::RegisterNF(0x1A);

    g_Handlers[0x1B] = DispatchWrapper<OpSbb_GvEv>;
    DispatchRegistrar<OpSbb_GvEv_NF>::RegisterNF(0x1B);

    g_Handlers[0x1C] = DispatchWrapper<OpSbb_AlImm>;
    DispatchRegistrar<OpSbb_AlImm_NF>::RegisterNF(0x1C);

    g_Handlers[0x1D] = DispatchWrapper<OpSbb_EaxImm>;
    DispatchRegistrar<OpSbb_EaxImm_NF>::RegisterNF(0x1D);

    g_Handlers[0x20] = DispatchWrapper<OpAnd_EbGb>;
    DispatchRegistrar<OpAnd_EbGb_NF>::RegisterNF(0x20);

    g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv>;
    DispatchRegistrar<OpAnd_EvGv_NF>::RegisterNF(0x21);

    g_Handlers[0x22] = DispatchWrapper<OpAnd_GbEb>;
    DispatchRegistrar<OpAnd_GbEb_NF>::RegisterNF(0x22);

    g_Handlers[0x23] = DispatchWrapper<OpAnd_GvEv>;
    DispatchRegistrar<OpAnd_GvEv_NF>::RegisterNF(0x23);

    g_Handlers[0x24] = DispatchWrapper<OpAnd_AlImm>;
    DispatchRegistrar<OpAnd_AlImm_NF>::RegisterNF(0x24);

    g_Handlers[0x25] = DispatchWrapper<OpAnd_EaxImm>;
    DispatchRegistrar<OpAnd_EaxImm_NF>::RegisterNF(0x25);

    g_Handlers[0x28] = DispatchWrapper<OpSub_EbGb>;
    DispatchRegistrar<OpSub_EbGb_NF>::RegisterNF(0x28);

    g_Handlers[0x29] = DispatchWrapper<OpSub_EvGv>;
    DispatchRegistrar<OpSub_EvGv_NF>::RegisterNF(0x29);

    g_Handlers[0x2A] = DispatchWrapper<OpSub_GbEb>;
    DispatchRegistrar<OpSub_GbEb_NF>::RegisterNF(0x2A);

    g_Handlers[0x2B] = DispatchWrapper<OpSub_GvEv>;
    DispatchRegistrar<OpSub_GvEv_NF>::RegisterNF(0x2B);

    g_Handlers[0x2C] = DispatchWrapper<OpSub_AlImm>;
    DispatchRegistrar<OpSub_AlImm_NF>::RegisterNF(0x2C);

    g_Handlers[0x2D] = DispatchWrapper<OpSub_EaxImm>;
    DispatchRegistrar<OpSub_EaxImm_NF>::RegisterNF(0x2D);

    g_Handlers[0x30] = DispatchWrapper<OpXor_EbGb>;
    DispatchRegistrar<OpXor_EbGb_NF>::RegisterNF(0x30);

    g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv>;
    DispatchRegistrar<OpXor_EvGv_NF>::RegisterNF(0x31);

    g_Handlers[0x32] = DispatchWrapper<OpXor_GbEb>;
    DispatchRegistrar<OpXor_GbEb_NF>::RegisterNF(0x32);

    g_Handlers[0x33] = DispatchWrapper<OpXor_GvEv>;
    DispatchRegistrar<OpXor_GvEv_NF>::RegisterNF(0x33);

    g_Handlers[0x34] = DispatchWrapper<OpXor_AlImm>;
    DispatchRegistrar<OpXor_AlImm_NF>::RegisterNF(0x34);

    g_Handlers[0x35] = DispatchWrapper<OpXor_EaxImm>;
    DispatchRegistrar<OpXor_EaxImm_NF>::RegisterNF(0x35);

    g_Handlers[0x3C] = DispatchWrapper<OpCmp_AlImm>;
    g_Handlers[0x3D] = DispatchWrapper<OpCmp_EaxImm>;
    g_Handlers[0x84] = DispatchWrapper<OpTest_EbGb>;
    g_Handlers[0xA8] = DispatchWrapper<OpTest_AlImm>;
    g_Handlers[0xA9] = DispatchWrapper<OpTest_EaxImm>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x40 + i] = DispatchWrapper<OpInc_Reg>;
        DispatchRegistrar<OpInc_Reg_NF>::RegisterNF(0x40 + i);

        g_Handlers[0x48 + i] = DispatchWrapper<OpDec_Reg>;
        DispatchRegistrar<OpDec_Reg_NF>::RegisterNF(0x48 + i);
    }

    g_Handlers[0x27] = DispatchWrapper<OpDaa>;
    g_Handlers[0x2F] = DispatchWrapper<OpDas>;
    g_Handlers[0x37] = DispatchWrapper<OpAaa>;
    g_Handlers[0x3F] = DispatchWrapper<OpAas>;
    g_Handlers[0xD4] = DispatchWrapper<OpAam>;
    g_Handlers[0xD5] = DispatchWrapper<OpAad>;
}
}  // namespace fiberish