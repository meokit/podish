// Double-Shift Instructions (SHLD/SHRD)
// Implements 0F A4/A5/AC/AD opcodes

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

// SHLD: Double Precision Shift Left
// dest = (dest << count) | (src >> (32 - count))
static FORCE_INLINE void OpShld_EvGvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F A4: SHLD r/m16/32, r16/32, imm8
    uint8_t count = op->imm & 0x1F;
    if (count == 0) return;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return;

        uint16_t res = (dest << count) | (src >> (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (16 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM16(state, op, res, utlb);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = (dest << count) | (src >> (32 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (32 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x80000000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x80000000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM32(state, op, res, utlb);
    }
}

static FORCE_INLINE void OpShld_EvGvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F A5: SHLD r/m16/32, r16/32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return;

        uint16_t res = (dest << count) | (src >> (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (16 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM16(state, op, res, utlb);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = (dest << count) | (src >> (32 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (32 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x80000000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x80000000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM32(state, op, res, utlb);
    }
}

// SHRD: Double Precision Shift Right
// dest = (dest >> count) | (src << (32 - count))
static FORCE_INLINE void OpShrd_EvGvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AC: SHRD r/m16/32, r16/32, imm8
    uint8_t count = op->imm & 0x1F;
    if (count == 0) return;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return;

        uint16_t res = (dest >> count) | (src << (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM16(state, op, res, utlb);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = (dest >> count) | (src << (32 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x80000000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x80000000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM32(state, op, res, utlb);
    }
}

static FORCE_INLINE void OpShrd_EvGvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AD: SHRD r/m16/32, r16/32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM16(state, op, utlb);
        if (!dest_res) return;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return;

        uint16_t res = (dest >> count) | (src << (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM16(state, op, res, utlb);
    } else {
        auto dest_res = ReadModRM32(state, op, utlb);
        if (!dest_res) return;
        uint32_t dest = *dest_res;
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = (dest >> count) | (src << (32 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x80000000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x80000000)) flags |= OF_MASK;
        state->ctx.eflags = flags;
        WriteModRM32(state, op, res, utlb);
    }
}

void RegisterDoubleShiftOps() {
    g_Handlers[0x1A4] = DispatchWrapper<OpShld_EvGvIb>;
    g_Handlers[0x1A5] = DispatchWrapper<OpShld_EvGvCl>;
    g_Handlers[0x1AC] = DispatchWrapper<OpShrd_EvGvIb>;
    g_Handlers[0x1AD] = DispatchWrapper<OpShrd_EvGvCl>;
}

}  // namespace fiberish