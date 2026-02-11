#pragma once
// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_alu.h"

namespace fiberish {

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdd_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 00: ADD r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, dest, src);

    // retry on TLB miss
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) {
        return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpAdd_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 01: ADD r/m16/32, r16/32
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, (op->modrm >> 3) & 7) & 0xFFFF;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdd_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 02: ADD r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdd_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 03: ADD r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 10: ADC r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdc<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 11: ADC r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdc<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluAdc<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 12: ADC r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdc<uint8_t, UpdateFlags>(state, dest, src);

    // Write back to reg8
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 13: ADC r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdc<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAdc<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 29: SUB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 20: AND r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 21: AND r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 22: AND r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 23: AND r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 09: OR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 31: XOR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpInc_Reg_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 40+rd: INC r16/32
    uint8_t reg = op->modrm & 7;

    // INC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, val, 1);
        if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = GetReg(state, reg);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, val, 1U);
        if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpDec_Reg_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 48+rd: DEC r16/32
    uint8_t reg = op->modrm & 7;

    // DEC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, val, 1);
        if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, val, 1U);
        if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdd_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 04: ADD AL, imm8
    uint8_t dest = GetReg8(state, EAX);  // AL is Reg 0 low byte
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, dest, src);

    // Write back to AL
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdd_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 05: ADD EAX, imm32
    if (op->prefixes.flags.opsize) {
        // ADD AX, imm16
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, dest, src);

        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        // ADD EAX, imm32
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 08: OR r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0A: OR r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0B: OR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0C: OR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpOr_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0D: OR EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 14: ADC AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdc<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAdc_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 15: ADC EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAdc<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAdc<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 18: SBB r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSbb<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_EvGv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 19: SBB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSbb<uint16_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = GetReg(state, reg);
        uint32_t res = AluSbb<uint32_t, UpdateFlags>(state, dest, src);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 1A: SBB r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSbb<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 1B: SBB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSbb<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluSbb<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 1C: SBB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSbb<uint8_t, UpdateFlags>(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSbb_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 1D: SBB EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluSbb<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluSbb<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 24: AND AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpAnd_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 25: AND EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 28: SUB r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 2A: SUB r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 2B: SUB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 2C: SUB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpSub_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 2D: SUB EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_EbGb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 30: XOR r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_GbEb_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 32: XOR r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, dest, src);
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_GvEv_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 33: XOR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_AlImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 34: XOR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <bool UpdateFlags>
FORCE_INLINE LogicFlow OpXor_EaxImm_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 35: XOR EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, dest, src);
        SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

namespace op {

// Wrappers for Add
FORCE_INLINE LogicFlow OpAdd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EvGv_Internal<false>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_EvGv_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EvGv_Internal<true, Specialized::RegEax>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdd_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdd_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Or
FORCE_INLINE LogicFlow OpOr_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpOr_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpOr_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpOr_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpOr_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpOr_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpOr_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpOr_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Adc
FORCE_INLINE LogicFlow OpAdc_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdc_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdc_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdc_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdc_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAdc_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAdc_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAdc_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Sbb
FORCE_INLINE LogicFlow OpSbb_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSbb_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSbb_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSbb_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSbb_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSbb_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSbb_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSbb_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for And
FORCE_INLINE LogicFlow OpAnd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAnd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAnd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAnd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAnd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpAnd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpAnd_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpAnd_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Sub
FORCE_INLINE LogicFlow OpSub_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSub_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSub_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSub_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSub_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpSub_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpSub_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpSub_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Xor
FORCE_INLINE LogicFlow OpXor_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EbGb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_EbGb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EbGb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXor_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EvGv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_EvGv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EvGv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXor_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_GbEb_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_GbEb_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_GbEb_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXor_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_GvEv_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_GvEv_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_GvEv_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXor_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_AlImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_AlImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_AlImm_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXor_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EaxImm_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpXor_EaxImm_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpXor_EaxImm_Internal<false>(state, op, utlb);
}

// Wrappers for Inc/Dec
FORCE_INLINE LogicFlow OpInc_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpInc_Reg_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpInc_Reg_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpInc_Reg_Internal<false>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpDec_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpDec_Reg_Internal<true>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpDec_Reg_NF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpDec_Reg_Internal<false>(state, op, utlb);
}

// Non-template functions
FORCE_INLINE LogicFlow OpCmp_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3C: CMP AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluSub(state, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmp_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3D: CMP EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        AluSub(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        AluSub(state, dest, src);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpTest_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 84: TEST r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluAnd(state, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpTest_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A8: TEST AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluAnd(state, dest, src);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpTest_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A9: TEST EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        AluAnd(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        AluAnd(state, dest, src);
    }
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
