#pragma once
#include "decoder.h"
#include "specialization.h"
#include "state.h"

#include <cstdio>

namespace fiberish {

// External reference to handlers
extern void* g_HandlerBase;
extern HandlerFunc g_Handlers[1024];

extern ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                  mem::MicroTLB utlb);
extern ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                mem::MicroTLB utlb);

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb) {
    // Prefetch next cache line
    PREFETCH((void*)(op + 2));
    // Execute Logic
    auto flow = Target(state, op, &utlb);

    switch (flow) {
        case LogicFlow::Continue:
            // Direct Relative Dispatch
            // Note: We don't check for 0 here for speed, assuming well-formed blocks
            // (sentinel always valid)
            ATTR_MUSTTAIL return (op + 1)->handler(state, op + 1, instr_limit, utlb);
        case LogicFlow::ExitOnCurrentEIP:
            state->sync_eip_to_op_start(op);
            return instr_limit;
        case LogicFlow::ExitOnNextEIP:
            state->sync_eip_to_op_end(op);
            return instr_limit;
        case LogicFlow::RestartMemoryOp:
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb);
        case LogicFlow::RetryMemoryOp:
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb);
        default:
            return instr_limit;
    }
}

// Registration Helper
// We use a struct to infer the function pointer type for registration
template <LogicFunc Target>
struct DispatchRegistrar {
    static void Register(int idx) { g_Handlers[idx] = DispatchWrapper<Target>; }

    static void RegisterNF(int idx) {
        SpecCriteria criteria;
        criteria.no_flags = true;
        // Don't set masks, so it matches any ModRM/Prefix unless overridden?
        // Wait, standard specialized handler matches narrowly.
        // But here we want to match broadly (any modrm) BUT with no_flags=true.
        // SpecCriteria defaults mask to 0 (wildcard). So this registers a generic NoFlags handler for this opcode.
        RegisterSpecializedHandler(idx, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }

    // Specialization Registration
    static void RegisterSpecialized(int opcode, SpecCriteria criteria) {
        // We register the Wrapper as the handler, because that is what the dispatch loop calls.
        // The wrapper internally calls Target().
        // LogicFunc (the raw logic) is NOT what is stored in the offset, the wrapper is.
        // Wait, SpecializedEntry stores LogicFunc? No, it should store HandlerFunc.
        // Let's cast DispatchWrapper<Target> to HandlerFunc (which it is compatible with).
        RegisterSpecializedHandler(opcode, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }
};

// Simplified Macro for ops registration
// Replaces the old g_Handlers[idx] = DispatchWrapper<Func>;
// Usage: DispatchRegistrar<Func>::Register(idx);

}  // namespace fiberish
