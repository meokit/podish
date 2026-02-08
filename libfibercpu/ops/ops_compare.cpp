// Comparison & Test
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

static FORCE_INLINE void OpCmp_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 38: CMP r/m8, r8
    auto dest_res = ReadModRM8(state, op, utlb);
    if (!dest_res) return;
    uint8_t dest = *dest_res;
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluCmp<uint8_t>(state, dest, src);
}

static FORCE_INLINE void OpCmp_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 39: CMP r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluCmp<uint16_t>(state, dest, src);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluCmp<uint32_t>(state, dest, src);
    }
}

static FORCE_INLINE void OpCmp_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    auto src_res = ReadModRM8(state, op, utlb);
    if (!src_res) return;
    uint8_t src = *src_res;
    AluCmp<uint8_t>(state, dest, src);
}

static FORCE_INLINE void OpCmp_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3B: CMP r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = (uint16_t)GetReg(state, reg);
        auto src_res = ReadModRM16(state, op, utlb);
        if (!src_res) return;
        uint16_t src = *src_res;
        AluCmp<uint16_t>(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, reg);
        auto src_res = ReadModRM32(state, op, utlb);
        if (!src_res) return;
        uint32_t src = *src_res;
        AluCmp<uint32_t>(state, dest, src);
    }
}

static FORCE_INLINE void OpTest_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 85: TEST r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluAnd<uint16_t>(state, dest, src);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, reg);
        AluAnd<uint32_t>(state, dest, src);
    }
}

static FORCE_INLINE void OpSetcc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F 9x: SETcc r/m8
    uint8_t cond = op->extra;
    uint8_t val = CheckCondition(state, cond) ? 1 : 0;
    if (!WriteModRM8(state, op, val, utlb)) return;
}

template <bool IsByte>
static FORCE_INLINE void OpCmpxchg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B0: CMPXCHG r/m8, r8
    // 0F B1: CMPXCHG r/m, r
    bool opsize = op->prefixes.flags.opsize;

    if constexpr (IsByte) {
        auto dest_res = ReadModRM8(state, op, utlb);
        if (!dest_res) return;
        uint8_t dest = *dest_res;
        uint8_t al = state->ctx.regs[EAX] & 0xFF;
        uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
        AluCmp<uint8_t>(state, al, dest);
        if (al == dest) {
            if (!WriteModRM8(state, op, src, utlb)) return;
        } else {
            state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFFFF00) | dest;
        }
    } else {
        if (opsize) {
            auto dest_res = ReadModRM16(state, op, utlb);
            if (!dest_res) return;
            uint16_t dest = *dest_res;
            uint16_t ax = state->ctx.regs[EAX] & 0xFFFF;
            uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint16_t>(state, ax, dest);
            if (ax == dest) {
                if (!WriteModRM16(state, op, src, utlb)) return;
            } else {
                state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFF0000) | dest;
            }
        } else {
            auto dest_res = ReadModRM32(state, op, utlb);
            if (!dest_res) return;
            uint32_t dest = *dest_res;
            uint32_t eax = state->ctx.regs[EAX];
            uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
            AluCmp<uint32_t>(state, eax, dest);
            if (eax == dest) {
                if (!WriteModRM32(state, op, src, utlb)) return;
            } else {
                state->ctx.regs[EAX] = dest;
            }
        }
    }
}

void RegisterCompareOps() {
    g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;
    g_Handlers[0x39] = DispatchWrapper<OpCmp_EvGv>;
    g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;
    g_Handlers[0x3B] = DispatchWrapper<OpCmp_GvEv>;
    g_Handlers[0x85] = DispatchWrapper<OpTest_EvGv>;
    g_Handlers[0x1B0] = DispatchWrapper<OpCmpxchg<true>>;   // 0F B0
    g_Handlers[0x1B1] = DispatchWrapper<OpCmpxchg<false>>;  // 0F B1

    for (int i = 0; i < 16; ++i) {
        g_Handlers[0x190 + i] = DispatchWrapper<OpSetcc>;  // SETcc (0F 9x)
    }
}

}  // namespace fiberish