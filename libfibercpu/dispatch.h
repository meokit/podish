#pragma once
#include "decoder.h"
#include "state.h"

#include <cstdio>

namespace x86emu {

// External reference to handlers
extern HandlerFunc g_Handlers[1024];

template<LogicFunc Target>
ATTR_PRESERVE_NONE
int64_t DispatchWrapper(EmuState* state, DecodedOp* op, int64_t instr_limit) {
    uint32_t original_eip = state->ctx.eip;
    // Advance EIP before execution
    state->ctx.eip += op->length;
    
    // Execute Logic
    Target(state, op);
    
    // Stop Chain if Fault/Stopped/Yield
    if (state->status != EmuStatus::Running) {
        // Restore EIP if Fault (Precise Exception)
        if (state->status == EmuStatus::Fault) {
            state->ctx.eip = original_eip;
        }
        return instr_limit;
    }
    
    // Tail call next instruction in the block
    DecodedOp* next = op + 1;
    HandlerFunc h = g_Handlers[next->handler_index];
    ATTR_MUSTTAIL return h(state, next, instr_limit);
}

} // namespace x86emu
