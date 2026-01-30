// Control Flow
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

void OpJmp_Rel(EmuState* state, DecodedOp* op) {
    // E9: JMP rel32
    state->ctx.eip += op->imm;
}

void OpJcc_Rel(EmuState* state, DecodedOp* op) {
    // 0F 8x: Jcc rel32
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    uint8_t cond = op->handler_index & 0xF;
    
    if (CheckCondition(state, cond)) {
        state->ctx.eip += op->imm;
    }
    // If not taken, EIP is already at next insn (fallthrough).
}

void OpCall_Rel(EmuState* state, DecodedOp* op) {
    // E8: CALL rel32
    // Push Return Address (Current EIP is already advanced to Next Insn by Wrapper/Step)
    Push32(state, state->ctx.eip);
    // Jump relative to Next Insn
    state->ctx.eip += op->imm;
}

void OpRet(EmuState* state, DecodedOp* op) {
    // C3: RET
    uint32_t ret_eip = Pop32(state);
    state->ctx.eip = ret_eip;
}

void OpInt(EmuState* state, DecodedOp* op) {
    // CD ib: INT imm8
    // Note: Decoder puts imm8 in op->imm
    uint8_t vector = (uint8_t)op->imm;
    // printf("[Sim] OpInt: Vector %02X\n", vector);
    if (!state->hooks.on_interrupt(state, vector)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector; // Fault with vector
        // NOTE: Real hardware might GPF if IDT descriptor is bad, 
        // but for us "Unhandled Interrupt" is a Fault.
    }
}

void OpInt3(EmuState* state, DecodedOp* op) {
    // CC: INT3 (Vector 3, Breakpoint)
    if (!state->hooks.on_interrupt(state, 3)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 3;
    }
}

void OpHlt(EmuState* state, DecodedOp* op) {
    // HLT (0xF4)
    state->status = EmuStatus::Stopped;
}

void OpNop(EmuState* state, DecodedOp* op) {
    // No Operation
}

void OpCmov_GvEv(EmuState* state, DecodedOp* op) {
    // 0F 4x: CMOVcc r32, r/m32
    uint8_t cond = op->handler_index & 0xF;
    bool pass = CheckCondition(state, cond);
    printf("CMOV cond=%d pass=%d eflags=%x\n", cond, pass, state->ctx.eflags);
    if (pass) {
        if (op->prefixes.flags.opsize) {
            uint16_t val = ReadModRM16(state, op);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
        } else {
            uint32_t val = ReadModRM32(state, op);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, val);
        }
    }
}

} // namespace x86emu
