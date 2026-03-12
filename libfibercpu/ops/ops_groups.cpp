#include "ops_groups_impl.h"

namespace fiberish {
void RegisterGroupOps() {
    using namespace op;

    // Generic Handlers (fallback)
    g_Handlers[0x80] = DispatchWrapper<OpGroup1_EbIb_Generic>;
    g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz_Generic>;
    g_Handlers[0x82] = DispatchWrapper<OpGroup1_EbIb_Generic>;  // Alias of 80
    g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIb_Generic>;

    g_Handlers[0xF6] = DispatchWrapper<OpGroup3_Eb_Generic>;
    g_Handlers[0xF7] = DispatchWrapper<OpGroup3_Ev_Generic>;
    g_Handlers[0xFE] = DispatchWrapper<OpGroup4_Eb_Generic>;
    g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev_Generic>;

    g_Handlers[0x98] = DispatchWrapper<OpCwde>;
    g_Handlers[0x99] = DispatchWrapper<OpCdq>;
    g_Handlers[0x1C7] = DispatchWrapper<OpGroup9>;
    g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Byte>;
    g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Word>;
    g_Handlers[0x118] = DispatchWrapper<OpPrefetch>;
    g_Handlers[0x10B] = DispatchWrapper<OpUd2_Groups>;

    // Specializations (Call macros defined above)
    // Re-invoke macros to register specialized handlers

#define REG_G1_EB(opcode, subop, name)                                             \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        DispatchRegistrar<name##_Flags>::RegisterSpecializedAutoBoth(opcode, c);   \
    }                                                                              \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        c.no_flags = true;                                                         \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecializedAutoBoth(opcode, c); \
    }

    REG_G1_EB(0x80, 0, OpGroup1_EbIb_Add);
    REG_G1_EB(0x80, 1, OpGroup1_EbIb_Or);
    REG_G1_EB(0x80, 2, OpGroup1_EbIb_Adc);
    REG_G1_EB(0x80, 3, OpGroup1_EbIb_Sbb);
    REG_G1_EB(0x80, 4, OpGroup1_EbIb_And);
    REG_G1_EB(0x80, 5, OpGroup1_EbIb_Sub);
    REG_G1_EB(0x80, 6, OpGroup1_EbIb_Xor);
    REG_G1_EB(0x80, 7, OpGroup1_EbIb_Cmp);

#define REG_EV_SPEC(opcode, subop, name)                                              \
    /* 32-bit Normal */                                                               \
    {                                                                                 \
        SpecCriteria c;                                                               \
        c.reg_mask = 7;                                                               \
        c.reg_val = subop;                                                            \
        c.prefix_mask = 0x40;                                                         \
        c.prefix_val = 0;                                                             \
        DispatchRegistrar<name##_32_Flags>::RegisterSpecializedAutoBoth(opcode, c);   \
    }                                                                                 \
    /* 32-bit NF */                                                                   \
    {                                                                                 \
        SpecCriteria c;                                                               \
        c.reg_mask = 7;                                                               \
        c.reg_val = subop;                                                            \
        c.prefix_mask = 0x40;                                                         \
        c.prefix_val = 0;                                                             \
        c.no_flags = true;                                                            \
        DispatchRegistrar<name##_32_NoFlags>::RegisterSpecializedAutoBoth(opcode, c); \
    }                                                                                 \
    /* 16-bit Normal */                                                               \
    {                                                                                 \
        SpecCriteria c;                                                               \
        c.reg_mask = 7;                                                               \
        c.reg_val = subop;                                                            \
        c.prefix_mask = 0x40;                                                         \
        c.prefix_val = 0x40;                                                          \
        DispatchRegistrar<name##_16_Flags>::RegisterSpecializedAutoBoth(opcode, c);   \
    }                                                                                 \
    /* 16-bit NF */                                                                   \
    {                                                                                 \
        SpecCriteria c;                                                               \
        c.reg_mask = 7;                                                               \
        c.reg_val = subop;                                                            \
        c.prefix_mask = 0x40;                                                         \
        c.prefix_val = 0x40;                                                          \
        c.no_flags = true;                                                            \
        DispatchRegistrar<name##_16_NoFlags>::RegisterSpecializedAutoBoth(opcode, c); \
    }

    // Group 1: 0x83 (Mostly used)
    REG_EV_SPEC(0x83, 0, OpGroup1_EvIb_Add);
    REG_EV_SPEC(0x83, 5, OpGroup1_EvIb_Sub);
    REG_EV_SPEC(0x83, 7, OpGroup1_EvIb_Cmp);

    // Group 1: 0x81 (Also used)
    REG_EV_SPEC(0x81, 0, OpGroup1_EvIz_Add);
    REG_EV_SPEC(0x81, 5, OpGroup1_EvIz_Sub);
    REG_EV_SPEC(0x81, 7, OpGroup1_EvIz_Cmp);

#define REG_G3_EB(opcode, subop, name)                                             \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        DispatchRegistrar<name##_Flags>::RegisterSpecializedAutoBoth(opcode, c);   \
    }                                                                              \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        c.no_flags = true;                                                         \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecializedAutoBoth(opcode, c); \
    }

    REG_G3_EB(0xF6, 2, OpGroup3_Eb_Not);
    REG_G3_EB(0xF6, 3, OpGroup3_Eb_Neg);
    REG_G3_EB(0xF6, 4, OpGroup3_Eb_Mul);
    REG_G3_EB(0xF6, 5, OpGroup3_Eb_Imul);

    // Group 3: 0xF7 (Ev) - Size + NF
    REG_EV_SPEC(0xF7, 2, OpGroup3_Ev_Not);
    REG_EV_SPEC(0xF7, 3, OpGroup3_Ev_Neg);
    REG_EV_SPEC(0xF7, 4, OpGroup3_Ev_Mul);
    REG_EV_SPEC(0xF7, 5, OpGroup3_Ev_Imul);
    REG_EV_SPEC(0xF7, 6, OpGroup3_Ev_Div);   // Only Size relevant
    REG_EV_SPEC(0xF7, 7, OpGroup3_Ev_Idiv);  // Only Size relevant

// Group 4: 0xFE (Eb) - INC/DEC
#define REG_G4_EB(opcode, subop, name)                                             \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        DispatchRegistrar<name##_Flags>::RegisterSpecializedAutoBoth(opcode, c);   \
    }                                                                              \
    {                                                                              \
        SpecCriteria c;                                                            \
        c.reg_mask = 7;                                                            \
        c.reg_val = subop;                                                         \
        c.no_flags = true;                                                         \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecializedAutoBoth(opcode, c); \
    }

    REG_G4_EB(0xFE, 0, OpGroup4_Eb_Inc);
    REG_G4_EB(0xFE, 1, OpGroup4_Eb_Dec);

    // Group 5: 0xFF (Ev)
    REG_EV_SPEC(0xFF, 0, OpGroup5_Ev_Inc);
    REG_EV_SPEC(0xFF, 1, OpGroup5_Ev_Dec);
    REG_EV_SPEC(0xFF, 2, OpGroup5_Ev_Call);
    REG_EV_SPEC(0xFF, 4, OpGroup5_Ev_Jmp);
    REG_EV_SPEC(0xFF, 6, OpGroup5_Ev_Push);

#undef REG_G1_EB
#undef REG_EV_SPEC
#undef REG_G3_EB
#undef REG_G4_EB
}

}  // namespace fiberish
