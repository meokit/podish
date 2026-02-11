// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

template <bool UpdateFlags>
static FORCE_INLINE LogicFlow OpAdd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpInc_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 40+rd: INC r16/32
    // Opcode 40-47. Reg is opcode & 7.
    // We don't store opcode. But we store modrm? No, 40-47 has no modrm.
    // We lost the register index!
    // DecodedOp must store the register index for 1-byte opcodes if we don't have
    // handler_index. Solution: We can use `modrm` field to store the low 3 bits
    // of opcode for non-ModRM instructions? Let's check `DecodeInstruction`. In
    // `DecodeInstruction`: `uint8_t has_modrm = kHasModRM[map][opcode];` If
    // !has_modrm, op->modrm is unused (0). We can reuse op->modrm to store opcode
    // or reg index!

    // For now, let's assume we fix DecodeInstruction to put opcode & 7 into modrm
    // for this group.
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
static FORCE_INLINE LogicFlow OpDec_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpOr_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAdc_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSbb_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpAnd_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpSub_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
static FORCE_INLINE LogicFlow OpXor_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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

static FORCE_INLINE LogicFlow OpCmp_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 3C: CMP AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluSub(state, dest, src);
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpCmp_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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

static FORCE_INLINE LogicFlow OpTest_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 84: TEST r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluAnd(state, dest, src);
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpTest_AlImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A8: TEST AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluAnd(state, dest, src);
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpTest_EaxImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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

void RegisterAluOps() {
    g_Handlers[0x00] = DispatchWrapper<OpAdd_EbGb<true>>;
    DispatchRegistrar<OpAdd_EbGb<false>>::RegisterNF(0x00);

    // 01: ADD r/m16/32, r16/32
    DispatchRegistrar<OpAdd_EvGv<true>>::Register(0x01);
    DispatchRegistrar<OpAdd_EvGv<false>>::RegisterNF(0x01);

    // Specialization: ADD EAX, r32 (Mod=3, RM=0)
    // OpAdd_EvGv<true, Specialized::RegEax>
    SpecCriteria criteria;
    criteria.mod_mask = 0xC0;
    criteria.mod_val = 0xC0;  // Mod=3 (Reg)
    criteria.rm_mask = 0x07;
    criteria.rm_val = 0x00;  // RM=0 (EAX)
    DispatchRegistrar<OpAdd_EvGv<true, Specialized::RegEax>>::RegisterSpecialized(0x01, criteria);

    g_Handlers[0x02] = DispatchWrapper<OpAdd_GbEb<true>>;
    DispatchRegistrar<OpAdd_GbEb<false>>::RegisterNF(0x02);

    g_Handlers[0x03] = DispatchWrapper<OpAdd_GvEv<true>>;
    DispatchRegistrar<OpAdd_GvEv<false>>::RegisterNF(0x03);

    g_Handlers[0x04] = DispatchWrapper<OpAdd_AlImm<true>>;
    DispatchRegistrar<OpAdd_AlImm<false>>::RegisterNF(0x04);

    g_Handlers[0x05] = DispatchWrapper<OpAdd_EaxImm<true>>;
    DispatchRegistrar<OpAdd_EaxImm<false>>::RegisterNF(0x05);

    g_Handlers[0x08] = DispatchWrapper<OpOr_EbGb<true>>;
    DispatchRegistrar<OpOr_EbGb<false>>::RegisterNF(0x08);

    g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv<true>>;
    DispatchRegistrar<OpOr_EvGv<false>>::RegisterNF(0x09);

    g_Handlers[0x0A] = DispatchWrapper<OpOr_GbEb<true>>;
    DispatchRegistrar<OpOr_GbEb<false>>::RegisterNF(0x0A);

    g_Handlers[0x0B] = DispatchWrapper<OpOr_GvEv<true>>;
    DispatchRegistrar<OpOr_GvEv<false>>::RegisterNF(0x0B);

    g_Handlers[0x0C] = DispatchWrapper<OpOr_AlImm<true>>;
    DispatchRegistrar<OpOr_AlImm<false>>::RegisterNF(0x0C);

    g_Handlers[0x0D] = DispatchWrapper<OpOr_EaxImm<true>>;
    DispatchRegistrar<OpOr_EaxImm<false>>::RegisterNF(0x0D);

    // ADC/SBB must always update/read flags, so no NF optimization for them
    // directly unless we prove they don't read CF (which is their whole point).
    // So we skip NF registration for ADC/SBB usually, OR we register them if they
    // happen to not update other flags? Wait, ADC reads CF but writes all flags.
    // If the write is dead, we can skip writing them. Yes, OpAdc_NF would read CF
    // but not write result flags.

    g_Handlers[0x10] = DispatchWrapper<OpAdc_EbGb<true>>;
    DispatchRegistrar<OpAdc_EbGb<false>>::RegisterNF(0x10);

    g_Handlers[0x11] = DispatchWrapper<OpAdc_EvGv<true>>;
    DispatchRegistrar<OpAdc_EvGv<false>>::RegisterNF(0x11);

    g_Handlers[0x12] = DispatchWrapper<OpAdc_GbEb<true>>;
    DispatchRegistrar<OpAdc_GbEb<false>>::RegisterNF(0x12);

    g_Handlers[0x13] = DispatchWrapper<OpAdc_GvEv<true>>;
    DispatchRegistrar<OpAdc_GvEv<false>>::RegisterNF(0x13);

    g_Handlers[0x14] = DispatchWrapper<OpAdc_AlImm<true>>;
    DispatchRegistrar<OpAdc_AlImm<false>>::RegisterNF(0x14);

    g_Handlers[0x15] = DispatchWrapper<OpAdc_EaxImm<true>>;
    DispatchRegistrar<OpAdc_EaxImm<false>>::RegisterNF(0x15);

    g_Handlers[0x18] = DispatchWrapper<OpSbb_EbGb<true>>;
    DispatchRegistrar<OpSbb_EbGb<false>>::RegisterNF(0x18);

    g_Handlers[0x19] = DispatchWrapper<OpSbb_EvGv<true>>;
    DispatchRegistrar<OpSbb_EvGv<false>>::RegisterNF(0x19);

    g_Handlers[0x1A] = DispatchWrapper<OpSbb_GbEb<true>>;
    DispatchRegistrar<OpSbb_GbEb<false>>::RegisterNF(0x1A);

    g_Handlers[0x1B] = DispatchWrapper<OpSbb_GvEv<true>>;
    DispatchRegistrar<OpSbb_GvEv<false>>::RegisterNF(0x1B);

    g_Handlers[0x1C] = DispatchWrapper<OpSbb_AlImm<true>>;
    DispatchRegistrar<OpSbb_AlImm<false>>::RegisterNF(0x1C);

    g_Handlers[0x1D] = DispatchWrapper<OpSbb_EaxImm<true>>;
    DispatchRegistrar<OpSbb_EaxImm<false>>::RegisterNF(0x1D);

    g_Handlers[0x20] = DispatchWrapper<OpAnd_EbGb<true>>;
    DispatchRegistrar<OpAnd_EbGb<false>>::RegisterNF(0x20);

    g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv<true>>;
    DispatchRegistrar<OpAnd_EvGv<false>>::RegisterNF(0x21);

    g_Handlers[0x22] = DispatchWrapper<OpAnd_GbEb<true>>;
    DispatchRegistrar<OpAnd_GbEb<false>>::RegisterNF(0x22);

    g_Handlers[0x23] = DispatchWrapper<OpAnd_GvEv<true>>;
    DispatchRegistrar<OpAnd_GvEv<false>>::RegisterNF(0x23);

    g_Handlers[0x24] = DispatchWrapper<OpAnd_AlImm<true>>;
    DispatchRegistrar<OpAnd_AlImm<false>>::RegisterNF(0x24);

    g_Handlers[0x25] = DispatchWrapper<OpAnd_EaxImm<true>>;
    DispatchRegistrar<OpAnd_EaxImm<false>>::RegisterNF(0x25);

    g_Handlers[0x28] = DispatchWrapper<OpSub_EbGb<true>>;
    DispatchRegistrar<OpSub_EbGb<false>>::RegisterNF(0x28);

    g_Handlers[0x29] = DispatchWrapper<OpSub_EvGv<true>>;
    DispatchRegistrar<OpSub_EvGv<false>>::RegisterNF(0x29);

    g_Handlers[0x2A] = DispatchWrapper<OpSub_GbEb<true>>;
    DispatchRegistrar<OpSub_GbEb<false>>::RegisterNF(0x2A);

    g_Handlers[0x2B] = DispatchWrapper<OpSub_GvEv<true>>;
    DispatchRegistrar<OpSub_GvEv<false>>::RegisterNF(0x2B);

    g_Handlers[0x2C] = DispatchWrapper<OpSub_AlImm<true>>;
    DispatchRegistrar<OpSub_AlImm<false>>::RegisterNF(0x2C);

    g_Handlers[0x2D] = DispatchWrapper<OpSub_EaxImm<true>>;
    DispatchRegistrar<OpSub_EaxImm<false>>::RegisterNF(0x2D);

    g_Handlers[0x30] = DispatchWrapper<OpXor_EbGb<true>>;
    DispatchRegistrar<OpXor_EbGb<false>>::RegisterNF(0x30);

    g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv<true>>;
    DispatchRegistrar<OpXor_EvGv<false>>::RegisterNF(0x31);

    g_Handlers[0x32] = DispatchWrapper<OpXor_GbEb<true>>;
    DispatchRegistrar<OpXor_GbEb<false>>::RegisterNF(0x32);

    g_Handlers[0x33] = DispatchWrapper<OpXor_GvEv<true>>;
    DispatchRegistrar<OpXor_GvEv<false>>::RegisterNF(0x33);

    g_Handlers[0x34] = DispatchWrapper<OpXor_AlImm<true>>;
    DispatchRegistrar<OpXor_AlImm<false>>::RegisterNF(0x34);

    g_Handlers[0x35] = DispatchWrapper<OpXor_EaxImm<true>>;
    DispatchRegistrar<OpXor_EaxImm<false>>::RegisterNF(0x35);

    g_Handlers[0x3C] = DispatchWrapper<OpCmp_AlImm>;
    g_Handlers[0x3D] = DispatchWrapper<OpCmp_EaxImm>;
    g_Handlers[0x84] = DispatchWrapper<OpTest_EbGb>;
    g_Handlers[0xA8] = DispatchWrapper<OpTest_AlImm>;
    g_Handlers[0xA9] = DispatchWrapper<OpTest_EaxImm>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x40 + i] = DispatchWrapper<OpInc_Reg<true>>;
        DispatchRegistrar<OpInc_Reg<false>>::RegisterNF(0x40 + i);

        g_Handlers[0x48 + i] = DispatchWrapper<OpDec_Reg<true>>;
        DispatchRegistrar<OpDec_Reg<false>>::RegisterNF(0x48 + i);
    }
}
}  // namespace fiberish
