// Comparison & Test
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

void OpCmp_EbGb(EmuState* state, DecodedOp* op) {
    // 38: CMP r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluSub(state, dest, src);
}

void OpCmp_EvGv(EmuState* state, DecodedOp* op) {
    // 39: CMP r/m32, r32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        AluSub(state, dest, src);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        AluSub(state, dest, src);
    }
}

void OpCmp_GbEb(EmuState* state, DecodedOp* op) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t src = ReadModRM8(state, op);
    AluSub(state, dest, src);
}

void OpCmp_GvEv(EmuState* state, DecodedOp* op) {
    // 3B: CMP r32, r/m32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        uint16_t src = ReadModRM16(state, op);
        AluSub(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t src = ReadModRM32(state, op);
        AluSub(state, dest, src);
    }
}

void OpTest_EvGv(EmuState* state, DecodedOp* op) {
    // 85: TEST r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    AluAnd(state, dest, src); // Discard result
}

} // namespace x86emu
