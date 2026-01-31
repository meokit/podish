// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

void OpAdd_EbGb(EmuState* state, DecodedOp* op) {
    // 00: ADD r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdd(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAdd_EvGv(EmuState* state, DecodedOp* op) {
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

void OpAdd_GbEb(EmuState* state, DecodedOp* op) {
    // 02: ADD r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdd(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAdd_GvEv(EmuState* state, DecodedOp* op) {
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

void OpAdc_EbGb(EmuState* state, DecodedOp* op) {
    // 10: ADC r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdc(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAdc_EvGv(EmuState* state, DecodedOp* op) {
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

void OpAdc_GbEb(EmuState* state, DecodedOp* op) {
    // 12: ADC r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdc(state, dest, src);
    
    // Write back to reg8
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAdc_GvEv(EmuState* state, DecodedOp* op) {
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

void OpSub_EvGv(EmuState* state, DecodedOp* op) {
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

void OpAnd_EbGb(EmuState* state, DecodedOp* op) {
    // 20: AND r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAnd_EvGv(EmuState* state, DecodedOp* op) {
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

void OpAnd_GbEb(EmuState* state, DecodedOp* op) {
    // 22: AND r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAnd(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAnd_GvEv(EmuState* state, DecodedOp* op) {
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

void OpOr_EvGv(EmuState* state, DecodedOp* op) {
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

void OpXor_EvGv(EmuState* state, DecodedOp* op) {
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

void OpInc_Reg(EmuState* state, DecodedOp* op) {
    // 40+rd: INC r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    
    // INC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    uint32_t res = AluAdd(state, val, 1U);
    state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
    
    SetReg(state, reg, res);
}

void OpDec_Reg(EmuState* state, DecodedOp* op) {
    // 48+rd: DEC r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    
    // DEC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    uint32_t res = AluSub(state, val, 1U);
    state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
    
    SetReg(state, reg, res);
}

void OpAdd_AlImm(EmuState* state, DecodedOp* op) {
    // 04: ADD AL, imm8
    uint8_t dest = GetReg8(state, EAX); // AL is Reg 0 low byte
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdd(state, dest, src);
    
    // Write back to AL
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpAdd_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpOr_EbGb(EmuState* state, DecodedOp* op) {
    // 08: OR r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluOr(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpOr_GbEb(EmuState* state, DecodedOp* op) {
    // 0A: OR r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluOr(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpOr_GvEv(EmuState* state, DecodedOp* op) {
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

void OpOr_AlImm(EmuState* state, DecodedOp* op) {
    // 0C: OR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluOr(state, dest, src);
    
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpOr_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpAdc_AlImm(EmuState* state, DecodedOp* op) {
    // 14: ADC AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAdc(state, dest, src);
    
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpAdc_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpSbb_EbGb(EmuState* state, DecodedOp* op) {
    // 18: SBB r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSbb(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpSbb_EvGv(EmuState* state, DecodedOp* op) {
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

void OpSbb_GbEb(EmuState* state, DecodedOp* op) {
    // 1A: SBB r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSbb(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpSbb_GvEv(EmuState* state, DecodedOp* op) {
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

void OpSbb_AlImm(EmuState* state, DecodedOp* op) {
    // 1C: SBB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSbb(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpSbb_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpAnd_AlImm(EmuState* state, DecodedOp* op) {
    // 24: AND AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluAnd(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpAnd_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpSub_EbGb(EmuState* state, DecodedOp* op) {
    // 28: SUB r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSub(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpSub_GbEb(EmuState* state, DecodedOp* op) {
    // 2A: SUB r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluSub(state, dest, src);
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpSub_GvEv(EmuState* state, DecodedOp* op) {
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

void OpSub_AlImm(EmuState* state, DecodedOp* op) {
    // 2C: SUB AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluSub(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpSub_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpXor_EbGb(EmuState* state, DecodedOp* op) {
    // 30: XOR r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluXor(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpXor_GbEb(EmuState* state, DecodedOp* op) {
    // 32: XOR r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluXor(state, dest, src);
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpXor_GvEv(EmuState* state, DecodedOp* op) {
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

void OpXor_AlImm(EmuState* state, DecodedOp* op) {
    // 34: XOR AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    uint8_t res = AluXor(state, dest, src);
    uint32_t val = GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    SetReg(state, EAX, val);
}

void OpXor_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpCmp_AlImm(EmuState* state, DecodedOp* op) {
    // 3C: CMP AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluSub(state, dest, src);
}

void OpCmp_EaxImm(EmuState* state, DecodedOp* op) {
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

void OpTest_EbGb(EmuState* state, DecodedOp* op) {
    // 84: TEST r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluAnd(state, dest, src);
}

void OpTest_AlImm(EmuState* state, DecodedOp* op) {
    // A8: TEST AL, imm8
    uint8_t dest = GetReg8(state, EAX);
    uint8_t src = (uint8_t)op->imm;
    AluAnd(state, dest, src);
}

void OpTest_EaxImm(EmuState* state, DecodedOp* op) {
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

} // namespace x86emu
