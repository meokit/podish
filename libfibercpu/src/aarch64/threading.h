#pragma once

#include <cstring>

#include "../musttail.h"
#include "aarch64.h"

#if FIBERCPU_HAS_MUSTTAIL && !defined(FIBERCPU_AARCH64_LOOP_DISPATCH)
#define FIBERCPU_AARCH64_TAIL_DISPATCH 1
#else
#define FIBERCPU_AARCH64_TAIL_DISPATCH 0
#endif

namespace fiberish::aarch64::detail {

constexpr unsigned BlockMax = 24;
constexpr unsigned PageBytes = 4096;
constexpr uint64_t PageMask = PageBytes - 1;

struct Block;

struct DispatchSlot {
    ThreadHandler thread = nullptr;
    uint64_t pc = 0;
    DecodedInstruction op{};
    union {
        Handler fused = nullptr;
        const Block* owner;  // Only the sentinel has an owner pointer.
    };
    uint8_t remaining = 0;  // Includes this member, excludes the sentinel.
    uint8_t fused_length = 1;
    bool retains_page = false;
    bool can_follow = false;
    bool omits_flags = false;
    bool untrusted_scalar = false;
    bool untrusted_fused = false;
};

static_assert(offsetof(DispatchSlot, thread) == 0);

struct BlockLink {
    const Block* target = nullptr;
    uint64_t replacement = 0;
};

struct Block {
    uint64_t pc = 0;
    const uint8_t* executable_page = nullptr;
    decltype(Profile::resolve) resolver = nullptr;
    decltype(Profile::resolve_pair) pair_resolver = nullptr;
    decltype(Profile::resolve_sequence) sequence_resolver = nullptr;
    unsigned count = 0;
    bool trusted_baking = false;
    uint64_t replacement = 0;
    mutable std::array<BlockLink, 2> successors{};
    std::array<uint32_t, BlockMax> words{};
    std::array<DispatchSlot, BlockMax + 1> slots{};
};

struct RunFrame {
    const Memory& memory;
    const uint8_t* executable = nullptr;
    const DispatchSlot* exit_slot = nullptr;
    StepResult exit{};
};

inline StepResult CompleteStep(State& state, const DispatchSlot* last, StepResult step) {
    if (step.reason == Stop::Continue && !last->retains_page) {
        state.pc = last->pc;
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
    const auto* words = reinterpret_cast<const uint8_t*>(block.words.data());
    unsigned count = block.count;
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
    FlushThread(state, current->pc, nzcv A64_THREAD_PIN_PASS);
    auto& frame = *state.active_run;
    frame.exit_slot = current;
    frame.exit = step;
    return remaining_after_block + current->remaining - uint64_t(RetiresOnExit(step.reason));
}

#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline const Block* LinkedBlock(const Block& source, uint64_t target_pc, uint64_t remaining_after_block) {
    if (!remaining_after_block || (target_pc & 3) || ((target_pc ^ source.pc) & ~PageMask)) return nullptr;
    for (const auto& link : source.successors) {
        const auto* target = link.target;
        if (target && target->pc == target_pc && target->replacement == link.replacement &&
            target->executable_page == source.executable_page && remaining_after_block >= target->count &&
            MatchesWords(*target, source.executable_page + (target_pc & PageMask)))
            return target;
    }
    return nullptr;
}

#define A64_THREAD_NEXT(length)                                                                 \
    do {                                                                                        \
        const auto* a64_next = current + (length);                                              \
        ATTR_MUSTTAIL return a64_next->thread(state, a64_next, remaining_after_block, read_tlb, \
                                              nzcv A64_THREAD_PIN_PASS);                        \
    } while (false)

#define A64_THREAD_REPLAY(slot)                                                                                    \
    ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ReplayMemory(state, (slot), remaining_after_block, read_tlb, \
                                                                   nzcv A64_THREAD_PIN_PASS)

#define A64_THREAD_LINK(sentinel, target_pc)                                                                          \
    do {                                                                                                              \
        if (const auto* a64_link =                                                                                    \
                ::fiberish::aarch64::detail::LinkedBlock(*(sentinel)->owner, (target_pc), remaining_after_block)) {   \
            const auto* a64_next = a64_link->slots.data();                                                            \
            ATTR_MUSTTAIL return a64_next->thread(state, a64_next, remaining_after_block - a64_link->count, read_tlb, \
                                                  nzcv A64_THREAD_PIN_PASS);                                          \
        }                                                                                                             \
    } while (false)

#define A64_THREAD_BRANCH(target_pc)                                                                   \
    do {                                                                                               \
        const uint64_t a64_target = (target_pc);                                                       \
        if (current->remaining == 1) A64_THREAD_LINK(current + 1, a64_target);                         \
        return ::fiberish::aarch64::detail::FinishThread(                                              \
            state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS,                           \
            {::fiberish::aarch64::Stop::Branch, ::fiberish::aarch64::Access::Execute, 0, a64_target}); \
    } while (false)

FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainDirect(A64_THREAD_PARAMS);

#define A64_THREAD_DIRECT() ATTR_MUSTTAIL return ::fiberish::aarch64::detail::ChainDirect(A64_THREAD_ARGS)

// Scalar entry and memory replay publish all architectural state before any
// call and reload it afterward. Only a callback-free access resumes the span.
#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_THREAD_ABI inline uint64_t ReplayMemory(A64_THREAD_PARAMS) {
    FlushThread(state, current->pc, nzcv A64_THREAD_PIN_PASS);
    const auto& memory = state.active_run->memory;
    auto step = CompleteStep(state, current, current->op.handler(state, memory, current->op));
    if (current->untrusted_scalar) {
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
    A64_THREAD_NEXT(1);
}

template <Handler Execute, unsigned span = 1>
FIBERCPU_AARCH64_THREAD_ABI uint64_t SafeScalar(A64_THREAD_PARAMS) {
    const unsigned length = span ? span : current->fused_length;
    const auto* last = current + length - 1;
    const bool untrusted = Execute == nullptr && span != 1 ? current->untrusted_fused : current->untrusted_scalar;
    FlushThread(state, current->pc, nzcv A64_THREAD_PIN_PASS);
    const auto& memory = state.active_run->memory;
    StepResult step;
    if constexpr (Execute != nullptr)
        step = Execute(state, memory, current->op);
    else if constexpr (span != 1)
        step = current->fused(state, memory, current->op);
    else
        step = current->op.handler(state, memory, current->op);
    nzcv = state.nzcv;
#if FIBERCPU_AARCH64_PIN_REG >= 0
    pinned = state.x[FIBERCPU_AARCH64_PIN_REG];
#endif
    if (step.reason == Stop::Continue) {
        step = CompleteStep(state, last, step);
        if (step.reason == Stop::Continue && !untrusted) A64_THREAD_NEXT(length);
        // An unknown scalar fusion can invoke arbitrary memory callbacks.
        step.reason = Stop::Refetch;
        return FinishThread(state, last, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
    }
    const auto offset = (state.pc - current->pc) / 4;
    current += offset < length ? offset : 0;
    if (untrusted) {
        state.active_run->executable = nullptr;
    } else if (step.reason == Stop::Branch) {
        A64_THREAD_BRANCH(step.address);
    }
    return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
}

FIBERCPU_AARCH64_THREAD_ABI inline uint64_t EndBlock(A64_THREAD_PARAMS) {
    A64_THREAD_LINK(current, current->pc);
    return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS, {});
}
#endif

constexpr ThreadHandler EndBlockThread() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return &EndBlock;
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
