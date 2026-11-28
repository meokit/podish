#pragma once

#include <bit>

#include "../mem/tlb.h"
#include "../musttail.h"
#include "block.h"

#if FIBERCPU_AARCH64_UNMANAGED_CACHE
#include <cstring>
#endif

#if FIBERCPU_HAS_MUSTTAIL && !defined(FIBERCPU_AARCH64_LOOP_DISPATCH)
#define FIBERCPU_AARCH64_TAIL_DISPATCH 1
#else
#define FIBERCPU_AARCH64_TAIL_DISPATCH 0
#endif

namespace fiberish::aarch64::detail {

struct RunFrame {
    const Memory& memory;
    // Threaded dispatch carries the remaining budget minus this reserve
    // and one. A nonnegative value can enter another block.
    int64_t budget_reserve = 0;
    const uint8_t* executable = nullptr;
    uint32_t exit_word = 0;
    StepResult exit{};
    uint64_t* exit_link = nullptr;
    uint64_t exit_target = 0;
#if FIBERCPU_LLVM_JIT
    const BasicBlock* llvm_hot_block = nullptr;
#endif
};

inline StepResult CompleteStep(State& state, const DecodedOp* last, StepResult step) {
    if (step.reason == Stop::Continue && !last->Has(RetainsPage)) {
        state.pc = last->GetStartPc(state);
        step.reason = Stop::Refetch;
    }
    return step;
}

constexpr bool RetiresOnExit(Stop reason) {
    return reason == Stop::Branch || reason == Stop::Refetch || reason == Stop::Syscall;
}

#if FIBERCPU_AARCH64_UNMANAGED_CACHE
template <class Word>
inline Word CodeWord(const uint8_t* code) {
    Word word;
    std::memcpy(&word, code, sizeof(word));
    return word;
}

A64_FORCE_INLINE bool MatchesWords(const BasicBlock& block, const uint8_t* code) {
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
#endif

inline void CommitDispatchState(State& state, uint64_t pc, uint32_t flags_cache A64_HANDLER_PIN_PARAMS) {
    state.pc = pc;
    state.nzcv = flags_cache;
#define A64_PIN_COMMIT(reg, slot) PinSlot<reg>(state) = pin_##slot;
    FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_COMMIT)
#undef A64_PIN_COMMIT
}

inline void ReloadDispatchPins([[maybe_unused]] State& state A64_HOT_PIN_REF) {
#define A64_PIN_RELOAD(reg, slot) pin_##slot = PinSlot<reg>(state);
    FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_RELOAD)
#undef A64_PIN_RELOAD
}

inline int64_t ReturnFromBlock(State& state, const DecodedOp* current, int64_t instr_limit,
                               uint32_t flags_cache A64_HANDLER_PIN_PARAMS, StepResult step, uint32_t word = 0) {
    CommitDispatchState(state, current->GetStartPc(state), flags_cache A64_HANDLER_PIN_ARGS);
    auto& frame = *state.active_run;
    frame.exit_word = word;
    frame.exit = step;
    return instr_limit + state.last_block->inst_count() - current->GuestIndex() - int64_t(RetiresOnExit(step.reason));
}

#if FIBERCPU_LLVM_JIT
A64_FORCE_INLINE void CountLLVMBlockEntry(State& state, const BasicBlock& block, BasicBlockChainPrefix chain) {
    const auto count = chain.llvm_hot_count;
    if (!count) [[likely]]
        return;
    if (count > 1) {
        block.header.chain.llvm_hot_count = count - 1;
    } else [[unlikely]] {
        auto& pending = state.active_run->llvm_hot_block;
        if (!pending) {
            block.header.chain.llvm_hot_count = 0;
            pending = &block;
        }
    }
}
#endif

A64_FORCE_INLINE HandlerFunc TryChainBlock(State& state, const BasicBlock*& target, uint64_t link, uint64_t target_pc,
                                           int64_t& instr_limit) {
#if !FIBERCPU_AARCH64_TAIL_DISPATCH
    (void)state;
    (void)target;
    (void)link;
    (void)target_pc;
    (void)instr_limit;
    return nullptr;
#else
    if (instr_limit < 0) return nullptr;
    target = LoadBlockLink(link);
    const auto bits = *reinterpret_cast<const volatile __uint128_t*>(&target->header);
    const auto header = std::bit_cast<BasicBlockHeader>(bits);
    if (!header.IsValid() || header.start_pc() != target_pc) return nullptr;
#ifdef ENABLE_TLB_STATS
    if (auto* stats = state.active_run->memory.dispatch_stats) {
        ++stats->direct_links;
        stats->cross_page_links += ((state.last_block->start_pc() ^ target_pc) & ~PageMask) != 0;
    }
#endif
    state.last_block = target;
    instr_limit -= header.inst_count();
#if FIBERCPU_LLVM_JIT
    CountLLVMBlockEntry(state, *target, header.chain);
#endif
#if defined(__clang__)
    __builtin_assume(header.entry != nullptr);
#endif
    return header.entry;
#endif
}

#define A64_DISPATCH_NEXT(next_slot)                                                       \
    do {                                                                                   \
        const auto* a64_next = (next_slot);                                                \
        ATTR_MUSTTAIL return a64_next->handler(state, a64_next, instr_limit A64_UTLB_ARGS, \
                                               flags_cache A64_HANDLER_PIN_ARGS);          \
    } while (false)

FIBERCPU_AARCH64_HANDLER_ABI int64_t MemoryOpGeneric(A64_HANDLER_PARAMS);
FIBERCPU_AARCH64_HANDLER_ABI int64_t ResolveBranchTargetSlow(A64_HANDLER_PARAMS);
FIBERCPU_AARCH64_HANDLER_ABI int64_t OpExitBlock(A64_HANDLER_PARAMS);
FIBERCPU_AARCH64_HANDLER_ABI int64_t OpExitSingleStep(A64_HANDLER_PARAMS);

#define A64_MEMORY_OP_GENERIC(slot)                                                                             \
    ATTR_MUSTTAIL return ::fiberish::aarch64::detail::MemoryOpGeneric(state, (slot), instr_limit A64_UTLB_ARGS, \
                                                                      flags_cache A64_HANDLER_PIN_ARGS)

#define A64_RESOLVE_BRANCH_TARGET_INLINE(link, target_pc)                                                 \
    do {                                                                                                  \
        auto& a64_link = (link);                                                                          \
        const auto a64_target_pc = (target_pc);                                                           \
        const ::fiberish::aarch64::detail::BasicBlock* a64_target_block;                                  \
        if (const auto a64_entry = ::fiberish::aarch64::detail::TryChainBlock(                            \
                state, a64_target_block, a64_link, a64_target_pc, instr_limit)) [[likely]] {              \
            ATTR_MUSTTAIL return a64_entry(state, a64_target_block->FirstOp(), instr_limit A64_UTLB_ARGS, \
                                           flags_cache A64_HANDLER_PIN_ARGS);                             \
        }                                                                                                 \
        auto& a64_frame = *state.active_run;                                                              \
        a64_frame.exit_link = &a64_link;                                                                  \
        a64_frame.exit_target = a64_target_pc;                                                            \
        ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ResolveBranchTargetSlow(A64_HANDLER_ARGS);      \
    } while (false)

enum class DispatchBranch : uint8_t {
    None,
    DirectUnconditional,
    DirectConditional,
    Indirect,
};

constexpr DispatchBranch BranchPolicy(Kind kind) {
    switch (kind) {
        case Kind::BranchImmediate:
            return DispatchBranch::DirectUnconditional;
        case Kind::BranchCondition:
        case Kind::CompareBranch:
        case Kind::TestBranch:
            return DispatchBranch::DirectConditional;
        case Kind::BranchRegister:
            return DispatchBranch::Indirect;
        default:
            return DispatchBranch::None;
    }
}

struct DispatchPolicy {
    bool wide = false;
    bool may_replay = false;
    bool may_exit = false;
    DispatchBranch branch = DispatchBranch::None;
};

using LogicFunc = StepResult (*)(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                 uint32_t& flags_cache A64_HOT_PIN_REF);

template <LogicFunc Logic, DispatchPolicy Policy>
#if defined(__GNUC__) || defined(__clang__)
[[gnu::visibility("hidden")]]
#endif
FIBERCPU_AARCH64_HANDLER_ABI int64_t DispatchWrapper(A64_HANDLER_PARAMS) {
    StepResult step;
#if defined(__clang__)
    [[clang::always_inline]]
#endif
    step = Logic(state, current A64_UTLB_ARGS, flags_cache A64_HANDLER_PIN_ARGS);
    if constexpr (Policy.may_replay) {
        if (step.reason == Stop::Replay) [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
    }
    if constexpr (Policy.branch != DispatchBranch::None) {
        if (Policy.branch == DispatchBranch::DirectUnconditional || step.reason == Stop::Branch) {
            if constexpr (Policy.branch == DispatchBranch::Indirect) {
                const auto& extra = GetExtraOperands(*current);
                A64_RESOLVE_BRANCH_TARGET_INLINE(
                    step.address == extra.primary + 4 ? current->NextOp<true>()->payload : extra.secondary,
                    step.address);
            } else {
                if constexpr (Policy.branch == DispatchBranch::DirectConditional)
                    instr_limit += current->BranchSuffix();
                const auto& branch = GetExtraOperands(*current);
                A64_RESOLVE_BRANCH_TARGET_INLINE(branch.secondary, branch.primary);
            }
        }
    }
    if constexpr (Policy.may_exit) {
        if (step.reason != Stop::Continue) [[unlikely]] {
            const auto word = state.last_block->Word(current->GuestIndex());
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step, word);
        }
    }
    A64_DISPATCH_NEXT(current->template NextOp<Policy.wide>());
}

constexpr HandlerFunc GetExitHandlerFallthrough() { return &OpExitBlock; }

}  // namespace fiberish::aarch64::detail
