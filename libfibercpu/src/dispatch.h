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

#if FIBERCPU_LLVM_JIT && !defined(FIBERCPU_LLVM_BLOCK_TEMPLATE)
bool CaptureLLVMWeightedProfilePage(EmuState& state, BasicBlock& block);
#endif
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
extern "C" void FiberLLVMRegionEdge(EmuState* state, DecodedOp* op, int64_t budget, mem::MicroTlbAbiWord tags,
                                    mem::MicroTlbAbiWord addend, uint32_t eip,
                                    uint64_t flags FIBERCPU_PIN_PARAMS) noexcept;
extern "C" void FiberLLVMRegionExit(EmuState* state) noexcept;
#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
extern "C" [[gnu::const]] bool FiberLLVMShareRegionExits();
#define FIBERCPU_REGION_EXIT_DECL(Name)                                                                              \
    extern "C" ATTR_PRESERVE_NONE int64_t Name(EmuState* state, DecodedOp* op, int64_t instr_limit,                  \
                                               mem::MicroTlbAbiWord tags, mem::MicroTlbAbiWord addend, uint32_t eip, \
                                               uint64_t flags_cache FIBERCPU_PIN_PARAMS)
FIBERCPU_REGION_EXIT_DECL(FiberLLVMRegionChainSingle);
FIBERCPU_REGION_EXIT_DECL(FiberLLVMRegionChainDouble);
FIBERCPU_REGION_EXIT_DECL(FiberLLVMRegionReturn);
#undef FIBERCPU_REGION_EXIT_DECL
#endif
#endif

FORCE_INLINE void RestoreLLVMBlockOnExit(EmuState* state) {
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    FiberLLVMRegionExit(state);
#else
    (void)state;
#endif
}

// External reference to handlers
extern void* g_HandlerBase;
extern HandlerFunc g_Handlers[1024];

#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
FORCE_INLINE void RecordBlockHandlersUntil(EmuState* state, const DecodedOp* stop_op) {
    if (!state->current_block_head) return;
    for (const DecodedOp* current = state->current_block_head; current != stop_op;
         current = state->last_block->NextOp(current)) {
        state->handler_exec_counts[reinterpret_cast<uintptr_t>(current->OriginalHandler())]++;
    }
}

FORCE_INLINE void RecordBlockHandlersThrough(EmuState* state, const DecodedOp* stop_op) {
    if (!state->current_block_head) return;
    for (const DecodedOp* current = state->current_block_head;; current = state->last_block->NextOp(current)) {
        state->handler_exec_counts[reinterpret_cast<uintptr_t>(current->OriginalHandler())]++;
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
extern ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowDoubleSlot(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                    int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                                    mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS);

FORCE_INLINE void CountLLVMBlockEntry(EmuState* state, BasicBlock* block, BasicBlockChainPrefix chain) {
#if FIBERCPU_BLOCK_COMPILATION
#if FIBERCPU_LLVM_JIT && !defined(FIBERCPU_ENABLE_BLOCK_PROFILE) && !defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    if (chain.weighted_profile || chain.jit_execution_profile) ++block->ExecCountStorage();
#endif
    const auto count = chain.llvm_hot_count;
    if (count > 1) {
        block->chain.llvm_hot_count = count - 1;
    } else if (count == 1) {
#if FIBERCPU_LLVM_JIT && !defined(FIBERCPU_LLVM_BLOCK_TEMPLATE)
        if (chain.weighted_profile && CaptureLLVMWeightedProfilePage(*state, *block)) {
            block->chain.llvm_hot_count = 0;
            return;
        }
#endif
        if (!state->llvm_hot_block) {
            block->chain.llvm_hot_count = 0;
            state->llvm_hot_block = block;
        }
    }
#else
    (void)state;
    (void)block;
    (void)chain;
#endif
}

FORCE_INLINE ATTR_PRESERVE_NONE int64_t ReturnFromBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                        int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                        mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                        uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
#if defined(FIBERCPU_LLVM_BLOCK_TEMPLATE) && defined(FIBERCPU_LLVM_SHARED_REGION_EXITS)
    if (FiberLLVMShareRegionExits()) {
        ATTR_MUSTTAIL return FiberLLVMRegionReturn(state, nullptr, instr_limit, 0, 0, 0, flags_cache FIBERCPU_PIN_ARGS);
    }
#endif
    (void)op;
    (void)utlb_tags;
    (void)utlb_addend;
    (void)branch;
    FIBERCPU_PIN_COMMIT(state);
    CommitFlagsCache(state, flags_cache);
    return instr_limit;
}

struct BlockChainTarget {
    DecodedOp* op;
    HandlerFunc entry;
};

template <unsigned LinkSlot>
FORCE_INLINE BlockChainTarget TryChainBlock(EmuState* state, DecodedOp* op, uint32_t target_eip, int64_t& instr_limit) {
    static_assert(LinkSlot <= 1);
    BasicBlock* next_block = (op + LinkSlot)->LoadBlockLink();
    auto header = *reinterpret_cast<const volatile __uint128_t*>(next_block);
    auto header_ptr = reinterpret_cast<const BasicBlock*>(&header);
    if (!header_ptr->MatchesChainTarget(target_eip)) [[unlikely]]
        return {};
#ifdef ENABLE_TLB_STATS
    auto& dispatch_stats = state->mmu.dispatch_stats;
    ++dispatch_stats.direct_links;
    dispatch_stats.cross_page_links += ((state->last_block->start_eip() ^ target_eip) & ~mem::PAGE_MASK) != 0;
#endif
    state->last_block = next_block;
    instr_limit -= header_ptr->inst_count();
    DecodedOp* next_head = next_block->FirstOp();
    CountLLVMBlockEntry(state, next_block, header_ptr->chain);
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    next_block->ExecCountStorage()++;
#endif
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = next_head;
#endif
    auto entry = header_ptr->entry;
    __builtin_assume(entry != nullptr);
    return {next_head, entry};
}

template <unsigned LinkSlot, bool EmitRegionEdge = true>
FORCE_INLINE ATTR_PRESERVE_NONE int64_t ResolveBranchTargetInline(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                                  mem::MicroTlbAbiWord utlb_addend, uint32_t target_eip,
                                                                  uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    (void)op;
    (void)utlb_tags;
    (void)utlb_addend;
    // The caller prepaid the full decoded block. Do this before handing the
    // budget to a local LLVM region edge, a cached link, or the slow resolver.
    if constexpr (LinkSlot == 1 && EmitRegionEdge) instr_limit += op->BranchSuffix();
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    if constexpr (EmitRegionEdge)
        FiberLLVMRegionEdge(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                            flags_cache FIBERCPU_PIN_RELOAD);
#else
    state->clear_pending_mem_op();
#endif
    if (instr_limit <= 0) {
        RestoreLLVMBlockOnExit(state);
        state->ctx.eip = target_eip;
        ATTR_MUSTTAIL return ReturnFromBlock(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                                             flags_cache FIBERCPU_PIN_ARGS);
    }

    static_assert(LinkSlot <= 1);
#if defined(FIBERCPU_LLVM_BLOCK_TEMPLATE) && defined(FIBERCPU_LLVM_SHARED_REGION_EXITS)
    if constexpr (EmitRegionEdge) {
        if (FiberLLVMShareRegionExits()) {
            RestoreLLVMBlockOnExit(state);
            constexpr auto exit = LinkSlot == 0 ? FiberLLVMRegionChainSingle : FiberLLVMRegionChainDouble;
            ATTR_MUSTTAIL return exit(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                                      flags_cache FIBERCPU_PIN_RELOAD);
        }
    }
#endif
    const auto next = TryChainBlock<LinkSlot>(state, op, target_eip, instr_limit);
    if (next.op) [[likely]] {
        ATTR_MUSTTAIL return next.entry(state, next.op, instr_limit, utlb_tags, utlb_addend,
                                        std::numeric_limits<uint32_t>::max(), flags_cache FIBERCPU_PIN_RELOAD);
    }

    // The successful chain above publishes its target. A miss still needs the source.
    RestoreLLVMBlockOnExit(state);
    if constexpr (LinkSlot == 0) {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowLink(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                                                         flags_cache FIBERCPU_PIN_RELOAD);
    } else {
        ATTR_MUSTTAIL return ResolveBranchTargetSlowDoubleSlot(state, op, instr_limit, utlb_tags, utlb_addend,
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
    const uint32_t target_eip =
        UseBranch ? branch : BakeableBlockEip<LLVMBlockEipField::Fallthrough>(op, state->last_block->sentinel_eip);
    ATTR_MUSTTAIL return ResolveBranchTargetInline<0>(state, op, instr_limit, utlb_tags, utlb_addend, target_eip,
                                                      flags_cache FIBERCPU_PIN_RELOAD);
}

template <LogicFunc Live, LogicFunc Dead>
FORCE_INLINE LogicFlow RunFlagPolicy(uint8_t metadata, LogicFuncParams) {
    if (metadata & Meta::no_flags_mask) return Dead(LogicPassParams);
    return Live(LogicPassParams);
}

template <class OperandPolicy>
FORCE_INLINE GprCache OperandGprCache(GprCache gpr) {
    if constexpr (requires { OperandPolicy::baked_address; }) gpr.baked_address = OperandPolicy::baked_address;
    return gpr;
}

// Unbound entries are only decoder/catalog identities. Distinct addresses
// remain observable even on linkers that fold identical function bodies.
template <auto Tag>
ATTR_PRESERVE_NONE int64_t DecodeHandlerKey(EmuState*, DecodedOp*, int64_t, mem::MicroTlbAbiWord, mem::MicroTlbAbiWord,
                                            uint32_t, uint64_t FIBERCPU_PIN_PARAMS) {
    static volatile unsigned char identity;
    (void)identity;
    __builtin_trap();
}

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState*, DecodedOp*, int64_t, mem::MicroTlbAbiWord, mem::MicroTlbAbiWord,
                                           uint32_t, uint64_t FIBERCPU_PIN_PARAMS) {
    static volatile unsigned char identity;
    (void)identity;
    __builtin_trap();
}

#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
struct DispatchSequenceHistory {
    uint8_t length;
    std::array<uintptr_t, 2> handlers;
};

FORCE_INLINE DispatchSequenceHistory BeginDispatchSequence(EmuState* state, DecodedOp* op) {
    DispatchSequenceHistory history{state->sequence_expected_op == op ? state->sequence_history_length : uint8_t{0},
                                    state->sequence_history};
    state->sequence_expected_op = nullptr;
    state->sequence_history_length = 0;
    return history;
}

FORCE_INLINE void RecordDispatchSequence(EmuState* state, DecodedOp* op, DecodedOp* next_op,
                                         const DispatchSequenceHistory& history) {
    if (next_op == state->last_block->Sentinel()) return;
    const auto current = reinterpret_cast<uintptr_t>(op->OriginalHandler());
    const auto next = reinterpret_cast<uintptr_t>(next_op->OriginalHandler());
    state->sequence_counts[{current, next}]++;
    if (history.length >= 1) state->ngram_counts[{{history.handlers[1], current, next, 0}}]++;
    if (history.length >= 2) state->ngram_counts[{{history.handlers[0], history.handlers[1], current, next}}]++;
    state->sequence_history = {history.handlers[1], current};
    state->sequence_history_length = static_cast<uint8_t>(std::min<int>(2, history.length + 1));
    state->sequence_expected_op = next_op;
}
#endif

template <LogicFunc Target, OperandSpec::Executable OperandPolicy>
FORCE_INLINE LogicFlow RunScalarLogic(EmuState* RESTRICT state, DecodedOp* RESTRICT op, mem::MicroTlbAbiWord& utlb_tags,
                                      mem::MicroTlbAbiWord& utlb_addend, uint32_t& branch, uint64_t& flags_cache,
                                      GprCache gpr) {
    mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    OperandPolicy::Assume(op);
    auto flow = Target(state, op, &utlb, OperandPolicy::Immediate(op), OperandPolicy::ModRM(op), &branch, flags_cache,
                       OperandGprCache<OperandPolicy>(gpr));
    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);
    return flow;
}

template <LogicFunc Target, OperandSpec::Executable OperandPolicy>
[[gnu::visibility("hidden")]]
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                           uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    const auto history = BeginDispatchSequence(state, op);
#endif
    const auto flow = RunScalarLogic<Target, OperandPolicy>(state, op, utlb_tags, utlb_addend, branch, flags_cache,
                                                            FIBERCPU_GPR_CACHE);

    switch (flow) {
        case LogicFlow::Continue:
            // Direct Relative Dispatch
            // Note: We don't check for 0 here for speed, assuming well-formed blocks
            // (sentinel always valid)
            if (auto* next_op = OperandSpec::Advance<OperandPolicy>(op)) {
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
                RecordDispatchSequence(state, op, next_op, history);
#endif
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
            ATTR_MUSTTAIL return ResolveBranchTargetInline<1>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                              flags_cache FIBERCPU_PIN_RELOAD);
        case LogicFlow::ExitToBranchSingleSlot:
            RecordBlockHandlersThrough(state, op);
            ATTR_MUSTTAIL return ResolveBranchTargetInline<0>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                              flags_cache FIBERCPU_PIN_RELOAD);
        default:
            FIBERCPU_PIN_COMMIT(state);
            CommitFlagsCache(state, flags_cache);
            return instr_limit;
    }
}

}  // namespace fiberish
