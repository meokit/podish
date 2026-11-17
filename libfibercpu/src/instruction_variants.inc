// One declaration per selected variant. LegacyBinding is the compatibility adapter.
// All matcher constraints and tool metadata are derived from these declarations.
// Last field preserves the pre-refactor precedence among equal-specificity rules.


// Handler index 0x000
INSTRUCTION_VARIANT(OpAdd_EbGb_NF, Add_EbGb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x001
INSTRUCTION_FUSION_VARIANT(OpAdd_EvGv_Eax, Add_EvGv, Spec{}.regForm().dst(Gpr::Eax), OpAdd_EvGv, 0)
INSTRUCTION_VARIANT(OpAdd_EvGv_NF_32_ModReg, Add_EvGv, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpAdd_EvGv_NF, Add_EvGv, Spec{}.flags(Flags::Dead), 2)

// Handler index 0x002
INSTRUCTION_VARIANT(OpAdd_GbEb_NF, Add_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x003
INSTRUCTION_VARIANT(OpAdd_GvEv_NF, Add_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x004
INSTRUCTION_VARIANT(OpAdd_AlImm_NF, Add_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x005
INSTRUCTION_VARIANT(OpAdd_EaxImm_NF, Add_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x008
INSTRUCTION_VARIANT(OpOr_EbGb_NF, Or_EbGb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x009
INSTRUCTION_VARIANT(OpOr_EvGv_NF_32_ModReg, Or_EvGv, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 0)
INSTRUCTION_VARIANT(OpOr_EvGv_NF, Or_EvGv, Spec{}.flags(Flags::Dead), 1)

// Handler index 0x00A
INSTRUCTION_VARIANT(OpOr_GbEb_NF, Or_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x00B
INSTRUCTION_VARIANT(OpOr_GvEv_NF, Or_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x00C
INSTRUCTION_VARIANT(OpOr_AlImm_NF, Or_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x00D
INSTRUCTION_VARIANT(OpOr_EaxImm_NF, Or_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x010
INSTRUCTION_VARIANT(OpAdc_EbGb_ModReg, Adc_EbGb, Spec{}.regForm(), 0)
INSTRUCTION_VARIANT(OpAdc_EbGb_NF_ModReg, Adc_EbGb, Spec{}.regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpAdc_EbGb_NF, Adc_EbGb, Spec{}.flags(Flags::Dead), 2)

// Handler index 0x011
INSTRUCTION_VARIANT(OpAdc_EvGv_ModReg, Adc_EvGv, Spec{}.regForm(), 0)
INSTRUCTION_VARIANT(OpAdc_EvGv_NF_ModReg, Adc_EvGv, Spec{}.regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpAdc_EvGv_NF, Adc_EvGv, Spec{}.flags(Flags::Dead), 2)

// Handler index 0x012
INSTRUCTION_VARIANT(OpAdc_GbEb_NF, Adc_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x013
INSTRUCTION_VARIANT(OpAdc_GvEv_NF, Adc_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x014
INSTRUCTION_VARIANT(OpAdc_AlImm_NF, Adc_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x015
INSTRUCTION_VARIANT(OpAdc_EaxImm_NF, Adc_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x018
INSTRUCTION_VARIANT(OpSbb_EbGb_ModReg, Sbb_EbGb, Spec{}.regForm(), 0)
INSTRUCTION_VARIANT(OpSbb_EbGb_NF_ModReg, Sbb_EbGb, Spec{}.regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpSbb_EbGb_NF, Sbb_EbGb, Spec{}.flags(Flags::Dead), 2)

// Handler index 0x019
INSTRUCTION_VARIANT(OpSbb_EvGv_ModReg, Sbb_EvGv, Spec{}.regForm(), 0)
INSTRUCTION_VARIANT(OpSbb_EvGv_NF_ModReg, Sbb_EvGv, Spec{}.regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpSbb_EvGv_NF, Sbb_EvGv, Spec{}.flags(Flags::Dead), 2)

// Handler index 0x01A
INSTRUCTION_VARIANT(OpSbb_GbEb_NF, Sbb_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x01B
INSTRUCTION_VARIANT(OpSbb_GvEv_NF, Sbb_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x01C
INSTRUCTION_VARIANT(OpSbb_AlImm_NF, Sbb_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x01D
INSTRUCTION_VARIANT(OpSbb_EaxImm_NF, Sbb_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x020
INSTRUCTION_VARIANT(OpAnd_EbGb_NF, And_EbGb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x021
INSTRUCTION_VARIANT(OpAnd_EvGv_NF_32_ModReg, And_EvGv, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 0)
INSTRUCTION_VARIANT(OpAnd_EvGv_NF, And_EvGv, Spec{}.flags(Flags::Dead), 1)

// Handler index 0x022
INSTRUCTION_VARIANT(OpAnd_GbEb_NF, And_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x023
INSTRUCTION_VARIANT(OpAnd_GvEv_NF, And_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x024
INSTRUCTION_VARIANT(OpAnd_AlImm_NF, And_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x025
INSTRUCTION_VARIANT(OpAnd_EaxImm_NF, And_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x028
INSTRUCTION_VARIANT(OpSub_EbGb_NF, Sub_EbGb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x029
INSTRUCTION_VARIANT(OpSub_EvGv_NF_32_ModReg, Sub_EvGv, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 0)
INSTRUCTION_VARIANT(OpSub_EvGv_NF, Sub_EvGv, Spec{}.flags(Flags::Dead), 1)

// Handler index 0x02A
INSTRUCTION_VARIANT(OpSub_GbEb_NF, Sub_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x02B
INSTRUCTION_VARIANT(OpSub_GvEv_NF, Sub_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x02C
INSTRUCTION_VARIANT(OpSub_AlImm_NF, Sub_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x02D
INSTRUCTION_VARIANT(OpSub_EaxImm_NF, Sub_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x030
INSTRUCTION_VARIANT(OpXor_EbGb_NF, Xor_EbGb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x031
INSTRUCTION_VARIANT(OpXor_EvGv_NF_32_ModReg, Xor_EvGv, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 0)
INSTRUCTION_VARIANT(OpXor_EvGv_NF, Xor_EvGv, Spec{}.flags(Flags::Dead), 1)

// Handler index 0x032
INSTRUCTION_VARIANT(OpXor_GbEb_NF, Xor_GbEb, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x033
INSTRUCTION_VARIANT(OpXor_GvEv_NF, Xor_GvEv, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x034
INSTRUCTION_VARIANT(OpXor_AlImm_NF, Xor_AlImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x035
INSTRUCTION_VARIANT(OpXor_EaxImm_NF, Xor_EaxImm, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x039
INSTRUCTION_VARIANT(OpCmp_EvGv_32_ModReg_Eax, Cmp_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Eax).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpCmp_EvGv_32_ModReg_Ecx, Cmp_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Ecx).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpCmp_EvGv_32_ModReg_Edx, Cmp_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Edx).dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpCmp_EvGv_32_ModReg, Cmp_EvGv, Spec{}.width(Width::W32).regForm(), 3)
INSTRUCTION_VARIANT(OpCmp_EvGv_16, Cmp_EvGv, Spec{}.width(Width::W16), 4)
INSTRUCTION_VARIANT(OpCmp_EvGv_32, Cmp_EvGv, Spec{}.width(Width::W32), 5)

// Handler index 0x03B
INSTRUCTION_VARIANT(OpCmp_GvEv_16, Cmp_GvEv, Spec{}.width(Width::W16), 0)
INSTRUCTION_VARIANT(OpCmp_GvEv_32, Cmp_GvEv, Spec{}.width(Width::W32), 1)

// Handler index 0x040
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_040, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x041
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_041, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x042
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_042, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x043
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_043, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x044
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_044, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x045
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_045, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x046
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_046, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x047
INSTRUCTION_VARIANT(OpInc_Reg_NF, Inc_Reg_047, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x048
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_048, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x049
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_049, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04A
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04A, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04B
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04B, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04C
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04C, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04D
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04D, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04E
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04E, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x04F
INSTRUCTION_VARIANT(OpDec_Reg_NF, Dec_Reg_04F, Spec{}.flags(Flags::Dead), 0)

// Handler index 0x050
INSTRUCTION_VARIANT(OpPush_Reg16_Eax, Push_Reg32_Eax, Spec{}.width(Width::W16), 0)

// Handler index 0x051
INSTRUCTION_VARIANT(OpPush_Reg16_Ecx, Push_Reg32_Ecx, Spec{}.width(Width::W16), 0)

// Handler index 0x052
INSTRUCTION_VARIANT(OpPush_Reg16_Edx, Push_Reg32_Edx, Spec{}.width(Width::W16), 0)

// Handler index 0x053
INSTRUCTION_VARIANT(OpPush_Reg16_Ebx, Push_Reg32_Ebx, Spec{}.width(Width::W16), 0)

// Handler index 0x054
INSTRUCTION_VARIANT(OpPush_Reg16_Esp, Push_Reg32_Esp, Spec{}.width(Width::W16), 0)

// Handler index 0x055
INSTRUCTION_VARIANT(OpPush_Reg16_Ebp, Push_Reg32_Ebp, Spec{}.width(Width::W16), 0)

// Handler index 0x056
INSTRUCTION_VARIANT(OpPush_Reg16_Esi, Push_Reg32_Esi, Spec{}.width(Width::W16), 0)

// Handler index 0x057
INSTRUCTION_VARIANT(OpPush_Reg16_Edi, Push_Reg32_Edi, Spec{}.width(Width::W16), 0)

// Handler index 0x058
INSTRUCTION_VARIANT(OpPop_Reg16_Eax, Pop_Reg32_Eax, Spec{}.width(Width::W16), 0)

// Handler index 0x059
INSTRUCTION_VARIANT(OpPop_Reg16_Ecx, Pop_Reg32_Ecx, Spec{}.width(Width::W16), 0)

// Handler index 0x05A
INSTRUCTION_VARIANT(OpPop_Reg16_Edx, Pop_Reg32_Edx, Spec{}.width(Width::W16), 0)

// Handler index 0x05B
INSTRUCTION_VARIANT(OpPop_Reg16_Ebx, Pop_Reg32_Ebx, Spec{}.width(Width::W16), 0)

// Handler index 0x05C
INSTRUCTION_VARIANT(OpPop_Reg16_Esp, Pop_Reg32_Esp, Spec{}.width(Width::W16), 0)

// Handler index 0x05D
INSTRUCTION_VARIANT(OpPop_Reg16_Ebp, Pop_Reg32_Ebp, Spec{}.width(Width::W16), 0)

// Handler index 0x05E
INSTRUCTION_VARIANT(OpPop_Reg16_Esi, Pop_Reg32_Esi, Spec{}.width(Width::W16), 0)

// Handler index 0x05F
INSTRUCTION_VARIANT(OpPop_Reg16_Edi, Pop_Reg32_Edi, Spec{}.width(Width::W16), 0)

// Handler index 0x080
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg0_Flags, Group1_EbIb_Cmp_080, Spec{}.regForm().dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags, Group1_EbIb_Cmp_080, Spec{}.regForm().dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, Group1_EbIb_Cmp_080, Spec{}.regForm().dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags, Group1_EbIb_Cmp_080, Spec{}.regForm().dst(Gpr::Ebx), 3)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Flags, Group1_EbIb_Cmp_080, Spec{}.regForm(), 4)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Add_Flags, Group1_EbIb_Add_080, Spec{}, 5)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Add_NoFlags, Group1_EbIb_Add_080, Spec{}.flags(Flags::Dead), 6)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Or_Flags, Group1_EbIb_Or_080, Spec{}, 7)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Or_NoFlags, Group1_EbIb_Or_080, Spec{}.flags(Flags::Dead), 8)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Adc_Flags, Group1_EbIb_Adc_080, Spec{}, 9)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Adc_NoFlags, Group1_EbIb_Adc_080, Spec{}.flags(Flags::Dead), 10)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Sbb_Flags, Group1_EbIb_Sbb_080, Spec{}, 11)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Sbb_NoFlags, Group1_EbIb_Sbb_080, Spec{}.flags(Flags::Dead), 12)
INSTRUCTION_VARIANT(OpGroup1_EbIb_And_Flags, Group1_EbIb_And_080, Spec{}, 13)
INSTRUCTION_VARIANT(OpGroup1_EbIb_And_NoFlags, Group1_EbIb_And_080, Spec{}.flags(Flags::Dead), 14)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Sub_Flags, Group1_EbIb_Sub_080, Spec{}, 15)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Sub_NoFlags, Group1_EbIb_Sub_080, Spec{}.flags(Flags::Dead), 16)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Xor_Flags, Group1_EbIb_Xor_080, Spec{}, 17)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Xor_NoFlags, Group1_EbIb_Xor_080, Spec{}.flags(Flags::Dead), 18)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_Flags, Group1_EbIb_Cmp_080, Spec{}, 19)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_NoFlags, Group1_EbIb_Cmp_080, Spec{}.flags(Flags::Dead), 20)

// Handler index 0x081
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_32_Flags_Edx, Group1_EvIz_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 0)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_Flags_Eax, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 1)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_NoFlags_Eax, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax).flags(Flags::Dead), 2)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_Flags_Eax, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 3)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_NoFlags_Eax, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax).flags(Flags::Dead), 4)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_32_Flags_Eax, Group1_EvIz_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 5)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_Flags_Ecx, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 6)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_NoFlags_Ecx, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx).flags(Flags::Dead), 7)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_Flags_Ecx, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 8)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_NoFlags_Ecx, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx).flags(Flags::Dead), 9)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_32_Flags_Ecx, Group1_EvIz_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 10)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_Flags_Edx, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 11)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_NoFlags_Edx, Group1_EvIz_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx).flags(Flags::Dead), 12)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_Flags_Edx, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 13)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_NoFlags_Edx, Group1_EvIz_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx).flags(Flags::Dead), 14)
INSTRUCTION_VARIANT(OpGroup1_EvIz_And_32_Flags, Group1_EvIz_And, Spec{}.width(Width::W32), 15)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_Flags, Group1_EvIz_Add, Spec{}.width(Width::W32), 16)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_16_NoFlags, Group1_EvIz_Add, Spec{}.width(Width::W16).flags(Flags::Dead), 17)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Or_32_Flags, Group1_EvIz_Or, Spec{}.width(Width::W32), 18)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Or_32_NoFlags, Group1_EvIz_Or, Spec{}.width(Width::W32).flags(Flags::Dead), 19)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Or_16_Flags, Group1_EvIz_Or, Spec{}.width(Width::W16), 20)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Or_16_NoFlags, Group1_EvIz_Or, Spec{}.width(Width::W16).flags(Flags::Dead), 21)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Adc_32_Flags, Group1_EvIz_Adc, Spec{}.width(Width::W32), 22)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Adc_32_NoFlags, Group1_EvIz_Adc, Spec{}.width(Width::W32).flags(Flags::Dead), 23)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Xor_32_Flags, Group1_EvIz_Xor, Spec{}.width(Width::W32), 24)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Xor_32_NoFlags, Group1_EvIz_Xor, Spec{}.width(Width::W32).flags(Flags::Dead), 25)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Xor_16_Flags, Group1_EvIz_Xor, Spec{}.width(Width::W16), 26)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Xor_16_NoFlags, Group1_EvIz_Xor, Spec{}.width(Width::W16).flags(Flags::Dead), 27)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_32_Flags, Group1_EvIz_Cmp, Spec{}.width(Width::W32), 28)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_32_NoFlags, Group1_EvIz_Cmp, Spec{}.width(Width::W32).flags(Flags::Dead), 29)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_16_Flags, Group1_EvIz_Cmp, Spec{}.width(Width::W16), 30)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Cmp_16_NoFlags, Group1_EvIz_Cmp, Spec{}.width(Width::W16).flags(Flags::Dead), 31)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_16_Flags, Group1_EvIz_Add, Spec{}.width(Width::W16), 32)
INSTRUCTION_VARIANT(OpGroup1_EvIz_And_32_NoFlags, Group1_EvIz_And, Spec{}.width(Width::W32).flags(Flags::Dead), 33)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Add_32_NoFlags, Group1_EvIz_Add, Spec{}.width(Width::W32).flags(Flags::Dead), 34)
INSTRUCTION_VARIANT(OpGroup1_EvIz_And_16_Flags, Group1_EvIz_And, Spec{}.width(Width::W16), 35)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sbb_16_NoFlags, Group1_EvIz_Sbb, Spec{}.width(Width::W16).flags(Flags::Dead), 36)
INSTRUCTION_VARIANT(OpGroup1_EvIz_And_16_NoFlags, Group1_EvIz_And, Spec{}.width(Width::W16).flags(Flags::Dead), 37)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sbb_16_Flags, Group1_EvIz_Sbb, Spec{}.width(Width::W16), 38)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_Flags, Group1_EvIz_Sub, Spec{}.width(Width::W32), 39)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sbb_32_NoFlags, Group1_EvIz_Sbb, Spec{}.width(Width::W32).flags(Flags::Dead), 40)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_32_NoFlags, Group1_EvIz_Sub, Spec{}.width(Width::W32).flags(Flags::Dead), 41)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sbb_32_Flags, Group1_EvIz_Sbb, Spec{}.width(Width::W32), 42)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_16_Flags, Group1_EvIz_Sub, Spec{}.width(Width::W16), 43)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Adc_16_NoFlags, Group1_EvIz_Adc, Spec{}.width(Width::W16).flags(Flags::Dead), 44)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Sub_16_NoFlags, Group1_EvIz_Sub, Spec{}.width(Width::W16).flags(Flags::Dead), 45)
INSTRUCTION_VARIANT(OpGroup1_EvIz_Adc_16_Flags, Group1_EvIz_Adc, Spec{}.width(Width::W16), 46)

// Handler index 0x082
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg0_Flags, Group1_EbIb_Cmp_082, Spec{}.regForm().dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags, Group1_EbIb_Cmp_082, Spec{}.regForm().dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, Group1_EbIb_Cmp_082, Spec{}.regForm().dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags, Group1_EbIb_Cmp_082, Spec{}.regForm().dst(Gpr::Ebx), 3)
INSTRUCTION_VARIANT(OpGroup1_EbIb_Cmp_ModReg_Flags, Group1_EbIb_Cmp_082, Spec{}.regForm(), 4)

// Handler index 0x083
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_32_Flags_Edx, Group1_EvIb_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 0)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_Flags_Eax, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 1)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_NoFlags_Eax, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax).flags(Flags::Dead), 2)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_Flags_Eax, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 3)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_NoFlags_Eax, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax).flags(Flags::Dead), 4)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_32_Flags_Eax, Group1_EvIb_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Eax), 5)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_Flags_Ecx, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 6)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_NoFlags_Ecx, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx).flags(Flags::Dead), 7)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_Flags_Ecx, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 8)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_NoFlags_Ecx, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx).flags(Flags::Dead), 9)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_32_Flags_Ecx, Group1_EvIb_Cmp, Spec{}.width(Width::W32).regForm().dst(Gpr::Ecx), 10)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_Flags_Edx, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 11)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_NoFlags_Edx, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx).flags(Flags::Dead), 12)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_Flags_Edx, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx), 13)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_NoFlags_Edx, Group1_EvIb_Sub, Spec{}.width(Width::W32).regForm().dst(Gpr::Edx).flags(Flags::Dead), 14)
INSTRUCTION_VARIANT(OpGroup1_EvIb_And_32_NoFlags_ModReg, Group1_EvIb_And, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 15)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_Flags_ModReg, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm(), 16)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_NoFlags_ModReg, Group1_EvIb_Add, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 17)
INSTRUCTION_VARIANT(OpGroup1_EvIb_And_32_NoFlags, Group1_EvIb_And, Spec{}.width(Width::W32).flags(Flags::Dead), 18)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Xor_32_NoFlags, Group1_EvIb_Xor, Spec{}.width(Width::W32).flags(Flags::Dead), 19)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Or_32_NoFlags, Group1_EvIb_Or, Spec{}.width(Width::W32).flags(Flags::Dead), 20)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Or_16_Flags, Group1_EvIb_Or, Spec{}.width(Width::W16), 21)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Or_16_NoFlags, Group1_EvIb_Or, Spec{}.width(Width::W16).flags(Flags::Dead), 22)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Adc_32_Flags, Group1_EvIb_Adc, Spec{}.width(Width::W32), 23)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Adc_32_NoFlags, Group1_EvIb_Adc, Spec{}.width(Width::W32).flags(Flags::Dead), 24)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_NoFlags, Group1_EvIb_Add, Spec{}.width(Width::W32).flags(Flags::Dead), 25)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Xor_16_Flags, Group1_EvIb_Xor, Spec{}.width(Width::W16), 26)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Xor_16_NoFlags, Group1_EvIb_Xor, Spec{}.width(Width::W16).flags(Flags::Dead), 27)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_32_Flags, Group1_EvIb_Cmp, Spec{}.width(Width::W32), 28)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_32_NoFlags, Group1_EvIb_Cmp, Spec{}.width(Width::W32).flags(Flags::Dead), 29)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_16_Flags, Group1_EvIb_Cmp, Spec{}.width(Width::W16), 30)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Cmp_16_NoFlags, Group1_EvIb_Cmp, Spec{}.width(Width::W16).flags(Flags::Dead), 31)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_16_Flags, Group1_EvIb_Add, Spec{}.width(Width::W16), 32)
INSTRUCTION_VARIANT(OpGroup1_EvIb_And_32_Flags, Group1_EvIb_And, Spec{}.width(Width::W32), 33)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_16_NoFlags, Group1_EvIb_Add, Spec{}.width(Width::W16).flags(Flags::Dead), 34)
INSTRUCTION_VARIANT(OpGroup1_EvIb_And_16_Flags, Group1_EvIb_And, Spec{}.width(Width::W16), 35)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Add_32_Flags, Group1_EvIb_Add, Spec{}.width(Width::W32), 36)
INSTRUCTION_VARIANT(OpGroup1_EvIb_And_16_NoFlags, Group1_EvIb_And, Spec{}.width(Width::W16).flags(Flags::Dead), 37)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sbb_16_NoFlags, Group1_EvIb_Sbb, Spec{}.width(Width::W16).flags(Flags::Dead), 38)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_Flags, Group1_EvIb_Sub, Spec{}.width(Width::W32), 39)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sbb_16_Flags, Group1_EvIb_Sbb, Spec{}.width(Width::W16), 40)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_32_NoFlags, Group1_EvIb_Sub, Spec{}.width(Width::W32).flags(Flags::Dead), 41)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sbb_32_NoFlags, Group1_EvIb_Sbb, Spec{}.width(Width::W32).flags(Flags::Dead), 42)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_16_Flags, Group1_EvIb_Sub, Spec{}.width(Width::W16), 43)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sbb_32_Flags, Group1_EvIb_Sbb, Spec{}.width(Width::W32), 44)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Sub_16_NoFlags, Group1_EvIb_Sub, Spec{}.width(Width::W16).flags(Flags::Dead), 45)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Adc_16_NoFlags, Group1_EvIb_Adc, Spec{}.width(Width::W16).flags(Flags::Dead), 46)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Xor_32_Flags, Group1_EvIb_Xor, Spec{}.width(Width::W32), 47)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Adc_16_Flags, Group1_EvIb_Adc, Spec{}.width(Width::W16), 48)
INSTRUCTION_VARIANT(OpGroup1_EvIb_Or_32_Flags, Group1_EvIb_Or, Spec{}.width(Width::W32), 49)

// Handler index 0x085
INSTRUCTION_VARIANT(OpTest_EvGv_32_ModReg_Eax, Test_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Eax).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpTest_EvGv_32_ModReg_Ecx, Test_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Ecx).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpTest_EvGv_32_ModReg_Edx, Test_EvGv, Spec{}.width(Width::W32).regForm().src(Gpr::Edx).dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpTest_EvGv_32_ModReg, Test_EvGv, Spec{}.width(Width::W32).regForm(), 3)
INSTRUCTION_VARIANT(OpTest_EvGv_16, Test_EvGv, Spec{}.width(Width::W16), 4)
INSTRUCTION_VARIANT(OpTest_EvGv_32, Test_EvGv, Spec{}.width(Width::W32), 5)

// Handler index 0x08D
INSTRUCTION_VARIANT(OpLea_32_Eax, Lea, Spec{}.width(Width::W32).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpLea_32_Ecx, Lea, Spec{}.width(Width::W32).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpLea_32_Edx, Lea, Spec{}.width(Width::W32).dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpLea_16, Lea, Spec{}.width(Width::W16), 3)
INSTRUCTION_VARIANT(OpLea_32, Lea, Spec{}.width(Width::W32), 4)

// Handler index 0x09C
INSTRUCTION_VARIANT(OpPushf16, Pushf32, Spec{}.width(Width::W16), 0)

// Handler index 0x09D
INSTRUCTION_VARIANT(OpPopf16, Popf32, Spec{}.width(Width::W16), 0)

// Handler index 0x0C1
INSTRUCTION_VARIANT(OpRol_RegImm16_Live, Group2_EvIb_Rol, Spec{}.width(Width::W16).regForm(), 0)
INSTRUCTION_VARIANT(OpRol_RegImm16_Dead, Group2_EvIb_Rol, Spec{}.width(Width::W16).regForm().flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpRor_RegImm16_Live, Group2_EvIb_Ror, Spec{}.width(Width::W16).regForm(), 2)
INSTRUCTION_VARIANT(OpRor_RegImm16_Dead, Group2_EvIb_Ror, Spec{}.width(Width::W16).regForm().flags(Flags::Dead), 3)
INSTRUCTION_VARIANT(OpRol_RegImm32_Live, Group2_EvIb_Rol, Spec{}.width(Width::W32).regForm(), 4)
INSTRUCTION_VARIANT(OpRol_RegImm32_Dead, Group2_EvIb_Rol, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 5)
INSTRUCTION_VARIANT(OpRor_RegImm32_Live, Group2_EvIb_Ror, Spec{}.width(Width::W32).regForm(), 6)
INSTRUCTION_VARIANT(OpRor_RegImm32_Dead, Group2_EvIb_Ror, Spec{}.width(Width::W32).regForm().flags(Flags::Dead), 7)
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvIb_Shl_ModReg, Group2_EvIb_Shl, Spec{}.regForm(), OpGroup2_EvIb_Shl, 8)
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvIb_Shr_ModReg, Group2_EvIb_Shr, Spec{}.regForm(), OpGroup2_EvIb_Shr, 9)
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvIb_Sar_ModReg, Group2_EvIb_Sar, Spec{}.regForm(), OpGroup2_EvIb_Sar, 10)
INSTRUCTION_VARIANT(OpGroup2_EvIb_Shl, Group2_EvIb_Shl, Spec{}, 11)
INSTRUCTION_VARIANT(OpGroup2_EvIb_Shr, Group2_EvIb_Shr, Spec{}, 12)
INSTRUCTION_VARIANT(OpGroup2_EvIb_Sar, Group2_EvIb_Sar, Spec{}, 13)

// Handler index 0x0C7
INSTRUCTION_VARIANT(OpMov_EvIz_EspBaseNoIndexNoSegment, Mov_EvIz, Spec{}.base(Base::Esp).index(Index::Absent).segment(Segment::Default), 0)
INSTRUCTION_VARIANT(OpMov_EvIz_EaxBaseNoIndexNoSegment, Mov_EvIz, Spec{}.base(Base::Eax).index(Index::Absent).segment(Segment::Default), 1)
INSTRUCTION_VARIANT(OpMov_EvIz_EsiBaseNoIndexNoSegment, Mov_EvIz, Spec{}.base(Base::Esi).index(Index::Absent).segment(Segment::Default), 2)

// Handler index 0x0D1
INSTRUCTION_FUSION_VARIANT(OpGroup2_Ev1_Shl_ModReg, Group2_Ev1_Shl, Spec{}.regForm(), OpGroup2_Ev1_Shl, 0)
INSTRUCTION_FUSION_VARIANT(OpGroup2_Ev1_Shr_ModReg, Group2_Ev1_Shr, Spec{}.regForm(), OpGroup2_Ev1_Shr, 1)
INSTRUCTION_FUSION_VARIANT(OpGroup2_Ev1_Sar_ModReg, Group2_Ev1_Sar, Spec{}.regForm(), OpGroup2_Ev1_Sar, 2)
INSTRUCTION_VARIANT(OpGroup2_Ev1_Shl, Group2_Ev1_Shl, Spec{}, 3)
INSTRUCTION_VARIANT(OpGroup2_Ev1_Shr, Group2_Ev1_Shr, Spec{}, 4)
INSTRUCTION_VARIANT(OpGroup2_Ev1_Sar, Group2_Ev1_Sar, Spec{}, 5)

// Handler index 0x0D3
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvCl_Shl_ModReg, Group2_EvCl_Shl, Spec{}.regForm(), OpGroup2_EvCl_Shl, 0)
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvCl_Shr_ModReg, Group2_EvCl_Shr, Spec{}.regForm(), OpGroup2_EvCl_Shr, 1)
INSTRUCTION_FUSION_VARIANT(OpGroup2_EvCl_Sar_ModReg, Group2_EvCl_Sar, Spec{}.regForm(), OpGroup2_EvCl_Sar, 2)
INSTRUCTION_VARIANT(OpGroup2_EvCl_Shl, Group2_EvCl_Shl, Spec{}, 3)
INSTRUCTION_VARIANT(OpGroup2_EvCl_Shr, Group2_EvCl_Shr, Spec{}, 4)
INSTRUCTION_VARIANT(OpGroup2_EvCl_Sar, Group2_EvCl_Sar, Spec{}, 5)

// Handler index 0x0F6
INSTRUCTION_VARIANT(OpGroup3_Eb_Not_Flags, Group3_Eb_Not, Spec{}, 0)
INSTRUCTION_VARIANT(OpGroup3_Eb_Not_NoFlags, Group3_Eb_Not, Spec{}.flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpGroup3_Eb_Neg_Flags, Group3_Eb_Neg, Spec{}, 2)
INSTRUCTION_VARIANT(OpGroup3_Eb_Neg_NoFlags, Group3_Eb_Neg, Spec{}.flags(Flags::Dead), 3)
INSTRUCTION_VARIANT(OpGroup3_Eb_Mul_Flags, Group3_Eb_Mul, Spec{}, 4)
INSTRUCTION_VARIANT(OpGroup3_Eb_Mul_NoFlags, Group3_Eb_Mul, Spec{}.flags(Flags::Dead), 5)
INSTRUCTION_VARIANT(OpGroup3_Eb_Imul_Flags, Group3_Eb_Imul, Spec{}, 6)
INSTRUCTION_VARIANT(OpGroup3_Eb_Imul_NoFlags, Group3_Eb_Imul, Spec{}.flags(Flags::Dead), 7)

// Handler index 0x0F7
INSTRUCTION_VARIANT(OpGroup3_Ev_Not_32_Flags, Group3_Ev_Not, Spec{}.width(Width::W32), 0)
INSTRUCTION_VARIANT(OpGroup3_Ev_Not_32_NoFlags, Group3_Ev_Not, Spec{}.width(Width::W32).flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpGroup3_Ev_Not_16_Flags, Group3_Ev_Not, Spec{}.width(Width::W16), 2)
INSTRUCTION_VARIANT(OpGroup3_Ev_Not_16_NoFlags, Group3_Ev_Not, Spec{}.width(Width::W16).flags(Flags::Dead), 3)
INSTRUCTION_VARIANT(OpGroup3_Ev_Neg_32_Flags, Group3_Ev_Neg, Spec{}.width(Width::W32), 4)
INSTRUCTION_VARIANT(OpGroup3_Ev_Neg_32_NoFlags, Group3_Ev_Neg, Spec{}.width(Width::W32).flags(Flags::Dead), 5)
INSTRUCTION_VARIANT(OpGroup3_Ev_Neg_16_Flags, Group3_Ev_Neg, Spec{}.width(Width::W16), 6)
INSTRUCTION_VARIANT(OpGroup3_Ev_Neg_16_NoFlags, Group3_Ev_Neg, Spec{}.width(Width::W16).flags(Flags::Dead), 7)
INSTRUCTION_VARIANT(OpGroup3_Ev_Mul_32_Flags, Group3_Ev_Mul, Spec{}.width(Width::W32), 8)
INSTRUCTION_VARIANT(OpGroup3_Ev_Mul_32_NoFlags, Group3_Ev_Mul, Spec{}.width(Width::W32).flags(Flags::Dead), 9)
INSTRUCTION_VARIANT(OpGroup3_Ev_Mul_16_Flags, Group3_Ev_Mul, Spec{}.width(Width::W16), 10)
INSTRUCTION_VARIANT(OpGroup3_Ev_Mul_16_NoFlags, Group3_Ev_Mul, Spec{}.width(Width::W16).flags(Flags::Dead), 11)
INSTRUCTION_VARIANT(OpGroup3_Ev_Imul_32_Flags, Group3_Ev_Imul, Spec{}.width(Width::W32), 12)
INSTRUCTION_VARIANT(OpGroup3_Ev_Imul_32_NoFlags, Group3_Ev_Imul, Spec{}.width(Width::W32).flags(Flags::Dead), 13)
INSTRUCTION_VARIANT(OpGroup3_Ev_Imul_16_Flags, Group3_Ev_Imul, Spec{}.width(Width::W16), 14)
INSTRUCTION_VARIANT(OpGroup3_Ev_Imul_16_NoFlags, Group3_Ev_Imul, Spec{}.width(Width::W16).flags(Flags::Dead), 15)
INSTRUCTION_VARIANT(OpGroup3_Ev_Div_32_Flags, Group3_Ev_Div, Spec{}.width(Width::W32), 16)
INSTRUCTION_VARIANT(OpGroup3_Ev_Div_32_NoFlags, Group3_Ev_Div, Spec{}.width(Width::W32).flags(Flags::Dead), 17)
INSTRUCTION_VARIANT(OpGroup3_Ev_Div_16_Flags, Group3_Ev_Div, Spec{}.width(Width::W16), 18)
INSTRUCTION_VARIANT(OpGroup3_Ev_Div_16_NoFlags, Group3_Ev_Div, Spec{}.width(Width::W16).flags(Flags::Dead), 19)
INSTRUCTION_VARIANT(OpGroup3_Ev_Idiv_32_Flags, Group3_Ev_Idiv, Spec{}.width(Width::W32), 20)
INSTRUCTION_VARIANT(OpGroup3_Ev_Idiv_32_NoFlags, Group3_Ev_Idiv, Spec{}.width(Width::W32).flags(Flags::Dead), 21)
INSTRUCTION_VARIANT(OpGroup3_Ev_Idiv_16_Flags, Group3_Ev_Idiv, Spec{}.width(Width::W16), 22)
INSTRUCTION_VARIANT(OpGroup3_Ev_Idiv_16_NoFlags, Group3_Ev_Idiv, Spec{}.width(Width::W16).flags(Flags::Dead), 23)

// Handler index 0x0FE
INSTRUCTION_VARIANT(OpGroup4_Eb_Inc_Flags, Group4_Eb_Inc, Spec{}, 0)
INSTRUCTION_VARIANT(OpGroup4_Eb_Inc_NoFlags, Group4_Eb_Inc, Spec{}.flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpGroup4_Eb_Dec_Flags, Group4_Eb_Dec, Spec{}, 2)
INSTRUCTION_VARIANT(OpGroup4_Eb_Dec_NoFlags, Group4_Eb_Dec, Spec{}.flags(Flags::Dead), 3)

// Handler index 0x0FF
INSTRUCTION_VARIANT(OpGroup5_Ev_Inc_32_Flags, Group5_Ev_Inc, Spec{}.width(Width::W32), 0)
INSTRUCTION_VARIANT(OpGroup5_Ev_Inc_32_NoFlags, Group5_Ev_Inc, Spec{}.width(Width::W32).flags(Flags::Dead), 1)
INSTRUCTION_VARIANT(OpGroup5_Ev_Inc_16_Flags, Group5_Ev_Inc, Spec{}.width(Width::W16), 2)
INSTRUCTION_VARIANT(OpGroup5_Ev_Inc_16_NoFlags, Group5_Ev_Inc, Spec{}.width(Width::W16).flags(Flags::Dead), 3)
INSTRUCTION_VARIANT(OpGroup5_Ev_Dec_32_Flags, Group5_Ev_Dec, Spec{}.width(Width::W32), 4)
INSTRUCTION_VARIANT(OpGroup5_Ev_Dec_32_NoFlags, Group5_Ev_Dec, Spec{}.width(Width::W32).flags(Flags::Dead), 5)
INSTRUCTION_VARIANT(OpGroup5_Ev_Dec_16_Flags, Group5_Ev_Dec, Spec{}.width(Width::W16), 6)
INSTRUCTION_VARIANT(OpGroup5_Ev_Dec_16_NoFlags, Group5_Ev_Dec, Spec{}.width(Width::W16).flags(Flags::Dead), 7)
INSTRUCTION_VARIANT(OpGroup5_Ev_Call_32_Flags, Group5_Ev_Call, Spec{}.width(Width::W32), 8)
INSTRUCTION_VARIANT(OpGroup5_Ev_Call_32_NoFlags, Group5_Ev_Call, Spec{}.width(Width::W32).flags(Flags::Dead), 9)
INSTRUCTION_VARIANT(OpGroup5_Ev_Call_16_Flags, Group5_Ev_Call, Spec{}.width(Width::W16), 10)
INSTRUCTION_VARIANT(OpGroup5_Ev_Call_16_NoFlags, Group5_Ev_Call, Spec{}.width(Width::W16).flags(Flags::Dead), 11)
INSTRUCTION_VARIANT(OpGroup5_Ev_Jmp_32_Flags, Group5_Ev_Jmp, Spec{}.width(Width::W32), 12)
INSTRUCTION_VARIANT(OpGroup5_Ev_Jmp_32_NoFlags, Group5_Ev_Jmp, Spec{}.width(Width::W32).flags(Flags::Dead), 13)
INSTRUCTION_VARIANT(OpGroup5_Ev_Jmp_16_Flags, Group5_Ev_Jmp, Spec{}.width(Width::W16), 14)
INSTRUCTION_VARIANT(OpGroup5_Ev_Jmp_16_NoFlags, Group5_Ev_Jmp, Spec{}.width(Width::W16).flags(Flags::Dead), 15)
INSTRUCTION_VARIANT(OpGroup5_Ev_Push_32_Flags, Group5_Ev_Push, Spec{}.width(Width::W32), 16)
INSTRUCTION_VARIANT(OpGroup5_Ev_Push_32_NoFlags, Group5_Ev_Push, Spec{}.width(Width::W32).flags(Flags::Dead), 17)
INSTRUCTION_VARIANT(OpGroup5_Ev_Push_16_Flags, Group5_Ev_Push, Spec{}.width(Width::W16), 18)
INSTRUCTION_VARIANT(OpGroup5_Ev_Push_16_NoFlags, Group5_Ev_Push, Spec{}.width(Width::W16).flags(Flags::Dead), 19)

// Handler index 0x140
INSTRUCTION_VARIANT(OpCmov_O_ModReg, Cmov_O, Spec{}.regForm(), 0)

// Handler index 0x141
INSTRUCTION_VARIANT(OpCmov_NO_ModReg, Cmov_NO, Spec{}.regForm(), 0)

// Handler index 0x142
INSTRUCTION_VARIANT(OpCmov_B_ModReg, Cmov_B, Spec{}.regForm(), 0)

// Handler index 0x143
INSTRUCTION_VARIANT(OpCmov_AE_ModReg, Cmov_AE, Spec{}.regForm(), 0)

// Handler index 0x144
INSTRUCTION_VARIANT(OpCmov_E_ModReg, Cmov_E, Spec{}.regForm(), 0)

// Handler index 0x145
INSTRUCTION_VARIANT(OpCmov_NE_ModReg, Cmov_NE, Spec{}.regForm(), 0)

// Handler index 0x146
INSTRUCTION_VARIANT(OpCmov_BE_ModReg, Cmov_BE, Spec{}.regForm(), 0)

// Handler index 0x147
INSTRUCTION_VARIANT(OpCmov_A_ModReg, Cmov_A, Spec{}.regForm(), 0)

// Handler index 0x148
INSTRUCTION_VARIANT(OpCmov_S_ModReg, Cmov_S, Spec{}.regForm(), 0)

// Handler index 0x149
INSTRUCTION_VARIANT(OpCmov_NS_ModReg, Cmov_NS, Spec{}.regForm(), 0)

// Handler index 0x14A
INSTRUCTION_VARIANT(OpCmov_P_ModReg, Cmov_P, Spec{}.regForm(), 0)

// Handler index 0x14B
INSTRUCTION_VARIANT(OpCmov_NP_ModReg, Cmov_NP, Spec{}.regForm(), 0)

// Handler index 0x14C
INSTRUCTION_VARIANT(OpCmov_L_ModReg, Cmov_L, Spec{}.regForm(), 0)

// Handler index 0x14D
INSTRUCTION_VARIANT(OpCmov_GE_ModReg, Cmov_GE, Spec{}.regForm(), 0)

// Handler index 0x14E
INSTRUCTION_VARIANT(OpCmov_LE_ModReg, Cmov_LE, Spec{}.regForm(), 0)

// Handler index 0x14F
INSTRUCTION_VARIANT(OpCmov_G_ModReg, Cmov_G, Spec{}.regForm(), 0)

// Handler index 0x160
INSTRUCTION_VARIANT(OpPunpcklbw_Sse, Encoding_160, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x161
INSTRUCTION_VARIANT(OpPunpcklwd_Sse, Encoding_161, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x162
INSTRUCTION_VARIANT(OpPunpckldq_Sse, Encoding_162, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x163
INSTRUCTION_VARIANT(OpPacksswb_Sse, Encoding_163, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x164
INSTRUCTION_VARIANT(OpPcmpgtb_Sse, Encoding_164, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x165
INSTRUCTION_VARIANT(OpPcmpgtw_Sse, Encoding_165, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x166
INSTRUCTION_VARIANT(OpPcmpgtd_Sse, Encoding_166, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x167
INSTRUCTION_VARIANT(OpPackuswb_Sse, Encoding_167, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x168
INSTRUCTION_VARIANT(OpPunpckhbw_Sse, Encoding_168, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x169
INSTRUCTION_VARIANT(OpPunpckhwd_Sse, Encoding_169, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16A
INSTRUCTION_VARIANT(OpPunpckhdq_Sse, Encoding_16A, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16B
INSTRUCTION_VARIANT(OpPackssdw_Sse, Encoding_16B, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16C
INSTRUCTION_VARIANT(OpPunpcklqdq_Sse, Encoding_16C, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16D
INSTRUCTION_VARIANT(OpPunpckhqdq_Sse, Encoding_16D, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16E
INSTRUCTION_VARIANT(OpMovd_Load, Encoding_16E, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x16F
INSTRUCTION_VARIANT(OpMovdqa_Load, Encoding_16F, Spec{}.mandatory(MandatoryPrefix::P66), 0)
INSTRUCTION_VARIANT(OpMovdqu_Load, Encoding_16F, Spec{}.mandatory(MandatoryPrefix::F3), 1)

// Handler index 0x170
INSTRUCTION_VARIANT(OpGroup_Pshuf, Encoding_170, Spec{}.mandatory(MandatoryPrefix::P66), 0)
INSTRUCTION_VARIANT(OpGroup_Pshuf, Encoding_170, Spec{}.mandatory(MandatoryPrefix::F2), 1)
INSTRUCTION_VARIANT(OpGroup_Pshuf, Encoding_170, Spec{}.mandatory(MandatoryPrefix::F3), 2)

// Handler index 0x171
INSTRUCTION_VARIANT(OpGroup_Sse_Shift_Imm_W, Encoding_171, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x172
INSTRUCTION_VARIANT(OpGroup_Sse_Shift_Imm_D, Encoding_172, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x173
INSTRUCTION_VARIANT(OpGroup_Sse_Shift_Imm_Q, Encoding_173, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x174
INSTRUCTION_VARIANT(OpPcmpeqb_Sse, Encoding_174, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x175
INSTRUCTION_VARIANT(OpPcmpeqw_Sse, Encoding_175, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x176
INSTRUCTION_VARIANT(OpPcmpeqd_Sse, Encoding_176, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x17E
INSTRUCTION_VARIANT(OpMovd_Store, Encoding_17E, Spec{}.mandatory(MandatoryPrefix::P66), 0)
INSTRUCTION_VARIANT(OpMovd_Store, Encoding_17E, Spec{}.mandatory(MandatoryPrefix::F3), 1)

// Handler index 0x17F
INSTRUCTION_VARIANT(OpMovdqa_Store, Encoding_17F, Spec{}.mandatory(MandatoryPrefix::P66), 0)
INSTRUCTION_VARIANT(OpMovdqu_Store, Encoding_17F, Spec{}.mandatory(MandatoryPrefix::F3), 1)

// Handler index 0x1B1
INSTRUCTION_VARIANT(OpCmpxchg_EvGv_16, Cmpxchg_EvGv, Spec{}.width(Width::W16), 0)
INSTRUCTION_VARIANT(OpCmpxchg_EvGv_32, Cmpxchg_EvGv, Spec{}.width(Width::W32), 1)

// Handler index 0x1B6
INSTRUCTION_VARIANT(OpMovzx_Byte_32_Eax, Movzx_Byte, Spec{}.width(Width::W32).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpMovzx_Byte_32_Ecx, Movzx_Byte, Spec{}.width(Width::W32).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpMovzx_Byte_32_Edx, Movzx_Byte, Spec{}.width(Width::W32).dst(Gpr::Edx), 2)

// Handler index 0x1BE
INSTRUCTION_VARIANT(OpMovsx_Byte_32_Eax, Movsx_Byte, Spec{}.width(Width::W32).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpMovsx_Byte_32_Ecx, Movsx_Byte, Spec{}.width(Width::W32).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpMovsx_Byte_32_Edx, Movsx_Byte, Spec{}.width(Width::W32).dst(Gpr::Edx), 2)

// Handler index 0x1BF
INSTRUCTION_VARIANT(OpMovsx_Word_32_Eax, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpMovsx_Word_32_Ecx, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpMovsx_Word_32_Edx, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpMovsx_Word_32_Ebx, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Ebx), 3)
INSTRUCTION_VARIANT(OpMovsx_Word_32_Ebp, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Ebp), 4)
INSTRUCTION_VARIANT(OpMovsx_Word_32_Edi, Movsx_Word, Spec{}.width(Width::W32).dst(Gpr::Edi), 5)

// Handler index 0x1C4
INSTRUCTION_VARIANT(OpPinsrw_Sse, Encoding_1C4, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1C5
INSTRUCTION_VARIANT(OpPextrw_Sse, Encoding_1C5, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D1
INSTRUCTION_VARIANT(OpPsrlw_Sse, Encoding_1D1, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D2
INSTRUCTION_VARIANT(OpPsrld_Sse, Encoding_1D2, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D3
INSTRUCTION_VARIANT(OpPsrlq_Sse, Encoding_1D3, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D4
INSTRUCTION_VARIANT(OpPaddq_Sse, Encoding_1D4, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D5
INSTRUCTION_VARIANT(OpPmullw_Sse, Encoding_1D5, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D6
INSTRUCTION_VARIANT(OpMovq_Store, Encoding_1D6, Spec{}.mandatory(MandatoryPrefix::P66), 0)
INSTRUCTION_VARIANT(OpMovq2dq, Encoding_1D6, Spec{}.mandatory(MandatoryPrefix::F3), 1)
INSTRUCTION_VARIANT(OpMovdq2q, Encoding_1D6, Spec{}.mandatory(MandatoryPrefix::F2), 2)

// Handler index 0x1D7
INSTRUCTION_VARIANT(OpPmovmskb_Sse, Encoding_1D7, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D8
INSTRUCTION_VARIANT(OpPsubusb_Sse, Encoding_1D8, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1D9
INSTRUCTION_VARIANT(OpPsubusw_Sse, Encoding_1D9, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DA
INSTRUCTION_VARIANT(OpPminub_Sse, Encoding_1DA, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DB
INSTRUCTION_VARIANT(OpPand_Sse, Encoding_1DB, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DC
INSTRUCTION_VARIANT(OpPaddusb_Sse, Encoding_1DC, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DD
INSTRUCTION_VARIANT(OpPaddusw_Sse, Encoding_1DD, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DE
INSTRUCTION_VARIANT(OpPmaxub_Sse, Encoding_1DE, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1DF
INSTRUCTION_VARIANT(OpPandn_Sse, Encoding_1DF, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E0
INSTRUCTION_VARIANT(OpPavgb_Sse, Encoding_1E0, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E1
INSTRUCTION_VARIANT(OpPsraw_Sse, Encoding_1E1, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E2
INSTRUCTION_VARIANT(OpPsrad_Sse, Encoding_1E2, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E3
INSTRUCTION_VARIANT(OpPavgw_Sse, Encoding_1E3, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E4
INSTRUCTION_VARIANT(OpPmulhuw_Sse, Encoding_1E4, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E5
INSTRUCTION_VARIANT(OpPmulhw_Sse, Encoding_1E5, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E7
INSTRUCTION_VARIANT(OpMovntdq, Encoding_1E7, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E8
INSTRUCTION_VARIANT(OpPsubsb_Sse, Encoding_1E8, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1E9
INSTRUCTION_VARIANT(OpPsubsw_Sse, Encoding_1E9, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1EA
INSTRUCTION_VARIANT(OpPminsw_Sse, Encoding_1EA, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1EB
INSTRUCTION_VARIANT(OpPor_Sse, Encoding_1EB, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1EC
INSTRUCTION_VARIANT(OpPaddsb_Sse, Encoding_1EC, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1ED
INSTRUCTION_VARIANT(OpPaddsw_Sse, Encoding_1ED, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1EE
INSTRUCTION_VARIANT(OpPmaxsw_Sse, Encoding_1EE, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1EF
INSTRUCTION_VARIANT(OpPxor_Sse, Encoding_1EF, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F1
INSTRUCTION_VARIANT(OpPsllw_Sse, Encoding_1F1, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F2
INSTRUCTION_VARIANT(OpPslld_Sse, Encoding_1F2, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F3
INSTRUCTION_VARIANT(OpPsllq_Sse, Encoding_1F3, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F4
INSTRUCTION_VARIANT(OpPmuludq_Sse, Encoding_1F4, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F5
INSTRUCTION_VARIANT(OpPmaddwd_Sse, Encoding_1F5, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F6
INSTRUCTION_VARIANT(OpPsadbw_Sse, Encoding_1F6, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F7
INSTRUCTION_VARIANT(OpMaskmovdqu, Encoding_1F7, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F8
INSTRUCTION_VARIANT(OpPsubb_Sse, Encoding_1F8, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1F9
INSTRUCTION_VARIANT(OpPsubw_Sse, Encoding_1F9, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1FA
INSTRUCTION_VARIANT(OpPsubd_Sse, Encoding_1FA, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1FB
INSTRUCTION_VARIANT(OpPsubq_Sse, Encoding_1FB, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1FC
INSTRUCTION_VARIANT(OpPaddb_Sse, Encoding_1FC, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1FD
INSTRUCTION_VARIANT(OpPaddw_Sse, Encoding_1FD, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x1FE
INSTRUCTION_VARIANT(OpPaddd_Sse, Encoding_1FE, Spec{}.mandatory(MandatoryPrefix::P66), 0)

// Handler index 0x200
INSTRUCTION_VARIANT(OpMov_EvGv_Eax, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Eax), 0)
INSTRUCTION_VARIANT(OpMov_EvGv_Ecx, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Ecx), 1)
INSTRUCTION_VARIANT(OpMov_EvGv_Edx, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Edx), 2)
INSTRUCTION_VARIANT(OpMov_EvGv_Ebx, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Ebx), 3)
INSTRUCTION_VARIANT(OpMov_EvGv_Esp, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Esp), 4)
INSTRUCTION_VARIANT(OpMov_EvGv_Ebp, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Ebp), 5)
INSTRUCTION_VARIANT(OpMov_EvGv_Esi, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Esi), 6)
INSTRUCTION_VARIANT(OpMov_EvGv_Edi, Mov_EvGv_Reg, Spec{}.regForm().src(Gpr::Edi), 7)

// Handler index 0x201
INSTRUCTION_VARIANT(OpMov_Store_Eax_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Eax).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 0)
INSTRUCTION_VARIANT(OpMov_Store_Ecx_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ecx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 1)
INSTRUCTION_VARIANT(OpMov_Store_Edx_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 2)
INSTRUCTION_VARIANT(OpMov_Store_Ebx_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 3)
INSTRUCTION_VARIANT(OpMov_Store_Esp_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esp).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 4)
INSTRUCTION_VARIANT(OpMov_Store_Ebp_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebp).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 5)
INSTRUCTION_VARIANT(OpMov_Store_Esi_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esi).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 6)
INSTRUCTION_VARIANT(OpMov_Store_Edi_EspBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edi).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 7)
INSTRUCTION_VARIANT(OpMov_Store_Edi_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edi).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 8)
INSTRUCTION_VARIANT(OpMov_Store_Ecx_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ecx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 9)
INSTRUCTION_VARIANT(OpMov_Store_Edx_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 10)
INSTRUCTION_VARIANT(OpMov_Store_Ebx_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 11)
INSTRUCTION_VARIANT(OpMov_Store_Eax_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Eax).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 12)
INSTRUCTION_VARIANT(OpMov_Store_Esp_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esp).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 13)
INSTRUCTION_VARIANT(OpMov_Store_Ebp_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebp).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 14)
INSTRUCTION_VARIANT(OpMov_Store_Esi_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esi).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 15)
INSTRUCTION_VARIANT(OpMov_Store_Edi_EaxBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edi).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 16)
INSTRUCTION_VARIANT(OpMov_Store_Eax_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Eax).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 17)
INSTRUCTION_VARIANT(OpMov_Store_Ecx_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ecx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 18)
INSTRUCTION_VARIANT(OpMov_Store_Edx_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Edx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 19)
INSTRUCTION_VARIANT(OpMov_Store_Ebx_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 20)
INSTRUCTION_VARIANT(OpMov_Store_Esp_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esp).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 21)
INSTRUCTION_VARIANT(OpMov_Store_Ebp_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebp).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 22)
INSTRUCTION_VARIANT(OpMov_Store_Esi_EsiBaseNoIndexNoSegment, Mov_EvGv_Mem, Spec{}.src(Gpr::Esi).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 23)
INSTRUCTION_VARIANT(OpMov_Store_Edx, Mov_EvGv_Mem, Spec{}.src(Gpr::Edx), 24)
INSTRUCTION_VARIANT(OpMov_Store_Ebp, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebp), 25)
INSTRUCTION_VARIANT(OpMov_Store_Esi, Mov_EvGv_Mem, Spec{}.src(Gpr::Esi), 26)
INSTRUCTION_VARIANT(OpMov_Store_Edi, Mov_EvGv_Mem, Spec{}.src(Gpr::Edi), 27)
INSTRUCTION_VARIANT(OpMov_Store_Eax, Mov_EvGv_Mem, Spec{}.src(Gpr::Eax), 28)
INSTRUCTION_VARIANT(OpMov_Store_Ebx, Mov_EvGv_Mem, Spec{}.src(Gpr::Ebx), 29)
INSTRUCTION_VARIANT(OpMov_Store_Esp, Mov_EvGv_Mem, Spec{}.src(Gpr::Esp), 30)
INSTRUCTION_VARIANT(OpMov_Store_Ecx, Mov_EvGv_Mem, Spec{}.src(Gpr::Ecx), 31)

// Handler index 0x202
INSTRUCTION_VARIANT(OpMov_Ebp_Esp, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Ebp).src(Gpr::Esp), 0)
INSTRUCTION_VARIANT(OpMov_Ecx_Eax, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Ecx).src(Gpr::Eax), 1)
INSTRUCTION_VARIANT(OpMov_Edx_Eax, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Edx).src(Gpr::Eax), 2)
INSTRUCTION_VARIANT(OpMov_Eax_Eax, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Eax).src(Gpr::Eax), 3)
INSTRUCTION_VARIANT(OpMov_Esi_Eax, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Esi).src(Gpr::Eax), 4)
INSTRUCTION_VARIANT(OpMov_Edi_Eax, Mov_GvEv_Reg, Spec{}.regForm().dst(Gpr::Edi).src(Gpr::Eax), 5)

// Handler index 0x203
INSTRUCTION_VARIANT(OpMov_Load_Eax_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Eax).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 0)
INSTRUCTION_VARIANT(OpMov_Load_Ecx_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ecx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 1)
INSTRUCTION_VARIANT(OpMov_Load_Edx_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 2)
INSTRUCTION_VARIANT(OpMov_Load_Ebx_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebx).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 3)
INSTRUCTION_VARIANT(OpMov_Load_Esp_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esp).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 4)
INSTRUCTION_VARIANT(OpMov_Load_Ebp_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebp).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 5)
INSTRUCTION_VARIANT(OpMov_Load_Esi_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esi).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 6)
INSTRUCTION_VARIANT(OpMov_Load_Edi_EspBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edi).base(Base::Esp).index(Index::Absent).segment(Segment::Default), 7)
INSTRUCTION_VARIANT(OpMov_Load_Edi_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edi).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 8)
INSTRUCTION_VARIANT(OpMov_Load_Ecx_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ecx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 9)
INSTRUCTION_VARIANT(OpMov_Load_Edx_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 10)
INSTRUCTION_VARIANT(OpMov_Load_Ebx_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebx).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 11)
INSTRUCTION_VARIANT(OpMov_Load_Eax_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Eax).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 12)
INSTRUCTION_VARIANT(OpMov_Load_Esp_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esp).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 13)
INSTRUCTION_VARIANT(OpMov_Load_Ebp_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebp).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 14)
INSTRUCTION_VARIANT(OpMov_Load_Esi_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esi).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 15)
INSTRUCTION_VARIANT(OpMov_Load_Edi_EaxBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edi).base(Base::Eax).index(Index::Absent).segment(Segment::Default), 16)
INSTRUCTION_VARIANT(OpMov_Load_Eax_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Eax).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 17)
INSTRUCTION_VARIANT(OpMov_Load_Ecx_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ecx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 18)
INSTRUCTION_VARIANT(OpMov_Load_Edx_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 19)
INSTRUCTION_VARIANT(OpMov_Load_Ebx_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebx).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 20)
INSTRUCTION_VARIANT(OpMov_Load_Esp_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esp).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 21)
INSTRUCTION_VARIANT(OpMov_Load_Ebp_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebp).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 22)
INSTRUCTION_VARIANT(OpMov_Load_Esi_EsiBaseNoIndexNoSegment, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esi).base(Base::Esi).index(Index::Absent).segment(Segment::Default), 23)
INSTRUCTION_VARIANT(OpMov_Load_Edx, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edx), 24)
INSTRUCTION_VARIANT(OpMov_Load_Ebp, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebp), 25)
INSTRUCTION_VARIANT(OpMov_Load_Esi, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esi), 26)
INSTRUCTION_VARIANT(OpMov_Load_Edi, Mov_GvEv_Mem, Spec{}.dst(Gpr::Edi), 27)
INSTRUCTION_VARIANT(OpMov_Load_Eax, Mov_GvEv_Mem, Spec{}.dst(Gpr::Eax), 28)
INSTRUCTION_VARIANT(OpMov_Load_Ebx, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ebx), 29)
INSTRUCTION_VARIANT(OpMov_Load_Esp, Mov_GvEv_Mem, Spec{}.dst(Gpr::Esp), 30)
INSTRUCTION_VARIANT(OpMov_Load_Ecx, Mov_GvEv_Mem, Spec{}.dst(Gpr::Ecx), 31)
