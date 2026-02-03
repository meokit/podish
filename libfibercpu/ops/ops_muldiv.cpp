// Multiplication & Division
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpImul_GvEv(EmuState* state, DecodedOp* op) {
    // 0F AF: IMUL r32, r/m32
    int32_t val1 = (int32_t)GetReg(state, (op->modrm >> 3) & 7);
    int32_t val2 = (int32_t)ReadModRM32(state, op);

    int64_t res = (int64_t)val1 * (int64_t)val2;
    uint32_t res32 = (uint32_t)res;
    SetReg(state, (op->modrm >> 3) & 7, res32);

    // Set OF/CF if result truncated
    if (res != (int64_t)(int32_t)res) {
        state->ctx.eflags |= (OF_MASK | CF_MASK);
    } else {
        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
}

static FORCE_INLINE void OpImul_GvEvIz(EmuState* state, DecodedOp* op) {
    // 69: IMUL r32, r/m32, imm32
    // 6B: IMUL r32, r/m32, imm8
    int32_t val1 = (int32_t)ReadModRM32(state, op);
    int32_t val2 = (int32_t)op->imm;

    if ((op->handler_index & 0xFF) == 0x6B) {
        val2 = (int32_t)(int8_t)val2;
    }

    int64_t res = (int64_t)val1 * (int64_t)val2;
    uint32_t res32 = (uint32_t)res;
    SetReg(state, (op->modrm >> 3) & 7, res32);

    if (res != (int64_t)(int32_t)res) {
        state->ctx.eflags |= (OF_MASK | CF_MASK);
    } else {
        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
}

void RegisterMulDivOps() {
    g_Handlers[0x69] = DispatchWrapper<OpImul_GvEvIz>;
    g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIz>;
    g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
}

}  // namespace x86emu