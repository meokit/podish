#pragma once
#include <cstddef>
#include <limits>
#include "decoder.h"
#include "exec_utils.h"
#include "logger.h"
#include "operand_policy.h"
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
                                                  mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                                  uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS);
extern ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                                uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowLink(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                              mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                              uint64_t flags_cache FIBERCPU_PIN_PARAMS);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowControlFlow(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit,
                                                                     mem::MicroTlbAbiWord utlb_tags,
                                                                     mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                                     uint64_t flags_cache FIBERCPU_PIN_PARAMS);
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                      int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                      mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                      uint64_t flags_cache FIBERCPU_PIN_PARAMS);

FORCE_INLINE void CountLLVMBlockEntry(EmuState* state, BasicBlock* block, BasicBlockChainPrefix chain) {
#if FIBERCPU_BLOCK_COMPILATION
#if FIBERCPU_LLVM_JIT && !defined(FIBERCPU_ENABLE_BLOCK_PROFILE) && !defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    if (chain.weighted_profile) ++block->exec_count;
#endif
    const auto count = chain.llvm_hot_count;
    if (count > 1) {
        block->chain.llvm_hot_count = count - 1;
    } else if (count == 1 && !state->llvm_hot_block) {
        block->chain.llvm_hot_count = 0;
        state->llvm_hot_block = block;
    }
#else
    (void)state;
    (void)block;
    (void)chain;
#endif
}

template <ExtKind Kind>
FORCE_INLINE ATTR_PRESERVE_NONE int64_t ResolveBranchTargetInline(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                                  mem::MicroTlbAbiWord utlb_addend, uint32_t target_eip,
                                                                  uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    (void)op;
    (void)utlb_tags;
    (void)utlb_addend;
    state->clear_pending_mem_op();
    if (instr_limit <= 0) {
        FIBERCPU_PIN_COMMIT(state);
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
        state->last_block = next_block;
        instr_limit -= header_ptr->inst_count();
        DecodedOp* next_head = next_block->FirstOp();
        CountLLVMBlockEntry(state, next_block, header_ptr->chain);
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
        next_block->exec_count++;
#endif
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
        state->current_block_head = next_head;
#endif
        __builtin_assume(entry != nullptr);
        ATTR_MUSTTAIL return entry(state, next_head, instr_limit, utlb_tags, utlb_addend,
                                   std::numeric_limits<uint32_t>::max(), flags_cache FIBERCPU_PIN_RELOAD);
    }

    if constexpr (Kind == ExtKind::Link) {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowLink(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                                                         flags_cache FIBERCPU_PIN_RELOAD);
    } else {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowControlFlow(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                target_eip, flags_cache FIBERCPU_PIN_RELOAD);
    }
}

template <bool UseBranch>
FORCE_INLINE ATTR_PRESERVE_NONE int64_t OpExitBlockImpl(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                        int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                        mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                        uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    RecordBlockHandlersUntil(state, op);
    // Group 5's indirect target can use the full guest address range.
    const uint32_t target_eip = UseBranch ? branch : op->next_eip;
    ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::Link>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                  target_eip, flags_cache FIBERCPU_PIN_RELOAD);
}

template <LogicFunc Target, class OperandPolicy = OperandSpec::Dynamic>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                           uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    const auto history_length = state->sequence_expected_op == op ? state->sequence_history_length : 0;
    const auto history = state->sequence_history;
    state->sequence_expected_op = nullptr;
    state->sequence_history_length = 0;
#endif
    mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    OperandPolicy::Assume(op);
    // Execute Logic
    auto flow = Target(state, op, &utlb, OperandPolicy::Immediate(op), &branch, flags_cache, FIBERCPU_GPR_CACHE);
    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);

    switch (flow) {
        case LogicFlow::Continue:
            // Direct Relative Dispatch
            // Note: We don't check for 0 here for speed, assuming well-formed blocks
            // (sentinel always valid)
            if (auto* next_op = NextOp(op)) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
                if (next_op->len != 0) {
                    state->sequence_counts[{reinterpret_cast<uintptr_t>(op->handler),
                                            reinterpret_cast<uintptr_t>(next_op->handler)}]++;
                    const auto current = reinterpret_cast<uintptr_t>(op->handler);
                    const auto next = reinterpret_cast<uintptr_t>(next_op->handler);
                    if (history_length >= 1) state->ngram_counts[{{history[1], current, next, 0}}]++;
                    if (history_length >= 2) state->ngram_counts[{{history[0], history[1], current, next}}]++;
                    state->sequence_history = {history[1], current};
                    state->sequence_history_length = static_cast<uint8_t>(std::min<int>(2, history_length + 1));
                    state->sequence_expected_op = next_op;
                }
#endif
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,
                                                      flags_cache FIBERCPU_PIN_FORWARD(OperandPolicy));
            }
            __builtin_unreachable();
        case LogicFlow::ContinueSkipOne:
            if (auto* next_op = NextOp(NextOp(op))) {
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,
                                                      flags_cache FIBERCPU_PIN_FORWARD(OperandPolicy));
            }
            __builtin_unreachable();
        case LogicFlow::ExitOnCurrentEIP:
            RecordBlockHandlersThrough(state, op);
            FIBERCPU_PIN_COMMIT(state);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_start(op);
            return instr_limit;
        case LogicFlow::ExitOnNextEIP:
            RecordBlockHandlersThrough(state, op);
            FIBERCPU_PIN_COMMIT(state);
            CommitFlagsCache(state, flags_cache);
            if (!state->eip_dirty) state->sync_eip_to_op_end(op);
            return instr_limit;
        case LogicFlow::RestartMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                 flags_cache FIBERCPU_PIN_RELOAD);
        case LogicFlow::RetryMemoryOp:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                               flags_cache FIBERCPU_PIN_RELOAD);
        case LogicFlow::ExitToBranch:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(
                state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache FIBERCPU_PIN_RELOAD);
        default:
            FIBERCPU_PIN_COMMIT(state);
            CommitFlagsCache(state, flags_cache);
            return instr_limit;
    }
}

}  // namespace fiberish
