// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpAdd_EbGb(EmuState* state, DecodedOp* op) {
    // 00: ADD r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdd(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpAdd_EvGv(EmuState* state, DecodedOp* op) {
    // 01: ADD r/m16/32, r16/32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, (op->modrm >> 3) & 7) & 0xFFFF;
        uint16_t res = AluAdd(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = AluAdd(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpAdd_GbEb(EmuState* state, DecodedOp* op) {
    // 02: ADD r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdd(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpAdd_GvEv(EmuState* state, DecodedOp* op) {
    // 03: ADD r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdd(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAdd(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpAdc_EbGb(EmuState* state, DecodedOp* op) {
    // 10: ADC r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdc(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpAdc_EvGv(EmuState* state, DecodedOp* op) {
    // 11: ADC r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdc(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluAdc(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpAdc_GbEb(EmuState* state, DecodedOp* op) {
    // 12: ADC r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdc(state, dest, src);

    // Write back to reg8
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpAdc_GvEv(EmuState* state, DecodedOp* op) {
    // 13: ADC r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdc(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAdc(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpSub_EvGv(EmuState* state, DecodedOp* op) {
    // 29: SUB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluSub(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpAnd_EbGb(EmuState* state, DecodedOp* op) {
    // 20: AND r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpAnd_EvGv(EmuState* state, DecodedOp* op) {
    // 21: AND r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluAnd(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpAnd_GbEb(EmuState* state, DecodedOp* op) {
    // 22: AND r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAnd(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpAnd_GvEv(EmuState* state, DecodedOp* op) {
    // 23: AND r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluAnd(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpOr_EvGv(EmuState* state, DecodedOp* op) {
    // 09: OR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluOr(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpXor_EvGv(EmuState* state, DecodedOp* op) {
    // 31: XOR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluXor(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpInc_Reg(EmuState* state, DecodedOp* op) {
    // 40+rd: INC r16/32
    uint8_t reg = op->handler_index & 7;
    
    // INC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        uint16_t res = AluAdd<uint16_t>(state, val, 1);
        state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = GetReg(state, reg);
        uint32_t res = AluAdd<uint32_t>(state, val, 1U);
        state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpDec_Reg(EmuState* state, DecodedOp* op) {
    // 48+rd: DEC r16/32
    uint8_t reg = op->handler_index & 7;

    // DEC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        uint16_t res = AluSub<uint16_t>(state, val, 1);
        state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = GetReg(state, reg);
        uint32_t res = AluSub<uint32_t>(state, val, 1U);
        state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpAdd_AlImm(EmuState* state, DecodedOp* op) {
    // 04: ADD AL, imm8
    uint8_t dest = GetReg8(state, EAX);  // AL is Reg 0 low byte
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdd(state, dest, src);

    // Write back to AL
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpAdd_EaxImm(EmuState* state, DecodedOp* op) {
    // 05: ADD EAX, imm32
    if (op->prefixes.flags.opsize) {
        // ADD AX, imm16
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAdd(state, dest, src);

        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        // ADD EAX, imm32
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAdd(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpOr_EbGb(EmuState* state, DecodedOp* op) {
    // 08: OR r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluOr(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpOr_GbEb(EmuState* state, DecodedOp* op) {
    // 0A: OR r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluOr(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpOr_GvEv(EmuState* state, DecodedOp* op) {
    // 0B: OR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluOr(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpOr_AlImm(EmuState* state, DecodedOp* op) {
    // 0C: OR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluOr(state, dest, src);

    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpOr_EaxImm(EmuState* state, DecodedOp* op) {
    // 0D: OR EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluOr(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluOr(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpAdc_AlImm(EmuState* state, DecodedOp* op) {
    // 14: ADC AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdc(state, dest, src);

    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpAdc_EaxImm(EmuState* state, DecodedOp* op) {
    // 15: ADC EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAdc(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAdc(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpSbb_EbGb(EmuState* state, DecodedOp* op) {
    // 18: SBB r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSbb(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpSbb_EvGv(EmuState* state, DecodedOp* op) {
    // 19: SBB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSbb(state, dest, src);
        WriteModRM16(state, op, res);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        uint32_t res = AluSbb(state, dest, src);
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpSbb_GbEb(EmuState* state, DecodedOp* op) {
    // 1A: SBB r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSbb(state, dest, src);

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpSbb_GvEv(EmuState* state, DecodedOp* op) {
    // 1B: SBB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSbb(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluSbb(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpSbb_AlImm(EmuState* state, DecodedOp* op) {
    // 1C: SBB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSbb(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpSbb_EaxImm(EmuState* state, DecodedOp* op) {
    // 1D: SBB EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluSbb(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluSbb(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpAnd_AlImm(EmuState* state, DecodedOp* op) {
    // 24: AND AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAnd(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpAnd_EaxImm(EmuState* state, DecodedOp* op) {
    // 25: AND EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluAnd(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluAnd(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpSub_EbGb(EmuState* state, DecodedOp* op) {
    // 28: SUB r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSub(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpSub_GbEb(EmuState* state, DecodedOp* op) {
    // 2A: SUB r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSub(state, dest, src);
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpSub_GvEv(EmuState* state, DecodedOp* op) {
    // 2B: SUB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluSub(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpSub_AlImm(EmuState* state, DecodedOp* op) {
    // 2C: SUB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSub(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpSub_EaxImm(EmuState* state, DecodedOp* op) {
    // 2D: SUB EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluSub(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluSub(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpXor_EbGb(EmuState* state, DecodedOp* op) {
    // 30: XOR r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluXor(state, dest, src);
    WriteModRM8(state, op, res);
}

static FORCE_INLINE void OpXor_GbEb(EmuState* state, DecodedOp* op) {
    // 32: XOR r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluXor(state, dest, src);
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4)
        *rptr = (*rptr & 0xFFFFFF00) | res;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

static FORCE_INLINE void OpXor_GvEv(EmuState* state, DecodedOp* op) {
    // 33: XOR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t src = ReadModRM16(state, op);
        uint16_t dest = GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor(state, dest, src);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t src = ReadModRM32(state, op);
        uint32_t dest = GetReg(state, reg);
        uint32_t res = AluXor(state, dest, src);
        SetReg(state, reg, res);
    }
}

static FORCE_INLINE void OpXor_AlImm(EmuState* state, DecodedOp* op) {
    // 34: XOR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluXor(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

static FORCE_INLINE void OpXor_EaxImm(EmuState* state, DecodedOp* op) {
    // 35: XOR EAX, imm32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)op->imm;
        uint16_t res = AluXor(state, dest, src);
        uint32_t val = GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        SetReg(state, EAX, val);
    } else {
        uint32_t dest = GetReg(state, EAX);
        uint32_t src = op->imm;
        uint32_t res = AluXor(state, dest, src);
        SetReg(state, EAX, res);
    }
}

static FORCE_INLINE void OpCmp_AlImm(EmuState* state, DecodedOp* op) {
    // 3C: CMP AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluSub(state, dest, src);
}

static FORCE_INLINE void OpCmp_EaxImm(EmuState* state, DecodedOp* op) {
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
}

static FORCE_INLINE void OpTest_EbGb(EmuState* state, DecodedOp* op) {
    // 84: TEST r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluAnd(state, dest, src);
}

static FORCE_INLINE void OpTest_AlImm(EmuState* state, DecodedOp* op) {
    // A8: TEST AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluAnd(state, dest, src);
}

static FORCE_INLINE void OpTest_EaxImm(EmuState* state, DecodedOp* op) {
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
}

void RegisterAluOps() {
    g_Handlers[0x00] = DispatchWrapper<OpAdd_EbGb>;
    g_Handlers[0x01] = DispatchWrapper<OpAdd_EvGv>;
    g_Handlers[0x02] = DispatchWrapper<OpAdd_GbEb>;
    g_Handlers[0x03] = DispatchWrapper<OpAdd_GvEv>;
    g_Handlers[0x04] = DispatchWrapper<OpAdd_AlImm>;
    g_Handlers[0x05] = DispatchWrapper<OpAdd_EaxImm>;
    g_Handlers[0x08] = DispatchWrapper<OpOr_EbGb>;
    g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv>;
    g_Handlers[0x0A] = DispatchWrapper<OpOr_GbEb>;
    g_Handlers[0x0B] = DispatchWrapper<OpOr_GvEv>;
    g_Handlers[0x0C] = DispatchWrapper<OpOr_AlImm>;
    g_Handlers[0x0D] = DispatchWrapper<OpOr_EaxImm>;
    g_Handlers[0x10] = DispatchWrapper<OpAdc_EbGb>;
    g_Handlers[0x11] = DispatchWrapper<OpAdc_EvGv>;
    g_Handlers[0x12] = DispatchWrapper<OpAdc_GbEb>;
    g_Handlers[0x13] = DispatchWrapper<OpAdc_GvEv>;
    g_Handlers[0x14] = DispatchWrapper<OpAdc_AlImm>;
    g_Handlers[0x15] = DispatchWrapper<OpAdc_EaxImm>;
    g_Handlers[0x18] = DispatchWrapper<OpSbb_EbGb>;
    g_Handlers[0x19] = DispatchWrapper<OpSbb_EvGv>;
    g_Handlers[0x1A] = DispatchWrapper<OpSbb_GbEb>;
    g_Handlers[0x1B] = DispatchWrapper<OpSbb_GvEv>;
    g_Handlers[0x1C] = DispatchWrapper<OpSbb_AlImm>;
    g_Handlers[0x1D] = DispatchWrapper<OpSbb_EaxImm>;
    g_Handlers[0x20] = DispatchWrapper<OpAnd_EbGb>;
    g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv>;
    g_Handlers[0x22] = DispatchWrapper<OpAnd_GbEb>;
    g_Handlers[0x23] = DispatchWrapper<OpAnd_GvEv>;
    g_Handlers[0x24] = DispatchWrapper<OpAnd_AlImm>;
    g_Handlers[0x25] = DispatchWrapper<OpAnd_EaxImm>;
    g_Handlers[0x28] = DispatchWrapper<OpSub_EbGb>;
    g_Handlers[0x29] = DispatchWrapper<OpSub_EvGv>;
    g_Handlers[0x2A] = DispatchWrapper<OpSub_GbEb>;
    g_Handlers[0x2B] = DispatchWrapper<OpSub_GvEv>;
    g_Handlers[0x2C] = DispatchWrapper<OpSub_AlImm>;
    g_Handlers[0x2D] = DispatchWrapper<OpSub_EaxImm>;
    g_Handlers[0x30] = DispatchWrapper<OpXor_EbGb>;
    g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv>;
    g_Handlers[0x32] = DispatchWrapper<OpXor_GbEb>;
    g_Handlers[0x33] = DispatchWrapper<OpXor_GvEv>;
    g_Handlers[0x34] = DispatchWrapper<OpXor_AlImm>;
    g_Handlers[0x35] = DispatchWrapper<OpXor_EaxImm>;
    g_Handlers[0x3C] = DispatchWrapper<OpCmp_AlImm>;
    g_Handlers[0x3D] = DispatchWrapper<OpCmp_EaxImm>;
    g_Handlers[0x84] = DispatchWrapper<OpTest_EbGb>;
    g_Handlers[0xA8] = DispatchWrapper<OpTest_AlImm>;
    g_Handlers[0xA9] = DispatchWrapper<OpTest_EaxImm>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x40 + i] = DispatchWrapper<OpInc_Reg>;
        g_Handlers[0x48 + i] = DispatchWrapper<OpDec_Reg>;
    }
}
}  // namespace x86emu
