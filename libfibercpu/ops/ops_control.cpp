#include "ops_control_impl.h"

namespace fiberish {

static HandlerFunc g_JccRel8Wrappers[] = {
    DispatchWrapper<op::OpJcc_O_Rel8>,  DispatchWrapper<op::OpJcc_NO_Rel8>, DispatchWrapper<op::OpJcc_B_Rel8>,
    DispatchWrapper<op::OpJcc_AE_Rel8>, DispatchWrapper<op::OpJcc_E_Rel8>,  DispatchWrapper<op::OpJcc_NE_Rel8>,
    DispatchWrapper<op::OpJcc_BE_Rel8>, DispatchWrapper<op::OpJcc_A_Rel8>,  DispatchWrapper<op::OpJcc_S_Rel8>,
    DispatchWrapper<op::OpJcc_NS_Rel8>, DispatchWrapper<op::OpJcc_P_Rel8>,  DispatchWrapper<op::OpJcc_NP_Rel8>,
    DispatchWrapper<op::OpJcc_L_Rel8>,  DispatchWrapper<op::OpJcc_GE_Rel8>, DispatchWrapper<op::OpJcc_LE_Rel8>,
    DispatchWrapper<op::OpJcc_G_Rel8>};

static HandlerFunc g_JccRel32Wrappers[] = {
    DispatchWrapper<op::OpJcc_O_Rel32>,  DispatchWrapper<op::OpJcc_NO_Rel32>, DispatchWrapper<op::OpJcc_B_Rel32>,
    DispatchWrapper<op::OpJcc_AE_Rel32>, DispatchWrapper<op::OpJcc_E_Rel32>,  DispatchWrapper<op::OpJcc_NE_Rel32>,
    DispatchWrapper<op::OpJcc_BE_Rel32>, DispatchWrapper<op::OpJcc_A_Rel32>,  DispatchWrapper<op::OpJcc_S_Rel32>,
    DispatchWrapper<op::OpJcc_NS_Rel32>, DispatchWrapper<op::OpJcc_P_Rel32>,  DispatchWrapper<op::OpJcc_NP_Rel32>,
    DispatchWrapper<op::OpJcc_L_Rel32>,  DispatchWrapper<op::OpJcc_GE_Rel32>, DispatchWrapper<op::OpJcc_LE_Rel32>,
    DispatchWrapper<op::OpJcc_G_Rel32>};

static HandlerFunc g_CmovWrappers[] = {
    DispatchWrapper<op::OpCmov_O>,  DispatchWrapper<op::OpCmov_NO>, DispatchWrapper<op::OpCmov_B>,
    DispatchWrapper<op::OpCmov_AE>, DispatchWrapper<op::OpCmov_E>,  DispatchWrapper<op::OpCmov_NE>,
    DispatchWrapper<op::OpCmov_BE>, DispatchWrapper<op::OpCmov_A>,  DispatchWrapper<op::OpCmov_S>,
    DispatchWrapper<op::OpCmov_NS>, DispatchWrapper<op::OpCmov_P>,  DispatchWrapper<op::OpCmov_NP>,
    DispatchWrapper<op::OpCmov_L>,  DispatchWrapper<op::OpCmov_GE>, DispatchWrapper<op::OpCmov_LE>,
    DispatchWrapper<op::OpCmov_G>};

void RegisterControlOps() {
    using namespace op;

    g_Handlers[0x90] = DispatchWrapper<OpNop>;
    g_Handlers[0x9B] = DispatchWrapper<OpWait>;
    g_Handlers[0xF4] = DispatchWrapper<OpHlt>;
    g_Handlers[0x9C] = DispatchWrapper<OpPushf>;
    g_Handlers[0x9D] = DispatchWrapper<OpPopf>;
    g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel32>;  // JMP rel32
    g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel8>;   // JMP rel8
    g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>;   // CALL rel32
    g_Handlers[0xC3] = DispatchWrapper<OpRet>;        // RET
    g_Handlers[0xC2] = DispatchWrapper<OpRet_Imm16>;  // RET imm16
    g_Handlers[0xCD] = DispatchWrapper<OpInt>;        // INT imm8
    g_Handlers[0xCC] = DispatchWrapper<OpInt3>;       // INT3
    g_Handlers[0xF5] = DispatchWrapper<OpCmc>;        // CMC
    g_Handlers[0xF8] = DispatchWrapper<OpClc>;        // CLC
    g_Handlers[0xF9] = DispatchWrapper<OpStc>;        // STC
    g_Handlers[0xFA] = DispatchWrapper<OpCli>;        // CLI
    g_Handlers[0xFB] = DispatchWrapper<OpSti>;        // STI
    g_Handlers[0xFC] = DispatchWrapper<OpCld>;        // CLD
    g_Handlers[0xFD] = DispatchWrapper<OpStd>;        // STD
    for (int i = 0; i < 16; i++) {
        g_Handlers[0x70 + i] = g_JccRel8Wrappers[i];    // Jcc rel8
        g_Handlers[0x180 + i] = g_JccRel32Wrappers[i];  // Jcc rel32 (0F 8x)
        g_Handlers[0x140 + i] = g_CmovWrappers[i];      // CMOVcc
    }

    // Register Specialized CMOV Handlers (ModReg)
    SpecCriteria c;
    c.mod_mask = 0x03;
    c.mod_val = 0x03;

#define REG_CMOV_SPEC(opcode, name) DispatchRegistrar<OpCmov_##name##_ModReg>::RegisterSpecialized(opcode, c)

    REG_CMOV_SPEC(0x140, O);
    REG_CMOV_SPEC(0x141, NO);
    REG_CMOV_SPEC(0x142, B);
    REG_CMOV_SPEC(0x143, AE);
    REG_CMOV_SPEC(0x144, E);
    REG_CMOV_SPEC(0x145, NE);
    REG_CMOV_SPEC(0x146, BE);
    REG_CMOV_SPEC(0x147, A);
    REG_CMOV_SPEC(0x148, S);
    REG_CMOV_SPEC(0x149, NS);
    REG_CMOV_SPEC(0x14A, P);
    REG_CMOV_SPEC(0x14B, NP);
    REG_CMOV_SPEC(0x14C, L);
    REG_CMOV_SPEC(0x14D, GE);
    REG_CMOV_SPEC(0x14E, LE);
    REG_CMOV_SPEC(0x14F, G);

#undef REG_CMOV_SPEC

    g_Handlers[0xCE] = DispatchWrapper<OpInto>;
    g_Handlers[0x131] = DispatchWrapper<OpRdtsc>;       // 0F 31
    g_Handlers[0x1A2] = DispatchWrapper<OpCpuid>;       // 0F A2
    g_Handlers[0x11F] = DispatchWrapper<OpNop>;         // Multi-byte NOP (0F 1F)
    g_Handlers[0x1AE] = DispatchWrapper<OpGroup_0FAE>;  // 0F AE /r
}

}  // namespace fiberish
