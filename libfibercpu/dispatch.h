#pragma once
#include <limits>
#include "decoder.h"
#include "exec_utils.h"
#include "logger.h"
#include "specialization.h"
#include "state.h"

namespace fiberish {

// External reference to handlers
extern void* g_HandlerBase;
extern HandlerFunc g_Handlers[1024];

#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
FORCE_INLINE void RecordBlockHandlersUntil(EmuState* state, const DecodedOp* stop_op) {
    if (!state->current_block_head) return;
    for (const DecodedOp* current = state->current_block_head; current != stop_op; current = NextOp(current)) {
        state->handler_exec_counts[reinterpret_cast<uintptr_t>(current->handler)]++;
    }
}

FORCE_INLINE void RecordBlockHandlersThrough(EmuState* state, const DecodedOp* stop_op) {
    if (!state->current_block_head) return;
    for (const DecodedOp* current = state->current_block_head;; current = NextOp(current)) {
        state->handler_exec_counts[reinterpret_cast<uintptr_t>(current->handler)]++;
        if (current == stop_op) break;
    }
}
#else
FORCE_INLINE void RecordBlockHandlersUntil(EmuState*, const DecodedOp*) {}
FORCE_INLINE void RecordBlockHandlersThrough(EmuState*, const DecodedOp*) {}
#endif

extern ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                  mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);
extern ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);
extern ATTR_PRESERVE_NONE int64_t ResolveSentinelTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                        int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                        uint64_t flags_cache);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                      int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                      uint64_t flags_cache);

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    // Prefetch further ops
    PREFETCH(reinterpret_cast<const std::byte*>(op) + 128);
    PREFETCH(reinterpret_cast<const std::byte*>(op) + 256);

    // Execute Logic
    auto flow = Target(state, op, &utlb, GetImm(op), &branch, flags_cache);

    switch (flow) {
        case LogicFlow::Continue:
            // Direct Relative Dispatch
            // Note: We don't check for 0 here for speed, assuming well-formed blocks
            // (sentinel always valid)
            if (auto* next_op = NextOp(op)) {
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
            }
            __builtin_unreachable();
        case LogicFlow::ContinueSkipOne:
            if (auto* next_op = NextOp(NextOp(op))) {
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
            }
            __builtin_unreachable();
        case LogicFlow::ExitOnCurrentEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_start(op);
            return instr_limit;
        case LogicFlow::ExitOnNextEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_end(op);
            return instr_limit;
        case LogicFlow::ExitWithoutSyncEIP:
            RecordBlockHandlersThrough(state, op);
            CommitFlagsCache(state, flags_cache);
            return instr_limit;
        case LogicFlow::RestartMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::RetryMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::ExitToBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTarget(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::ExitToNextOpBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTarget(state, NextOp(op), instr_limit, utlb, branch, flags_cache);
        default:
            CommitFlagsCache(state, flags_cache);
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

template <LogicFunc Target>
struct FusedDispatchRegistrar {
    static void RegisterSpecialized(int opcode, FusedSpecCriteria criteria) {
        RegisterFusedSpecializedHandler(opcode, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }
};

// Simplified Macro for ops registration
// Replaces the old g_Handlers[idx] = DispatchWrapper<Func>;
// Usage: DispatchRegistrar<Func>::Register(idx);

}  // namespace fiberish
