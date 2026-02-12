#pragma once
// Double-Shift Instructions (SHLD/SHRD)
// Implements 0F A4/A5/AC/AD opcodes

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_double_shift.h"

namespace fiberish {
namespace op {

// SHLD: Double Precision Shift Left
// dest = (dest << count) | (src >> (32 - count))
FORCE_INLINE LogicFlow OpShld_EvGvIb(LogicFuncParams) {
    // 0F A4: SHLD r/m16/32, r16/32, imm8
    uint8_t count = imm & 0x1F;
    if (count == 0) return LogicFlow::Continue;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return LogicFlow::Continue;

        uint16_t res = (dest << count) | (src >> (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (16 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
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

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpShld_EvGvCl(LogicFuncParams) {
    // 0F A5: SHLD r/m16/32, r16/32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return LogicFlow::Continue;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return LogicFlow::Continue;

        uint16_t res = (dest << count) | (src >> (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (16 - count)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
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

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// SHRD: Double Precision Shift Right
// dest = (dest >> count) | (src << (32 - count))
FORCE_INLINE LogicFlow OpShrd_EvGvIb(LogicFuncParams) {
    // 0F AC: SHRD r/m16/32, r16/32, imm8
    uint8_t count = imm & 0x1F;
    if (count == 0) return LogicFlow::Continue;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return LogicFlow::Continue;

        uint16_t res = (dest >> count) | (src << (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
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

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpShrd_EvGvCl(LogicFuncParams) {
    // 0F AD: SHRD r/m16/32, r16/32, CL
    uint8_t count = GetReg(state, ECX) & 0x1F;
    if (count == 0) return LogicFlow::Continue;

    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        count &= 0x0F;
        if (count == 0) return LogicFlow::Continue;

        uint16_t res = (dest >> count) | (src << (16 - count));
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dest >> (count - 1)) & 1) flags |= CF_MASK;
        if (res == 0) flags |= ZF_MASK;
        if (res & 0x8000) flags |= SF_MASK;
        if (Parity(res & 0xFF)) flags |= PF_MASK;
        if (count == 1 && ((dest ^ res) & 0x8000)) flags |= OF_MASK;
        state->ctx.eflags = flags;

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
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

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
