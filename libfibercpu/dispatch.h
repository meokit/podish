#pragma once
#include <cstddef>
#include <limits>
#include "decoder.h"
#include "exec_utils.h"
#include "jit/stencil.h"
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
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowLink(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                              uint64_t flags_cache);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowControlFlow(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                      int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                      uint64_t flags_cache);

#ifdef FIBERCPU_ENABLE_JCC_PROFILE
FORCE_INLINE void RecordConditionalBranchCacheResult(EmuState* state, const DecodedOp* op, bool cache_hit) {
    if (!op->meta.flags.is_conditional_branch) return;
    auto& counters = state->jcc_profile_counts[reinterpret_cast<uintptr_t>(op->handler)];
    if (cache_hit) {
        counters.cache_hit++;
    } else {
        counters.cache_miss++;
    }
}
#else
FORCE_INLINE void RecordConditionalBranchCacheResult(EmuState*, const DecodedOp*, bool) {}
#endif

template <ExtKind Kind>
FORCE_INLINE ATTR_PRESERVE_NONE int64_t ResolveBranchTargetInline(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTLB utlb,
                                                                  uint32_t target_eip, uint64_t flags_cache) {
    state->mem_op.emplace<0>();
    if (instr_limit <= 0) {
        CommitFlagsCache(state, flags_cache);
        state->ctx.eip = target_eip;
        return instr_limit;
    }

    BasicBlock* next_block;
    if constexpr (Kind == ExtKind::Link) {
        next_block = GetNextBlock(op);
    } else {
        static_assert(Kind == ExtKind::ControlFlow);
        next_block = GetCachedTarget(op);
    }

    auto header = *reinterpret_cast<const volatile __uint128_t*>(next_block);
    auto header_ptr = reinterpret_cast<const BasicBlock*>(&header);
    auto entry = header_ptr->entry;
    if (header_ptr->MatchesChainTarget(target_eip)) [[likely]] {
        RecordConditionalBranchCacheResult(state, op, true);
        state->last_block = next_block;
        instr_limit -= header_ptr->inst_count();
        DecodedOp* next_head = next_block->FirstOp();
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
        next_block->exec_count++;
        state->current_block_head = next_head;
#endif
        __builtin_assume(entry != nullptr);
        ATTR_MUSTTAIL return entry(state, next_head, instr_limit, utlb, std::numeric_limits<uint32_t>::max(),
                                   flags_cache);
    }

    if constexpr (Kind == ExtKind::Link) {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowLink(state, op, instr_limit, utlb, target_eip, flags_cache);
    } else {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowControlFlow(state, op, instr_limit, utlb, target_eip, flags_cache);
    }
}

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
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
        case LogicFlow::RestartMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::RetryMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb, branch, flags_cache);
        case LogicFlow::ExitToBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(state, op, instr_limit, utlb, branch,
                                                                                 flags_cache);
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
        RegisterSpecializedHandler(idx, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }

    // Specialization Registration
    static void RegisterSpecialized(int opcode, SpecCriteria criteria) {
        RegisterSpecializedHandler(opcode, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }
};

// Simplified Macro for ops registration
// Replaces the old g_Handlers[idx] = DispatchWrapper<Func>;
// Usage: DispatchRegistrar<Func>::Register(idx);

}  // namespace fiberish
