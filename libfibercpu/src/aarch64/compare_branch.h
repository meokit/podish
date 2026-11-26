#pragma once

#include <array>
#include <type_traits>
#include <utility>

#include "block_operands.h"
#include "dispatch.h"

namespace fiberish::aarch64::detail {

constexpr bool IsUnshiftedCompare(const DecodedInstruction& op) {
    return op.operation == 3 && op.rd == 31 &&
           (op.kind == Kind::AddSubImmediate || (op.kind == Kind::AddSubShifted && op.shift == 0));
}

constexpr unsigned ConditionalBranchCount = 14;

#if FIBERCPU_AARCH64_TAIL_DISPATCH

#if defined(_MSC_VER)
#define A64_COMPARE_BRANCH_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_COMPARE_BRANCH_INLINE inline __attribute__((always_inline))
#else
#define A64_COMPARE_BRANCH_INLINE inline
#endif

template <Kind kind, unsigned width>
A64_COMPARE_BRANCH_INLINE auto ReadCompareOperands(const HotRegisters& registers, const DecodedInstruction& op) {
    using Word = std::conditional_t<width == 32, uint32_t, uint64_t>;
    const auto left = registers.Read(op.rn, width, kind == Kind::AddSubImmediate);
    const auto right = kind == Kind::AddSubImmediate ? op.immediate : registers.Read(op.rm, width);
    return std::pair{Word(left), Word(right)};
}

template <Kind kind, unsigned width, unsigned condition>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t CompareBranchHandler(A64_HANDLER_PARAMS) {
    const auto compare = GetOpOperands<kind>(*current);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    const auto [left, right] = ReadCompareOperands<kind, width>(registers, compare);
    AddSub<false>(flags_cache, left, right, width, true, true);

    current = current->NextOp<false>();
    if (ConditionHolds(flags_cache, condition)) A64_RESOLVE_DIRECT_BRANCH_TARGET();
    A64_DISPATCH_NEXT(current->NextOp<true>());
}

template <Kind kind, size_t... index>
constexpr auto CompareBranchVariants(std::index_sequence<index...>) {
    return std::array<HandlerFunc, sizeof...(index)>{
        &CompareBranchHandler<kind, (index < ConditionalBranchCount ? 32 : 64), index % ConditionalBranchCount>...};
}

inline HandlerFunc LowerCompareBranch(const DecodedInstruction& first, const DecodedInstruction& second) {
    if (second.kind != Kind::BranchCondition || second.shift >= ConditionalBranchCount || !IsUnshiftedCompare(first))
        return nullptr;
    constexpr auto indices = std::make_index_sequence<2 * ConditionalBranchCount>{};
    constexpr auto immediate = CompareBranchVariants<Kind::AddSubImmediate>(indices);
    constexpr auto reg = CompareBranchVariants<Kind::AddSubShifted>(indices);
    const auto index = (first.width == 64 ? ConditionalBranchCount : 0) + second.shift;
    return (first.kind == Kind::AddSubImmediate ? immediate : reg)[index];
}

#undef A64_COMPARE_BRANCH_INLINE
#endif

}  // namespace fiberish::aarch64::detail
