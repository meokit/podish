// Comparison & Test
// Auto-generated specialization refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_compare.h"

namespace fiberish {

// ============================================================================
// Specializations for OpCmp_EvGv (39: CMP r/m16/32, r16/32)
// ============================================================================

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpCmp_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 39: CMP r/m16/32, r16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluCmp<uint16_t>(state, dest, src);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluCmp<uint32_t>(state, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmp_GvEv (3B: CMP r16/32, r/m16/32)
// ============================================================================

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpCmp_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3B: CMP r16/32, r/m16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        uint16_t dest = (uint16_t)GetReg(state, reg);
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;
        AluCmp<uint16_t>(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, reg);
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;
        AluCmp<uint32_t>(state, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpTest_EvGv (85: TEST r/m16/32, r16/32)
// ============================================================================

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpTest_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 85: TEST r/m16/32, r16/32
    bool opsize;
    if constexpr (S == Specialized::Opsize16) {
        opsize = true;
    } else if constexpr (S == Specialized::Opsize32) {
        opsize = false;
    } else {
        opsize = op->prefixes.flags.opsize;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluAnd<uint16_t>(state, dest, src);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluAnd<uint32_t>(state, dest, src);
    }
    return LogicFlow::Continue;
}

// ============================================================================
// Specializations for OpCmpxchg (0F B0/B1: CMPXCHG r/m, r)
// ============================================================================

template <bool IsByte, Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpCmpxchg_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B0: CMPXCHG r/m8, r8
    // 0F B1: CMPXCHG r/m, r

    if constexpr (IsByte) {
        // Byte version: always 8-bit
        auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint8_t dest = *dest_res;
        uint8_t al = state->ctx.regs[EAX] & 0xFF;
        uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
        AluCmp<uint8_t>(state, al, dest);
        if (al == dest) {
            if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
        } else {
            state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFFFF00) | dest;
        }
    } else {
        bool opsize;
        if constexpr (S == Specialized::Opsize16) {
            opsize = true;
        } else if constexpr (S == Specialized::Opsize32) {
            opsize = false;
        } else {
            opsize = op->prefixes.flags.opsize;
        }

        if (opsize) {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint16_t dest = *dest_res;
            uint16_t ax = state->ctx.regs[EAX] & 0xFFFF;
            uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint16_t>(state, ax, dest);
            if (ax == dest) {
                if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
            } else {
                state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFF0000) | dest;
            }
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            uint32_t dest = *dest_res;
            uint32_t eax = state->ctx.regs[EAX];
            uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint32_t>(state, eax, dest);
            if (eax == dest) {
                if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
            } else {
                state->ctx.regs[EAX] = dest;
            }
        }
    }
    return LogicFlow::Continue;
}

template <uint8_t Cond>
static FORCE_INLINE LogicFlow OpSetcc_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 9x: SETcc r/m8
    uint8_t val = CheckCondition(state, Cond) ? 1 : 0;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) {
        return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpCmp_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_EvGv_Internal<Specialized::Opsize16>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmp_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_EvGv_Internal<Specialized::Opsize32>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmp_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_EvGv_Internal<Specialized::None>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpCmp_GvEv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_GvEv_Internal<Specialized::Opsize16>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmp_GvEv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_GvEv_Internal<Specialized::Opsize32>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmp_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmp_GvEv_Internal<Specialized::None>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpTest_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpTest_EvGv_Internal<Specialized::Opsize16>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpTest_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpTest_EvGv_Internal<Specialized::Opsize32>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpTest_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpTest_EvGv_Internal<Specialized::None>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpCmpxchg_EvGv_16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmpxchg_Internal<false, Specialized::Opsize16>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmpxchg_EvGv_32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmpxchg_Internal<false, Specialized::Opsize32>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmpxchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmpxchg_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpCmpxchg_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpCmpxchg_Internal<true>(state, op, utlb);
}

// ============================================================================
// Non-size-specific functions (unchanged)
// ============================================================================

FORCE_INLINE LogicFlow OpCmp_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 38: CMP r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluCmp<uint8_t>(state, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmp_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;
    AluCmp<uint8_t>(state, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSetcc_0(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<0>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_1(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<1>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<2>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_3(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<3>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_4(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<4>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_5(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<5>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_6(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<6>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_7(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<7>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<8>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<9>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_10(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<10>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_11(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<11>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_12(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<12>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_13(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<13>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_14(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<14>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSetcc_15(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSetcc_Internal<15>(state, op, utlb);
}

}  // namespace op

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

    g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;

    // 3B: CMP r16/32, r/m16/32
    REGISTER_OPSIZE(0x3B, OpCmp_GvEv);

    // 85: TEST r/m16/32, r16/32
    REGISTER_OPSIZE(0x85, OpTest_EvGv);

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