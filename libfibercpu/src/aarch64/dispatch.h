#pragma once

#include <bit>
#include <cstring>

#include "../mem/tlb.h"
#include "../musttail.h"
#include "block.h"

#if FIBERCPU_HAS_MUSTTAIL && !defined(FIBERCPU_AARCH64_LOOP_DISPATCH)
#define FIBERCPU_AARCH64_TAIL_DISPATCH 1
#else
#define FIBERCPU_AARCH64_TAIL_DISPATCH 0
#endif

namespace fiberish::aarch64::detail {

struct RunFrame {
    const Memory& memory;
    const uint8_t* executable = nullptr;
    const DecodedOp* exit_op = nullptr;
    StepResult exit{};
    unsigned exit_edge = 2;
    uint64_t exit_target = 0;
};

inline StepResult CompleteStep(State& state, const DecodedOp* last, StepResult step) {
    if (step.reason == Stop::Continue && !last->Has(RetainsPage)) {
        state.pc = last->GetStartPc();
        step.reason = Stop::Refetch;
    }
    return step;
}

constexpr bool RetiresOnExit(Stop reason) {
    return reason == Stop::Branch || reason == Stop::Refetch || reason == Stop::Syscall;
}

template <class Word>
inline Word CodeWord(const uint8_t* code) {
    Word word;
    std::memcpy(&word, code, sizeof(word));
    return word;
}

#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline bool MatchesWords(const BasicBlock& block, const uint8_t* code) {
    const auto* words = reinterpret_cast<const uint8_t*>(block.Words());
    unsigned count = block.inst_count();
    while (count >= 4) {
        const uint64_t difference = (CodeWord<uint64_t>(code) ^ CodeWord<uint64_t>(words)) |
                                    (CodeWord<uint64_t>(code + 8) ^ CodeWord<uint64_t>(words + 8));
        if (difference) return false;
        count -= 4;
        code += 16;
        words += 16;
    }
    if (count & 2) {
        if (CodeWord<uint64_t>(code) != CodeWord<uint64_t>(words)) return false;
        code += 8;
        words += 8;
    }
    return !(count & 1) || CodeWord<uint32_t>(code) == CodeWord<uint32_t>(words);
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH

inline void CommitDispatchState(State& state, uint64_t pc, uint32_t flags_cache A64_HANDLER_PIN_PARAMS) {
    state.pc = pc;
    state.nzcv = flags_cache;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    state.x[FIBERCPU_AARCH64_PIN_REG] = pinned;
#endif
}

inline uint64_t ReturnFromBlock(State& state, const DecodedOp* current, uint64_t instr_limit,
                                uint32_t flags_cache A64_HANDLER_PIN_PARAMS, StepResult step) {
    CommitDispatchState(state, current->GetStartPc(), flags_cache A64_HANDLER_PIN_ARGS);
    auto& frame = *state.active_run;
    frame.exit_op = current;
    frame.exit = step;
    return instr_limit + current->Sentinel()->GuestIndex() - current->GuestIndex() -
           uint64_t(RetiresOnExit(step.reason));
}

#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline HandlerFunc TryChainBlock(const BasicBlock*& target, const DecodedOp& sentinel, unsigned edge,
                                 uint64_t target_pc, uint64_t& instr_limit) {
    if (!instr_limit) return nullptr;
    target = GetBlockSentinel(sentinel).links[edge];
    const auto bits = *reinterpret_cast<const volatile __uint128_t*>(&target->header);
    const auto header = std::bit_cast<BasicBlockHeader>(bits);
    if (!header.IsValid() || header.start_pc() != target_pc || instr_limit < header.inst_count()) return nullptr;
    instr_limit -= header.inst_count();
#if defined(__clang__)
    __builtin_assume(header.entry != nullptr);
#endif
    return header.entry;
}

#define A64_DISPATCH_NEXT(next_slot)                                                                           \
    do {                                                                                                       \
        const auto* a64_next = (next_slot);                                                                    \
        ATTR_MUSTTAIL return a64_next->handler(state, a64_next, instr_limit, data_tlb, utlb_tags, utlb_addend, \
                                               flags_cache A64_HANDLER_PIN_ARGS);                              \
    } while (false)

#define A64_MEMORY_OP_GENERIC(slot)                                                                                    \
    ATTR_MUSTTAIL return ::fiberish::aarch64::detail::MemoryOpGeneric(state, (slot), instr_limit, data_tlb, utlb_tags, \
                                                                      utlb_addend, flags_cache A64_HANDLER_PIN_ARGS)

FIBERCPU_AARCH64_HANDLER_ABI uint64_t ResolveBranchTargetSlow(A64_HANDLER_PARAMS);

#ifdef ENABLE_TLB_STATS
#define A64_COUNT_DIRECT_LINK(source, target)                                                                       \
    do {                                                                                                            \
        if (auto* stats = state.active_run->memory.dispatch_stats) {                                                \
            ++stats->direct_links;                                                                                  \
            stats->cross_page_links += (((source)->OwningBlock()->start_pc ^ (target)->start_pc) & ~PageMask) != 0; \
        }                                                                                                           \
    } while (false)
#else
#define A64_COUNT_DIRECT_LINK(source, target) ((void)0)
#endif

#define A64_RESOLVE_BRANCH_TARGET_INLINE(sentinel, edge, target_pc)                                                   \
    do {                                                                                                              \
        const ::fiberish::aarch64::detail::BasicBlock* a64_target_block;                                              \
        if (const auto a64_entry = ::fiberish::aarch64::detail::TryChainBlock(a64_target_block, *(sentinel), (edge),  \
                                                                              (target_pc), instr_limit)) [[likely]] { \
            A64_COUNT_DIRECT_LINK((sentinel), a64_target_block);                                                      \
            ATTR_MUSTTAIL return a64_entry(state, a64_target_block->FirstOp(), instr_limit, data_tlb, utlb_tags,      \
                                           utlb_addend, flags_cache A64_HANDLER_PIN_ARGS);                            \
        }                                                                                                             \
        auto& a64_frame = *state.active_run;                                                                          \
        a64_frame.exit_edge = (edge);                                                                                 \
        a64_frame.exit_target = (target_pc);                                                                          \
        ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ResolveBranchTargetSlow(A64_HANDLER_ARGS);                  \
    } while (false)

#define A64_RESOLVE_BRANCH_TARGET(target_pc)                                                                      \
    do {                                                                                                          \
        const uint64_t a64_target = (target_pc);                                                                  \
        const auto* a64_sentinel = current->Sentinel();                                                           \
        if (current->GuestIndex() + 1 == a64_sentinel->GuestIndex())                                              \
            A64_RESOLVE_BRANCH_TARGET_INLINE(a64_sentinel, a64_target == a64_sentinel->GetStartPc(), a64_target); \
        return ::fiberish::aarch64::detail::ReturnFromBlock(                                                      \
            state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,                                        \
            {::fiberish::aarch64::Stop::Branch, ::fiberish::aarch64::Access::Execute, 0, a64_target});            \
    } while (false)

FIBERCPU_AARCH64_HANDLER_ABI uint64_t ResolveDirectBranchTarget(A64_HANDLER_PARAMS);

#define A64_RESOLVE_DIRECT_BRANCH_TARGET() \
    ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ResolveDirectBranchTarget(A64_HANDLER_ARGS)

// Cold dispatch reconstructs the full operation from the saved instruction word.
DecodedInstruction ReadScalarOp(const DecodedOp& slot);

// Scalar entry and memory replay publish all architectural state before any
// call and reload it afterward. Only a callback-free access resumes the span.
#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_HANDLER_ABI inline uint64_t MemoryOpGeneric(A64_HANDLER_PARAMS) {
    CommitDispatchState(state, current->GetStartPc(), flags_cache A64_HANDLER_PIN_ARGS);
    const auto& memory = state.active_run->memory;
#ifdef ENABLE_TLB_STATS
    if (auto* stats = memory.dispatch_stats) ++stats->memory_replays;
#endif
    const auto op = ReadScalarOp(*current);
    auto step = CompleteStep(state, current, op.scalar_handler(state, memory, op));
    utlb_tags = mem::InvalidMicroTlbAbiTags();
    if (current->Has(UntrustedScalar)) {
        state.active_run->executable = nullptr;
        if (step.reason == Stop::Continue) step.reason = Stop::Refetch;
    }
    flags_cache = state.nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    pinned = state.x[FIBERCPU_AARCH64_PIN_REG];
#endif
    if (step.reason != Stop::Continue) {
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
    }
    A64_DISPATCH_NEXT(current->NextOp());
}

template <ScalarHandlerFunc Execute, unsigned span = 1>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t RunScalarHandler(A64_HANDLER_PARAMS) {
    BasicBlock::Fusion fusion;
    if constexpr (span == 0) fusion = current->OwningBlock()->FusedBinding(current->GuestIndex());
    const unsigned length = span ? span : fusion.length;
    const auto* last = current->LastOp(length);
    const bool untrusted =
        Execute == nullptr && span != 1 ? current->Has(UntrustedFused) : current->Has(UntrustedScalar);
    CommitDispatchState(state, current->GetStartPc(), flags_cache A64_HANDLER_PIN_ARGS);
    const auto& memory = state.active_run->memory;
    const auto op = ReadScalarOp(*current);
    StepResult step;
    if constexpr (Execute != nullptr)
        step = Execute(state, memory, op);
    else if constexpr (span != 1)
        step = fusion.handler(state, memory, op);
    else
        step = op.scalar_handler(state, memory, op);
    flags_cache = state.nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    pinned = state.x[FIBERCPU_AARCH64_PIN_REG];
#endif
    utlb_tags = mem::InvalidMicroTlbAbiTags();
    if (step.reason == Stop::Continue) {
        step = CompleteStep(state, last, step);
        if (step.reason == Stop::Continue && !untrusted) A64_DISPATCH_NEXT(last->NextOp());
        // An unknown scalar fusion can invoke arbitrary memory callbacks.
        step.reason = Stop::Refetch;
        return ReturnFromBlock(state, last, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
    }
    const auto offset = (state.pc - current->GetStartPc()) / 4;
    if (offset < length) current = current->LastOp(unsigned(offset) + 1);
    if (untrusted) {
        state.active_run->executable = nullptr;
    } else if (step.reason == Stop::Branch) {
        A64_RESOLVE_BRANCH_TARGET(step.address);
    }
    return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
}

template <bool compact_pc>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t OpExitBlock(A64_HANDLER_PARAMS) {
    const uint64_t target = compact_pc ? current->Operands() : current->GetStartPc();
    A64_RESOLVE_BRANCH_TARGET_INLINE(current, 1, target);
}
#endif

constexpr HandlerFunc GetExitHandlerFallthrough([[maybe_unused]] uint64_t fallthrough) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return fallthrough <= DecodedOp::OperandMask ? &OpExitBlock<true> : &OpExitBlock<false>;
#else
    return nullptr;
#endif
}

constexpr HandlerFunc GetScalarDispatchHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return &RunScalarHandler<nullptr>;
#else
    return nullptr;
#endif
}

constexpr HandlerFunc GetFusedDispatchHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return &RunScalarHandler<nullptr, 0>;
#else
    return nullptr;
#endif
}

}  // namespace fiberish::aarch64::detail
