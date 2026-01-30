#pragma once

#include <functional>
#include <array>
#include <string>
#include "common.h"

namespace x86emu {

struct EmuState; // Forward decl

// Handler types
// Return true to continue, false to Fault
using InterruptHook = std::function<bool(EmuState* state, uint8_t vector)>;

class HookManager {
public:
    void set_interrupt_hook(uint8_t vector, InterruptHook handler) {
        handlers_[vector] = handler;
    }

    // Trigger an interrupt. Returns true if handled, false if emulator should fault.
    bool on_interrupt(EmuState* state, uint8_t vector) {
        if (handlers_[vector]) {
            return handlers_[vector](state, vector);
        }
        return false; // No hook -> Unhandled Interrupt -> Fault
    }

    // Standard #UD (Undefined Opcode) - Vector 6
    bool on_invalid_opcode(EmuState* state) {
        return on_interrupt(state, 6);
    }

    // Decode Fault -> #UD - Vector 6
    bool on_decode_fault(EmuState* state) {
        return on_interrupt(state, 6);
    }

private:
    std::array<InterruptHook, 256> handlers_;
};

} // namespace x86emu
