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
    // 01: ADD r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAdd(state, dest, src);
    WriteModRM32(state, op, res);
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
    // 03: ADD r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAdd(state, dest, src);
    SetReg(state, reg, res);
}

void OpAdc_EbGb(EmuState* state, DecodedOp* op) {
    // 10: ADC r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdc(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAdc_EvGv(EmuState* state, DecodedOp* op) {
    // 11: ADC r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
    uint32_t res = AluAdc(state, dest, src);
    WriteModRM32(state, op, res);
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
    // 13: ADC r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAdc(state, dest, src);
    SetReg(state, reg, res);
}

void OpSub_EvGv(EmuState* state, DecodedOp* op) {
    // 29: SUB r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluSub(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpAnd_EbGb(EmuState* state, DecodedOp* op) {
    // 20: AND r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAnd_EvGv(EmuState* state, DecodedOp* op) {
    // 21: AND r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAnd(state, dest, src);
    WriteModRM32(state, op, res);
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
    // 23: AND r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAnd(state, dest, src);
    SetReg(state, reg, res);
}

void OpOr_EvGv(EmuState* state, DecodedOp* op) {
    // 09: OR r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluOr(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpXor_EvGv(EmuState* state, DecodedOp* op) {
    // 31: XOR r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluXor(state, dest, src);
    WriteModRM32(state, op, res);
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

} // namespace x86emu
