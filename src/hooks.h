#pragma once

#include <functional>
#include <map>
#include <string>
#include "common.h"

namespace x86emu {

// Handler type for hooks
// Returns true if execution should continue, false if it should abort/trap
using HookHandler = std::function<bool(Context* ctx)>;

class HookManager {
public:
    // Register a hook for a specific interrupt vector (e.g. 0x80 for Linux Syscalls)
    void register_interrupt(uint8_t vector, HookHandler handler) {
        interrupt_hooks[vector] = handler;
    }

    // Register a generic instruction hook (e.g. for UD2 or specific EIP)
    void register_eip_hook(uint32_t eip, HookHandler handler) {
        eip_hooks[eip] = handler;
    }

    bool handle_interrupt(Context* ctx, uint8_t vector) {
        if (interrupt_hooks.contains(vector)) {
            return interrupt_hooks[vector](ctx);
        }
        // Default: Log unhandled interrupt
        // In a real mock, we might just ignore or print warning
        return true; 
    }

    bool check_eip_hook(Context* ctx) {
        if (eip_hooks.contains(ctx->eip)) {
            return eip_hooks[ctx->eip](ctx);
        }
        return true;
    }

private:
    std::map<uint8_t, HookHandler> interrupt_hooks;
    std::map<uint32_t, HookHandler> eip_hooks;
};

// Global hook manager instance (or part of the Emulator class later)
// For now, standalone.

} // namespace x86emu
