// Shared Helper Functions
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4) return val & 0xFF;
    else return (val >> 8) & 0xFF;
}

uint32_t ReadModRM(EmuState* state, DecodedOp* op, bool is_byte) {
    if (is_byte) {
         // Read 8-bit
         uint8_t mod = (op->modrm >> 6) & 3;
         uint8_t rm = op->modrm & 7;
         if (mod == 3) {
             uint32_t val = GetReg(state, rm & 3);
             if (rm < 4) return val & 0xFF;
             else return (val >> 8) & 0xFF;
         } else {
             uint32_t addr = ComputeEAD(state, op);
             return state->mmu.read<uint8_t>(addr);
         }
    } else if (op->prefixes.flags.opsize) {
        return ReadModRM16(state, op);
    } else {
        return ReadModRM32(state, op);
    }
}

} // namespace x86emu
