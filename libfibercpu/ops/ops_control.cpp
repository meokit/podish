#include "ops_control_impl.h"

namespace fiberish {
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
