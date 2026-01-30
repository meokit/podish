#pragma once
#include "decoder.h"
#include "state.h"

namespace x86emu {

template<LogicFunc Target>
ATTR_PRESERVE_NONE
void DispatchWrapper(EmuState* state, DecodedOp* op) {
    // Advance EIP before execution
    // (If Handler is Control Flow, it will overwrite EIP)
    state->ctx.eip += op->length;
    
    // Tail call optimization
    // ATTR_MUSTTAIL return Target(state, op);
    return Target(state, op);
}

} // namespace x86emu
