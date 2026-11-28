#pragma once

#include "compare_branch.h"
#include "flag_liveness.h"
#include "memory.h"

namespace fiberish::aarch64 {

constexpr bool CanBake(uint32_t word) {
    const auto kind = detail::DecodeFields(word).kind;
    return (kind >= Kind::Address && kind <= Kind::TestBranch) ||
           (kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral);
}

constexpr bool IsBakedTransfer(Kind kind) { return kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral; }

constexpr bool CanFollowInBakedSequence(uint32_t word) {
    const auto op = detail::DecodeFields(word);
    return (op.kind >= Kind::Address && op.kind <= Kind::Multiply) ||
           (op.kind >= Kind::LoadStoreImmediate && op.kind <= Kind::LoadLiteral);
}

template <uint32_t... Words>
constexpr bool CanBakeSequence() {
    constexpr std::array words{Words...};
    for (size_t i = 0; i + 1 < words.size(); ++i)
        if (!CanFollowInBakedSequence(words[i])) return false;
    return CanBake(words.back());
}

// Plan keys clear only scalar immediate fields that the runtime operand slots carry.
// PC-relative targets are canonicalized separately by CanonicalizeBakedWord.
constexpr uint32_t PartialImmediateMask(const DecodedInstruction& op) {
    switch (op.kind) {
        case Kind::AddSubImmediate:
            return 0xfffu << 10;
        case Kind::LoadStoreImmediate:
            if (detail::Field(op.word, 24, 1)) return 0xfffu << 10;
            return detail::Field(op.word, 10, 1) ? 0x1ffu << 12 : 0;
        case Kind::LoadStorePair:
            return 0x7fu << 15;
        case Kind::MoveWide:
            return 0xffffu << 5;
        case Kind::LogicalImmediate:
            return 0x1fffu << 10;
        default:
            return 0;
    }
}

namespace detail {

template <uint32_t Word, bool dynamic_immediate>
A64_FORCE_INLINE decltype(auto) BakedOperands(const DecodedOp& slot) {
    static_assert(CanBake(Word));
    static constexpr auto shape = DecodeFields(Word);
    static_assert(!dynamic_immediate || PartialImmediateMask(shape));
    if constexpr (dynamic_immediate) {
        auto operands = shape;
        operands.immediate = GetOpOperands<shape.kind>(slot).immediate;
        return operands;
    } else {
        return (shape);
    }
}

template <uint32_t dead, uint32_t dynamic_mask, uint32_t Word, uint32_t... Rest>
A64_FORCE_INLINE StepResult ExecuteBakedOps(State& state, const DecodedOp*& member A64_UTLB_REF_PARAMS,
                                            uint32_t& flags_cache A64_HOT_PIN_REF) {
    constexpr auto op = DecodeFields(Word);
    if constexpr (HasNativeCompareBranch && sizeof...(Rest) == 1 && IsUnshiftedCompare(op) && !(dead & 1)) {
        constexpr auto branch = DecodeFields(std::array{Rest...}.front());
        if constexpr (branch.kind == Kind::BranchCondition && branch.shift < ConditionalBranchCount) {
            const bool taken = CompareAndTest<op.kind, op.width, branch.shift>(
                state, BakedOperands<Word, (dynamic_mask & 1) != 0>(*member), flags_cache A64_HANDLER_PIN_ARGS);
            member = member->NextOp<false>();
            return taken ? Branch(GetExtraOperands(*member).primary) : StepResult{};
        }
    }
    const auto& operands = BakedOperands<Word, (dynamic_mask & 1) != 0>(*member);
    uint64_t target = 0;
    if constexpr (op.kind == Kind::Address)
        target = GetAddressTarget<op.operation>(*member);
    else if constexpr (HasBlockTarget(op.kind))
        target = GetExtraOperands(*member).primary;
    if constexpr (IsBakedTransfer(op.kind)) {
        if (!ProbeLoadStore<op.kind, op.width, op.operation, op.mode, op.shift, Field(Word, 26, 1)>(
                state, operands, target, state.data_tlb A64_UTLB_ARGS A64_HANDLER_PIN_ARGS)) [[unlikely]]
            return {Stop::Replay};
    } else {
        uint64_t pc = 0;
        if constexpr (op.kind == Kind::BranchImmediate && op.operation != 0) pc = member->GetStartPc(state);
        if constexpr (op.kind == Kind::BranchRegister && op.operation != 0) pc = GetExtraOperands(*member).primary;
        constexpr auto form = IsRotateImmediate(op) ? IntegerForm::RotateRight : IntegerForm::Default;
        const auto step = HotInteger<op.kind, op.width, op.operation, op.shift, !(dead & 1), form>(
            state, operands, pc, target, flags_cache A64_HANDLER_PIN_ARGS);
        if constexpr (op.kind >= Kind::BranchImmediate && op.kind <= Kind::TestBranch) return step;
    }
    if constexpr (sizeof...(Rest) == 0) {
        return {};
    } else {
        static_assert(CanFollowInBakedSequence(Word));
        member = member->NextOp<HasExtraOperandSlot(op)>();
        return ExecuteBakedOps<(dead >> 1), (dynamic_mask >> 1), Rest...>(state, member A64_UTLB_ARGS,
                                                                          flags_cache A64_HANDLER_PIN_ARGS);
    }
}

template <uint32_t dead, uint32_t dynamic_mask, uint32_t... Words>
A64_FORCE_INLINE StepResult BakedLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                       uint32_t& flags_cache A64_HOT_PIN_REF) {
    return ExecuteBakedOps<dead, dynamic_mask, Words...>(state, current A64_UTLB_ARGS,
                                                         flags_cache A64_HANDLER_PIN_ARGS);
}

template <uint32_t... Words>
constexpr DispatchPolicy BakedPolicy() {
    constexpr auto terminal = std::array{DecodeFields(Words)...}.back();
    return {
        .wide = HasExtraOperandSlot(terminal),
        .may_replay = (IsBakedTransfer(DecodeFields(Words).kind) || ...),
        .may_exit = false,
        .branch = BranchPolicy(terminal.kind),
    };
}

}  // namespace detail

template <uint32_t Word, bool dynamic_immediate = false>
constexpr detail::HandlerFunc GetBakedHandler() {
    if constexpr (CanBake(Word))
        return &detail::DispatchWrapper<detail::BakedLogic<0, dynamic_immediate, Word>, detail::BakedPolicy<Word>()>;
    return nullptr;
}

template <uint32_t Word, bool dynamic_immediate = false>
constexpr detail::HandlerFunc BakedBinding(bool omit_nzcv) {
    constexpr auto op = detail::DecodeFields(Word);
    if (omit_nzcv) {
        if constexpr (detail::WritesIntegerNZCV(op))
            return &detail::DispatchWrapper<detail::BakedLogic<1, dynamic_immediate, Word>,
                                            detail::BakedPolicy<Word>()>;
        return nullptr;
    }
    return GetBakedHandler<Word, dynamic_immediate>();
}

template <uint32_t... Words>
constexpr uint32_t DeadSequenceFlags(uint32_t live) {
    constexpr std::array ops{detail::DecodeFields(Words)...};
    uint32_t dead = 0;
    for (unsigned i = ops.size(); i-- > 0;)
        if (detail::OmitNZCV(ops[i], live)) dead |= uint32_t{1} << i;
    return dead;
}

struct BakedFusionBinding {
    detail::HandlerFunc normal = nullptr;
    detail::HandlerFunc omit_live_out = nullptr;
    detail::HandlerFunc omit_dead_out = nullptr;
    uint32_t live_out = 0;
    uint32_t dead_out = 0;

    constexpr detail::HandlerFunc Select(uint32_t block_dead, unsigned length) const {
        const uint32_t dead = block_dead & ((1u << length) - 1);
        if (dead == live_out && omit_live_out) return omit_live_out;
        if (dead == dead_out && omit_dead_out) return omit_dead_out;
        return normal;
    }
};

template <uint32_t dynamic_mask, uint32_t... Words>
constexpr BakedFusionBinding GetBakedFusion() {
    constexpr unsigned length = sizeof...(Words);
    static_assert(length >= 2 && length <= 24);
    static_assert((dynamic_mask >> length) == 0);
    BakedFusionBinding binding{};
    if constexpr (CanBakeSequence<Words...>()) {
        // Whitelisted writers replace all four bits. The final producer is
        // the only one whose eligibility can depend on the rest of the block.
        constexpr auto live_out = DeadSequenceFlags<Words...>(detail::FlagMask);
        constexpr auto dead_out = DeadSequenceFlags<Words...>(0);
        binding.normal =
            &detail::DispatchWrapper<detail::BakedLogic<0, dynamic_mask, Words...>, detail::BakedPolicy<Words...>()>;
        if constexpr (live_out != 0) {
            binding.live_out = live_out;
            binding.omit_live_out = &detail::DispatchWrapper<detail::BakedLogic<live_out, dynamic_mask, Words...>,
                                                             detail::BakedPolicy<Words...>()>;
        }
        if constexpr (dead_out != 0 && dead_out != live_out) {
            binding.dead_out = dead_out;
            binding.omit_dead_out = &detail::DispatchWrapper<detail::BakedLogic<dead_out, dynamic_mask, Words...>,
                                                             detail::BakedPolicy<Words...>()>;
        }
    }
    return binding;
}

template <uint32_t... Words>
inline constexpr auto BakedFusion = GetBakedFusion<0, Words...>();

template <uint32_t dynamic_mask, uint32_t... Words>
inline constexpr auto PartialBakedFusion = GetBakedFusion<dynamic_mask, Words...>();

template <uint32_t... Words>
detail::HandlerFunc BindBakedFusion(uint32_t block_dead) {
    return BakedFusion<Words...>.Select(block_dead, sizeof...(Words));
}

}  // namespace fiberish::aarch64
