#pragma once
#include "decoder.h"
#include "state.h"

namespace x86emu {

// External reference to handlers
extern HandlerFunc g_Handlers[1024];

template<LogicFunc Target>
ATTR_PRESERVE_NONE
void DispatchWrapper(EmuState* state, DecodedOp* op) {
    uint32_t original_eip = state->ctx.eip;
    // Advance EIP before execution
    state->ctx.eip += op->length;
    
    // Execute Logic
    Target(state, op);
    
    // Stop Chain if Fault/Stopped/Yield or if this is the last op in a sequence (e.g. X86_Step)
    if (state->status != EmuStatus::Running || op->meta.flags.is_last) {
        // Restore EIP if Fault (Precise Exception)
        if (state->status == EmuStatus::Fault) {
            state->ctx.eip = original_eip;
        }
        return;
    }
    
    // Tail call next instruction in the block
    DecodedOp* next = op + 1;
    ATTR_MUSTTAIL return g_Handlers[next->handler_index](state, next);
}

} // namespace x86emu
