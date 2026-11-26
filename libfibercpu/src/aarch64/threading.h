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
    const DispatchSlot* exit_slot = nullptr;
    StepResult exit{};
    unsigned exit_edge = 2;
    uint64_t exit_target = 0;
};

inline StepResult CompleteStep(State& state, const DispatchSlot* last, StepResult step) {
    if (step.reason == Stop::Continue && !last->Has(RetainsPage)) {
        state.pc = last->PC();
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
inline bool MatchesWords(const Block& block, const uint8_t* code) {
    const auto* words = reinterpret_cast<const uint8_t*>(block.Words());
    unsigned count = block.Count();
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

inline void FlushThread(State& state, uint64_t pc, uint32_t nzcv A64_THREAD_PIN_PARAM) {
    state.pc = pc;
    state.nzcv = nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    state.x[FIBERCPU_AARCH64_PIN_REG] = pinned;
#endif
}

inline uint64_t FinishThread(State& state, const DispatchSlot* current, uint64_t remaining_after_block,
                             uint32_t nzcv A64_THREAD_PIN_PARAM, StepResult step) {
    FlushThread(state, current->PC(), nzcv A64_THREAD_PIN_PASS);
    auto& frame = *state.active_run;
    frame.exit_slot = current;
    frame.exit = step;
    return remaining_after_block + current->Sentinel()->Ordinal() - current->Ordinal() -
           uint64_t(RetiresOnExit(step.reason));
}

#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline ThreadHandler LinkedBlock(const Block*& target, const DispatchSlot& sentinel, unsigned edge, uint64_t target_pc,
                                 uint64_t& remaining_after_block) {
    if (!remaining_after_block) return nullptr;
    target = SentinelData(sentinel).links[edge];
    const auto bits = *reinterpret_cast<const volatile __uint128_t*>(&target->header);
    const auto header = std::bit_cast<BlockHeader>(bits);
    if (!header.IsValid() || header.Start() != target_pc || remaining_after_block < header.Count()) return nullptr;
    remaining_after_block -= header.Count();
#if defined(__clang__)
    __builtin_assume(header.entry != nullptr);
#endif
    return header.entry;
}

#define A64_THREAD_NEXT(next_slot)                                                                                    \
    do {                                                                                                              \
        const auto* a64_next = (next_slot);                                                                           \
        ATTR_MUSTTAIL return a64_next->thread(state, a64_next, remaining_after_block, data_tlb, tlb_tags, tlb_addend, \
                                              nzcv A64_THREAD_PIN_PASS);                                              \
    } while (false)

#define A64_THREAD_REPLAY(slot)                                                                                    \
    ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ReplayMemory(state, (slot), remaining_after_block, data_tlb, \
                                                                   tlb_tags, tlb_addend, nzcv A64_THREAD_PIN_PASS)

FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainSlow(A64_THREAD_PARAMS);

#ifdef ENABLE_TLB_STATS
#define A64_COUNT_DIRECT_LINK(source, target)                                                     \
    do {                                                                                          \
        if (auto* stats = state.active_run->memory.dispatch_stats) {                              \
            ++stats->direct_links;                                                                \
            stats->cross_page_links += (((source)->Owner()->pc ^ (target)->pc) & ~PageMask) != 0; \
        }                                                                                         \
    } while (false)
#else
#define A64_COUNT_DIRECT_LINK(source, target) ((void)0)
#endif

#define A64_THREAD_LINK(sentinel, edge, target_pc)                                                            \
    do {                                                                                                      \
        const ::fiberish::aarch64::detail::Block* a64_target_block;                                           \
        if (const auto a64_entry = ::fiberish::aarch64::detail::LinkedBlock(                                  \
                a64_target_block, *(sentinel), (edge), (target_pc), remaining_after_block)) [[likely]] {      \
            A64_COUNT_DIRECT_LINK((sentinel), a64_target_block);                                              \
            ATTR_MUSTTAIL return a64_entry(state, a64_target_block->Slots(), remaining_after_block, data_tlb, \
                                           tlb_tags, tlb_addend, nzcv A64_THREAD_PIN_PASS);                   \
        }                                                                                                     \
        auto& a64_frame = *state.active_run;                                                                  \
        a64_frame.exit_edge = (edge);                                                                         \
        a64_frame.exit_target = (target_pc);                                                                  \
        ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ChainSlow(A64_THREAD_ARGS);                         \
    } while (false)

#define A64_THREAD_BRANCH(target_pc)                                                                   \
    do {                                                                                               \
        const uint64_t a64_target = (target_pc);                                                       \
        const auto* a64_sentinel = current->Sentinel();                                                \
        if (current->Ordinal() + 1 == a64_sentinel->Ordinal())                                         \
            A64_THREAD_LINK(a64_sentinel, a64_target == a64_sentinel->PC(), a64_target);               \
        return ::fiberish::aarch64::detail::FinishThread(                                              \
            state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS,                           \
            {::fiberish::aarch64::Stop::Branch, ::fiberish::aarch64::Access::Execute, 0, a64_target}); \
    } while (false)

FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainDirect(A64_THREAD_PARAMS);

#define A64_THREAD_DIRECT() ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ChainDirect(A64_THREAD_ARGS)

// Cold dispatch reconstructs the full operation from the saved instruction word.
DecodedInstruction ScalarInstruction(const DispatchSlot& slot);

// Scalar entry and memory replay publish all architectural state before any
// call and reload it afterward. Only a callback-free access resumes the span.
#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_THREAD_ABI inline uint64_t ReplayMemory(A64_THREAD_PARAMS) {
    FlushThread(state, current->PC(), nzcv A64_THREAD_PIN_PASS);
    const auto& memory = state.active_run->memory;
#ifdef ENABLE_TLB_STATS
    if (auto* stats = memory.dispatch_stats) ++stats->memory_replays;
#endif
    const auto op = ScalarInstruction(*current);
    auto step = CompleteStep(state, current, op.handler(state, memory, op));
    tlb_tags = mem::InvalidMicroTlbAbiTags();
    if (current->Has(UntrustedScalar)) {
        state.active_run->executable = nullptr;
        if (step.reason == Stop::Continue) step.reason = Stop::Refetch;
    }
    nzcv = state.nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    pinned = state.x[FIBERCPU_AARCH64_PIN_REG];
#endif
    if (step.reason != Stop::Continue) {
        return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
    }
    A64_THREAD_NEXT(current->Next());
}

template <Handler Execute, unsigned span = 1>
FIBERCPU_AARCH64_THREAD_ABI uint64_t SafeScalar(A64_THREAD_PARAMS) {
    Block::Fusion fusion;
    if constexpr (span == 0) fusion = current->Owner()->FusedBinding(current->Ordinal());
    const unsigned length = span ? span : fusion.length;
    const auto* last = current->Last(length);
    const bool untrusted =
        Execute == nullptr && span != 1 ? current->Has(UntrustedFused) : current->Has(UntrustedScalar);
    FlushThread(state, current->PC(), nzcv A64_THREAD_PIN_PASS);
    const auto& memory = state.active_run->memory;
    const auto op = ScalarInstruction(*current);
    StepResult step;
    if constexpr (Execute != nullptr)
        step = Execute(state, memory, op);
    else if constexpr (span != 1)
        step = fusion.handler(state, memory, op);
    else
        step = op.handler(state, memory, op);
    nzcv = state.nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    pinned = state.x[FIBERCPU_AARCH64_PIN_REG];
#endif
    tlb_tags = mem::InvalidMicroTlbAbiTags();
    if (step.reason == Stop::Continue) {
        step = CompleteStep(state, last, step);
        if (step.reason == Stop::Continue && !untrusted) A64_THREAD_NEXT(last->Next());
        // An unknown scalar fusion can invoke arbitrary memory callbacks.
        step.reason = Stop::Refetch;
        return FinishThread(state, last, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
    }
    const auto offset = (state.pc - current->PC()) / 4;
    if (offset < length) current = current->Last(unsigned(offset) + 1);
    if (untrusted) {
        state.active_run->executable = nullptr;
    } else if (step.reason == Stop::Branch) {
        A64_THREAD_BRANCH(step.address);
    }
    return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
}

template <bool compact_pc>
FIBERCPU_AARCH64_THREAD_ABI uint64_t EndBlock(A64_THREAD_PARAMS) {
    const uint64_t target = compact_pc ? current->Operands() : current->PC();
    A64_THREAD_LINK(current, 1, target);
}
#endif

constexpr ThreadHandler EndBlockThread([[maybe_unused]] uint64_t fallthrough) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return fallthrough <= DispatchSlot::OperandMask ? &EndBlock<true> : &EndBlock<false>;
#else
    return nullptr;
#endif
}

template <Kind kind, Handler Execute>
constexpr HandlerBinding BindHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {Execute, &SafeScalar<Execute>};
#else
    return {Execute, nullptr};
#endif
}

constexpr ThreadHandler DynamicThread() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return &SafeScalar<nullptr>;
#else
    return nullptr;
#endif
}

constexpr ThreadHandler FusedThread() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return &SafeScalar<nullptr, 0>;
#else
    return nullptr;
#endif
}

}  // namespace fiberish::aarch64::detail
