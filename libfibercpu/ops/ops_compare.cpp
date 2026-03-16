#include "ops_compare_impl.h"

namespace fiberish {
void RegisterCompareOps() {
    using namespace op;

    g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;

#define REGISTER_OPSIZE(opcode, func_base)                                 \
    {                                                                      \
        g_Handlers[opcode] = DispatchWrapper<func_base>;                   \
        SpecCriteria c;                                                    \
        c.prefix_mask = 0x40;                                              \
        c.prefix_val = 0x40;                                               \
        DispatchRegistrar<func_base##_16>::RegisterSpecialized(opcode, c); \
        c.prefix_val = 0x00;                                               \
        DispatchRegistrar<func_base##_32>::RegisterSpecialized(opcode, c); \
    }

    // 39: CMP r/m16/32, r16/32
    REGISTER_OPSIZE(0x39, OpCmp_EvGv);
    {
        SpecCriteria c;
        c.mod_mask = 3;
        c.mod_val = 3;
        c.prefix_mask = 0x40;
        c.prefix_val = 0x00;
        DispatchRegistrar<op::OpCmp_EvGv_32_ModReg>::RegisterSpecialized(0x39, c);
    }

    g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;

    // 3B: CMP r16/32, r/m16/32
    REGISTER_OPSIZE(0x3B, OpCmp_GvEv);

    // 85: TEST r/m16/32, r16/32
    REGISTER_OPSIZE(0x85, OpTest_EvGv);
    {
        SpecCriteria c;
        c.mod_mask = 3;
        c.mod_val = 3;
        c.prefix_mask = 0x40;
        c.prefix_val = 0x00;
        DispatchRegistrar<op::OpTest_EvGv_32_ModReg>::RegisterSpecialized(0x85, c);
    }

    g_Handlers[0x1B0] = DispatchWrapper<OpCmpxchg_Byte>;  // 0F B0

    // 0F B1: CMPXCHG r/m, r
    {
        g_Handlers[0x1B1] = DispatchWrapper<OpCmpxchg_EvGv>;
        SpecCriteria c;
        c.prefix_mask = 0x40;
        c.prefix_val = 0x40;
        DispatchRegistrar<OpCmpxchg_EvGv_16>::RegisterSpecialized(0x1B1, c);
        c.prefix_val = 0x00;
        DispatchRegistrar<OpCmpxchg_EvGv_32>::RegisterSpecialized(0x1B1, c);
    }
#undef REGISTER_OPSIZE

    // SETcc (0F 9x)
    g_Handlers[0x190] = DispatchWrapper<OpSetcc_0>;
    g_Handlers[0x191] = DispatchWrapper<OpSetcc_1>;
    g_Handlers[0x192] = DispatchWrapper<OpSetcc_2>;
    g_Handlers[0x193] = DispatchWrapper<OpSetcc_3>;
    g_Handlers[0x194] = DispatchWrapper<OpSetcc_4>;
    g_Handlers[0x195] = DispatchWrapper<OpSetcc_5>;
    g_Handlers[0x196] = DispatchWrapper<OpSetcc_6>;
    g_Handlers[0x197] = DispatchWrapper<OpSetcc_7>;
    g_Handlers[0x198] = DispatchWrapper<OpSetcc_8>;
    g_Handlers[0x199] = DispatchWrapper<OpSetcc_9>;
    g_Handlers[0x19A] = DispatchWrapper<OpSetcc_10>;
    g_Handlers[0x19B] = DispatchWrapper<OpSetcc_11>;
    g_Handlers[0x19C] = DispatchWrapper<OpSetcc_12>;
    g_Handlers[0x19D] = DispatchWrapper<OpSetcc_13>;
    g_Handlers[0x19E] = DispatchWrapper<OpSetcc_14>;
    g_Handlers[0x19F] = DispatchWrapper<OpSetcc_15>;
}

}  // namespace fiberish
