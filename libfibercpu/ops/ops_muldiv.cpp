// Multiplication & Division
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

static FORCE_INLINE void OpImul_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AF: IMUL r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        int16_t val1 = (int16_t)GetReg(state, reg);
        auto res_md = ReadModRM16(state, op, utlb);
        if (!res_md) return;
        int16_t val2 = (int16_t)*res_md;
        int32_t res = (int32_t)val1 * (int32_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);
        if (res != (int32_t)(int16_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    } else {
        int32_t val1 = (int32_t)GetReg(state, reg);
        auto res_md = ReadModRM32(state, op, utlb);
        if (!res_md) return;
        int32_t val2 = (int32_t)*res_md;
        int64_t res = (int64_t)val1 * (int64_t)val2;
        SetReg(state, reg, (uint32_t)res);
        if (res != (int64_t)(int32_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    }
}

static FORCE_INLINE void OpImul_GvEvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 69: IMUL r16/32, r/m16/32, imm16/32
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.opsize) {
        auto res_md = ReadModRM16(state, op, utlb);
        if (!res_md) return;
        int16_t val1 = (int16_t)*res_md;
        int16_t val2 = (int16_t)op->imm;

        int32_t res = (int32_t)val1 * (int32_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);
        if (res != (int32_t)(int16_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    } else {
        auto res_md = ReadModRM32(state, op, utlb);
        if (!res_md) return;
        int32_t val1 = (int32_t)*res_md;
        int32_t val2 = (int32_t)op->imm;

        int64_t res = (int64_t)val1 * (int64_t)val2;
        SetReg(state, reg, (uint32_t)res);
        if (res != (int64_t)(int32_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    }
}

static FORCE_INLINE void OpImul_GvEvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 6B: IMUL r16/32, r/m16/32, imm8
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.opsize) {
        auto res_md = ReadModRM16(state, op, utlb);
        if (!res_md) return;
        int16_t val1 = (int16_t)*res_md;
        int16_t val2 = (int16_t)(int8_t)op->imm;

        int32_t res = (int32_t)val1 * (int32_t)val2;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)res);
        if (res != (int32_t)(int16_t)res) {
            state->ctx.eflags |= (OF_MASK | CF_MASK);
        } else {
            state->ctx.eflags &= ~(OF_MASK | CF_MASK);
        }
    } else {
        auto res_md = ReadModRM32(state, op, utlb);
        if (!res_md) return;
        int32_t val1 = (int32_t)*res_md;
        int32_t val2 = (int32_t)(int8_t)op->imm;

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
    g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIb>;
    g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
}

}  // namespace fiberish