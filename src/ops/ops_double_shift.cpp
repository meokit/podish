// Double-Shift Instructions (SHLD/SHRD)
// Implements 0F A4/A5/AC/AD opcodes

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

// SHLD: Double Precision Shift Left
// dest = (dest << count) | (src >> (32 - count))
static FORCE_INLINE void OpShld_EvGvIb(EmuState* state, DecodedOp* op) {
    // 0F A4: SHLD r/m32, r32, imm8
    uint8_t count = op->imm & 0x1F;  // Mask to 5 bits
    if (count == 0) return;          // No operation

    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);

    // Perform double-precision shift left
    uint32_t res = (dest << count) | (src >> (32 - count));

    // Update flags
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);

    // CF: Last bit shifted out from dest
    bool cf = (dest >> (32 - count)) & 1;
    if (cf) flags |= CF_MASK;

    // Standard flags
    if (res == 0) flags |= ZF_MASK;
    if (res & 0x80000000) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;

    // OF: For count=1, OF = MSB changed
    if (count == 1) {
        bool msb_orig = dest & 0x80000000;
        bool msb_res = res & 0x80000000;
        if (msb_orig != msb_res) flags |= OF_MASK;
    }

    state->ctx.eflags = flags;
    WriteModRM32(state, op, res);
}

static FORCE_INLINE void OpShld_EvGvCl(EmuState* state, DecodedOp* op) {
    // 0F A5: SHLD r/m32, r32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return;

    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);

    uint32_t res = (dest << count) | (src >> (32 - count));

    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);

    bool cf = (dest >> (32 - count)) & 1;
    if (cf) flags |= CF_MASK;

    if (res == 0) flags |= ZF_MASK;
    if (res & 0x80000000) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;

    if (count == 1) {
        bool msb_orig = dest & 0x80000000;
        bool msb_res = res & 0x80000000;
        if (msb_orig != msb_res) flags |= OF_MASK;
    }

    state->ctx.eflags = flags;
    WriteModRM32(state, op, res);
}

// SHRD: Double Precision Shift Right
// dest = (dest >> count) | (src << (32 - count))
static FORCE_INLINE void OpShrd_EvGvIb(EmuState* state, DecodedOp* op) {
    // 0F AC: SHRD r/m32, r32, imm8
    uint8_t count = op->imm & 0x1F;
    if (count == 0) return;

    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);

    uint32_t res = (dest >> count) | (src << (32 - count));

    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);

    // CF: Last bit shifted out
    bool cf = (dest >> (count - 1)) & 1;
    if (cf) flags |= CF_MASK;

    if (res == 0) flags |= ZF_MASK;
    if (res & 0x80000000) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;

    // OF: For count=1, OF = MSB changed
    if (count == 1) {
        bool msb_orig = dest & 0x80000000;
        bool msb_res = res & 0x80000000;
        if (msb_orig != msb_res) flags |= OF_MASK;
    }

    state->ctx.eflags = flags;
    WriteModRM32(state, op, res);
}

static FORCE_INLINE void OpShrd_EvGvCl(EmuState* state, DecodedOp* op) {
    // 0F AD: SHRD r/m32, r32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return;

    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);

    uint32_t res = (dest >> count) | (src << (32 - count));

    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);

    bool cf = (dest >> (count - 1)) & 1;
    if (cf) flags |= CF_MASK;

    if (res == 0) flags |= ZF_MASK;
    if (res & 0x80000000) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;

    if (count == 1) {
        if (dest & 0x80000000) flags |= OF_MASK;
    }

    state->ctx.eflags = flags;
    WriteModRM32(state, op, res);
}

void RegisterDoubleShiftOps() {
    g_Handlers[0x1A4] = DispatchWrapper<OpShld_EvGvIb>;  // 0F A4: SHLD r/m32, r32, imm8
    g_Handlers[0x1A5] = DispatchWrapper<OpShld_EvGvCl>;  // 0F A5: SHLD r/m32, r32, CL
    g_Handlers[0x1AC] = DispatchWrapper<OpShrd_EvGvIb>;  // 0F AC: SHRD r/m32, r32, imm8
    g_Handlers[0x1AD] = DispatchWrapper<OpShrd_EvGvCl>;  // 0F AD: SHRD r/m32, r32, CL
}

}  // namespace x86emu