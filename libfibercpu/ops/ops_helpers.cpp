// Shared Helper Functions
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4)
        return val & 0xFF;
    else
        return (val >> 8) & 0xFF;
}

}  // namespace x86emu
