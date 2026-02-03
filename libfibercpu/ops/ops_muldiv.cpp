// Multiplication & Division
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpImul_GvEv(EmuState* state, DecodedOp* op) {
    // 0F AF: IMUL r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        int16_t val1 = (int16_t)GetReg(state, reg);
        int16_t val2 = (int16_t)ReadModRM16(state, op);
        int32_t res = (int32_t)val1 * (int32_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);
        if (res != (int32_t)(int16_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    } else {
        int32_t val1 = (int32_t)GetReg(state, reg);
        int32_t val2 = (int32_t)ReadModRM32(state, op);
        int64_t res = (int64_t)val1 * (int64_t)val2;
        SetReg(state, reg, (uint32_t)res);
        if (res != (int64_t)(int32_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    }
}

static FORCE_INLINE void OpImul_GvEvIz(EmuState* state, DecodedOp* op) {
    // 69: IMUL r16/32, r/m16/32, imm16/32
    // 6B: IMUL r16/32, r/m16/32, imm8
    uint8_t reg = (op->modrm >> 3) & 7;
    bool is_imm8 = ((op->handler_index & 0xFF) == 0x6B);

    if (op->prefixes.flags.opsize) {
        int16_t val1 = (int16_t)ReadModRM16(state, op);
        int16_t val2 = (int16_t)op->imm;
        if (is_imm8) val2 = (int16_t)(int8_t)val2;

        int32_t res = (int32_t)val1 * (int32_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);
        if (res != (int32_t)(int16_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    } else {
        int32_t val1 = (int32_t)ReadModRM32(state, op);
        int32_t val2 = (int32_t)op->imm;
        if (is_imm8) val2 = (int32_t)(int8_t)val2;

        int64_t res = (int64_t)val1 * (int64_t)val2;
        SetReg(state, reg, (uint32_t)res);
        if (res != (int64_t)(int32_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    }
}

void RegisterMulDivOps() {
    g_Handlers[0x69] = DispatchWrapper<OpImul_GvEvIz>;
    g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIz>;
    g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
}

}  // namespace x86emu