#pragma once
#include "decoder.h"
#include "state.h"

#include <cstdio>

namespace x86emu {

// External reference to handlers
extern void* g_HandlerBase;
extern HandlerFunc g_Handlers[1024];
extern HandlerFunc g_Handlers_NF[1024];

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* state, DecodedOp* op, int64_t instr_limit) {
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

    // Direct Relative Dispatch
    int32_t offset = next->handler_offset;
    // Note: We don't check for 0 here for speed, assuming well-formed blocks
    // (sentinel always valid)
    HandlerFunc h = (HandlerFunc)((intptr_t)g_HandlerBase + offset);
    ATTR_MUSTTAIL return h(state, next, instr_limit);
}

// Registration Helper
// We use a struct to infer the function pointer type for registration
template <LogicFunc Target>
struct DispatchRegistrar {
    static void Register(int idx) { g_Handlers[idx] = DispatchWrapper<Target>; }

    static void RegisterNF(int idx) { g_Handlers_NF[idx] = DispatchWrapper<Target>; }
};

// Simplified Macro for ops registration
// Replaces the old g_Handlers[idx] = DispatchWrapper<Func>;
// Usage: DispatchRegistrar<Func>::Register(idx);

}  // namespace x86emu
