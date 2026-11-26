#pragma once

#include "block_operands.h"
#include "flag_liveness.h"
#include "memory.h"
#include "threading.h"

namespace fiberish::aarch64 {

// Keep a trained sequence in its threaded entry. In particular, crossing a
// platform-ABI function boundary forces live dispatch registers onto the stack.
#if defined(_MSC_VER)
#define A64_BAKED_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_BAKED_INLINE inline __attribute__((always_inline))
#else
#define A64_BAKED_INLINE inline
#endif

constexpr bool CanBake(uint32_t word) {
    const auto kind = detail::DecodeFields(word).kind;
    return (kind >= Kind::Address && kind <= Kind::TestBranch) ||
           (kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral);
}

constexpr bool IsBakedTransfer(Kind kind) { return kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral; }

template <uint32_t Word, bool dynamic_imm12>
A64_BAKED_INLINE decltype(auto) BakedOperands(const DecodedInstruction& slot) {
    static_assert(CanBake(Word));
    static constexpr auto shape = detail::DecodeFields(Word);
    if constexpr (dynamic_imm12) {
        static_assert(shape.kind == Kind::AddSubImmediate ||
                      (shape.kind == Kind::LoadStoreImmediate && detail::Field(Word, 24, 1)));
        auto operands = shape;
        operands.immediate = slot.immediate;
        return operands;
    } else {
        return (shape);
    }
}

template <uint32_t Word, bool write_nzcv = true, bool dynamic_imm12 = false>
A64_BAKED_INLINE StepResult Baked(State& state, const Memory& memory, const DecodedInstruction& slot) {
    constexpr auto op = detail::DecodeFields(Word);
    const auto& operands = BakedOperands<Word, dynamic_imm12>(slot);
    if constexpr (IsBakedTransfer(op.kind)) {
#if defined(__clang__)
        [[clang::always_inline]]
#endif
        return detail::LoadStore<op.kind, op.width, op.operation, op.mode, op.shift, detail::Field(Word, 26, 1)>(
            state, memory, operands);
    } else {
#if defined(__clang__)
        [[clang::always_inline]]
#endif
        return detail::Integer<op.kind, op.width, op.operation, op.shift, write_nzcv>(state, memory, operands);
    }
}

template <uint32_t Word>
constexpr Handler BakedHandler() {
    if constexpr (CanBake(Word)) return &Baked<Word>;
    return nullptr;
}

constexpr bool CanFollowInBakedSequence(uint32_t word) {
    const auto op = detail::DecodeFields(word);
    return (op.kind >= Kind::Address && op.kind <= Kind::Multiply) ||
           (op.kind >= Kind::LoadStoreImmediate && op.kind <= Kind::LoadLiteral);
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
namespace detail {

struct BakedStop {
    const detail::DispatchSlot* member;
    StepResult step;
};

template <uint32_t dead, bool dynamic_imm12, uint32_t Word, uint32_t... Rest>
A64_BAKED_INLINE BakedStop ExecuteBakedThread(State& state, const detail::DispatchSlot* member,
                                              const mem::SoftTlb* data_tlb, uint64_t& tlb_tags, uintptr_t& tlb_addend,
                                              uint32_t& nzcv A64_HOT_PIN_REF) {
    constexpr auto op = detail::DecodeFields(Word);
    auto operands = op;
    if constexpr (dynamic_imm12) operands = BakedOperands<Word, true>(SlotOperands<op.kind>(*member));
    uint64_t target = 0;
    if constexpr (HasBlockTarget(op.kind)) target = WideOperands(*member).primary;
    if constexpr (IsBakedTransfer(op.kind)) {
        if (!detail::ProbeLoadStore<op.kind, op.width, op.operation, op.mode, op.shift, detail::Field(Word, 26, 1)>(
                state, operands, target, data_tlb, tlb_tags, tlb_addend A64_THREAD_PIN_PASS))
            return {member, {Stop::Replay}};
    } else {
        uint64_t pc = 0;
        if constexpr ((op.kind == Kind::BranchImmediate || op.kind == Kind::BranchRegister) && op.operation != 0)
            pc = WideOperands(*member).secondary;
        const auto step = detail::HotInteger<op.kind, op.width, op.operation, op.shift, !(dead & 1)>(
            state, operands, pc, target, nzcv A64_THREAD_PIN_PASS);
        if constexpr (op.kind >= Kind::BranchImmediate && op.kind <= Kind::TestBranch) return {member, step};
    }
    if constexpr (sizeof...(Rest) == 0) {
        return {member, {}};
    } else {
        static_assert(!dynamic_imm12 && CanFollowInBakedSequence(Word));
        return ExecuteBakedThread<(dead >> 1), false, Rest...>(
            state, member->Next<HasWideOperands(op.kind)>(), data_tlb, tlb_tags, tlb_addend, nzcv A64_THREAD_PIN_PASS);
    }
}

template <uint32_t dead, bool dynamic_imm12, uint32_t... Words>
FIBERCPU_AARCH64_THREAD_ABI uint64_t BakedThreadRun(A64_THREAD_PARAMS) {
    const auto result = ExecuteBakedThread<dead, dynamic_imm12, Words...>(state, current, data_tlb, tlb_tags,
                                                                          tlb_addend, nzcv A64_THREAD_PIN_PASS);
    if constexpr ((IsBakedTransfer(detail::DecodeFields(Words).kind) || ...)) {
        if (result.step.reason == Stop::Replay) [[unlikely]] {
            A64_THREAD_REPLAY(result.member);
        }
    }
    constexpr auto terminal = std::array{detail::DecodeFields(Words).kind...}.back();
    if constexpr (terminal >= Kind::BranchImmediate && terminal <= Kind::TestBranch) {
        if (result.step.reason == Stop::Branch) {
            current = result.member;
            if constexpr (terminal == Kind::BranchRegister) {
                A64_THREAD_LINK(current->Next<true>(), result.step.address == WideOperands(*current).secondary + 4,
                                result.step.address);
            } else {
                A64_THREAD_DIRECT();
            }
        }
    }
    A64_THREAD_NEXT(result.member->template Next<HasWideOperands(terminal)>());
}

}  // namespace detail
#endif

template <uint32_t Word, bool dynamic_imm12 = false>
constexpr detail::ThreadHandler BakedThreadHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    if constexpr (CanBake(Word)) return &detail::BakedThreadRun<0, dynamic_imm12, Word>;
#endif
    return nullptr;
}

template <uint32_t dead, uint32_t Word, uint32_t... Rest>
A64_BAKED_INLINE StepResult BakedSequenceWithFlags(State& state, const Memory& memory, const DecodedInstruction& slot) {
    const auto result = Baked<Word, !(dead & 1)>(state, memory, slot);
    if constexpr (sizeof...(Rest) == 0)
        return result;
    else {
        static_assert(CanFollowInBakedSequence(Word));
        if (result.reason != Stop::Continue) return result;
        state.pc += 4;
        return BakedSequenceWithFlags<(dead >> 1), Rest...>(state, memory, slot);
    }
}

template <uint32_t... Words>
constexpr Handler BakedSequenceHandler() {
    constexpr std::array words{Words...};
    constexpr bool valid = [](const auto& sequence) {
        for (size_t i = 0; i + 1 < sequence.size(); ++i)
            if (!CanFollowInBakedSequence(sequence[i])) return false;
        return CanBake(sequence.back());
    }(words);
    if constexpr (valid) return &BakedSequenceWithFlags<0, Words...>;
    return nullptr;
}

// The dispatcher validates both slots and the remaining instruction budget before entry.
// The second instruction sees its own PC, including on a restartable memory fault.
template <uint32_t First, uint32_t Second>
A64_BAKED_INLINE StepResult BakedPair(State& state, const Memory& memory, const DecodedInstruction& slot) {
    static_assert(detail::DecodeFields(First).kind <= Kind::Multiply);
    const auto first = Baked<First>(state, memory, slot);
    if (first.reason != Stop::Continue) return first;
    state.pc += 4;
    return Baked<Second>(state, memory, slot);
}

template <uint32_t First, uint32_t Second>
constexpr Handler BakedPairHandler() {
    if constexpr (CanBake(First) && CanBake(Second) && detail::DecodeFields(First).kind <= Kind::Multiply)
        return &BakedPair<First, Second>;
    return nullptr;
}

template <Handler execute, unsigned length>
constexpr Handler BindBakedFusion(detail::ThreadHandler& threaded) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    if constexpr (execute != nullptr) threaded = detail::SafeScalar<execute, length>;
#else
    (void)threaded;
#endif
    return execute;
}

template <uint32_t Word, bool dynamic_imm12 = false>
constexpr detail::HandlerBinding BakedBinding(bool omit_nzcv) {
    constexpr auto op = detail::DecodeFields(Word);
    if (omit_nzcv) {
        if constexpr (detail::WritesIntegerNZCV(op)) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
            return {Baked<Word, false, dynamic_imm12>, &detail::BakedThreadRun<1, dynamic_imm12, Word>};
#else
            return {Baked<Word, false, dynamic_imm12>, nullptr};
#endif
        }
        return {};
    }
    if constexpr (CanBake(Word)) return {Baked<Word, true, dynamic_imm12>, BakedThreadHandler<Word, dynamic_imm12>()};
    return {};
}

template <uint32_t... Words>
constexpr uint32_t DeadSequenceFlags(uint32_t live) {
    constexpr std::array ops{detail::DecodeFields(Words)...};
    uint32_t dead = 0;
    for (unsigned i = ops.size(); i-- > 0;)
        if (detail::OmitNZCV(ops[i], live)) dead |= uint32_t{1} << i;
    return dead;
}

template <uint32_t dead, uint32_t... Words>
constexpr Handler BindBakedSequence(detail::ThreadHandler& threaded) {
    constexpr auto scalar = BakedSequenceWithFlags<dead, Words...>;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    threaded = &detail::BakedThreadRun<dead, false, Words...>;
#else
    (void)threaded;
#endif
    return scalar;
}

template <uint32_t... Words>
Handler BindBakedRun(uint32_t block_dead, detail::ThreadHandler& threaded) {
    constexpr unsigned length = sizeof...(Words);
    static_assert(length >= 2 && length <= 24);
    if constexpr (BakedSequenceHandler<Words...>() != nullptr) {
        // Whitelisted writers replace all four bits. The final producer is
        // the only one whose eligibility can depend on the rest of the block.
        constexpr auto live_out = DeadSequenceFlags<Words...>(detail::FlagMask);
        constexpr auto dead_out = DeadSequenceFlags<Words...>(0);
        const uint32_t dead = block_dead & ((1u << length) - 1);
        if constexpr (live_out != 0) {
            if (dead == live_out) return BindBakedSequence<live_out, Words...>(threaded);
        }
        if constexpr (dead_out != 0 && dead_out != live_out) {
            if (dead == dead_out) return BindBakedSequence<dead_out, Words...>(threaded);
        }
        return BindBakedSequence<0, Words...>(threaded);
    }
    return nullptr;
}

#undef A64_BAKED_INLINE

}  // namespace fiberish::aarch64
