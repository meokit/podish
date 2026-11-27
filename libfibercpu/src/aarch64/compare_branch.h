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

#if defined(__aarch64__) && defined(__clang__) && !defined(_MSC_VER)
#if __has_extension(gnu_asm_goto_with_outputs_full) && \
    (!defined(FIBERCPU_AARCH64_NATIVE_COMPARE_BRANCH) || FIBERCPU_AARCH64_NATIVE_COMPARE_BRANCH)
#define A64_USE_NATIVE_COMPARE_BRANCH 1
#endif
#endif

#if defined(A64_USE_NATIVE_COMPARE_BRANCH)
inline constexpr bool HasNativeCompareBranch = true;

// The two outputs are independent: the branch consumes host PSTATE and both
// guest successors still receive the complete NZCV carried in a GPR.
#define A64_COMPARE_BRANCH_ASM(cc, reg)              \
    asm goto("cmp %" reg "[left], %" reg             \
             "[right]\n\t"                           \
             "mrs %x[flags], nzcv\n\t"               \
             "b." cc " %l[taken]"                    \
             : [flags] "=r"(flags)                   \
             : [left] "r"(left), [right] "rI"(right) \
             : "cc"                                  \
             : taken)

#define A64_COMPARE_BRANCH_CASE(index, cc)              \
    if constexpr (condition == index) {                 \
        if constexpr (sizeof(Word) == sizeof(uint32_t)) \
            A64_COMPARE_BRANCH_ASM(cc, "w");            \
        else                                            \
            A64_COMPARE_BRANCH_ASM(cc, "x");            \
    }

template <unsigned condition, class Word>
A64_COMPARE_BRANCH_INLINE bool NativeCompareBranch(uint32_t& flags, Word left, Word right) {
    static_assert(condition < ConditionalBranchCount);
    A64_COMPARE_BRANCH_CASE(0, "eq")
    else A64_COMPARE_BRANCH_CASE(1, "ne") else A64_COMPARE_BRANCH_CASE(2, "hs") else A64_COMPARE_BRANCH_CASE(
        3,
        "lo") else A64_COMPARE_BRANCH_CASE(4,
                                           "mi") else A64_COMPARE_BRANCH_CASE(5,
                                                                              "pl") else A64_COMPARE_BRANCH_CASE(6,
                                                                                                                 "v"
                                                                                                                 "s") else A64_COMPARE_BRANCH_CASE(7,
                                                                                                                                                   "vc") else A64_COMPARE_BRANCH_CASE(8,
                                                                                                                                                                                      "hi") else A64_COMPARE_BRANCH_CASE(9,
                                                                                                                                                                                                                         "ls") else A64_COMPARE_BRANCH_CASE(10,
                                                                                                                                                                                                                                                            "ge") else A64_COMPARE_BRANCH_CASE(11,
                                                                                                                                                                                                                                                                                               "lt") else A64_COMPARE_BRANCH_CASE(12,
                                                                                                                                                                                                                                                                                                                                  "gt") else A64_COMPARE_BRANCH_CASE(13,
                                                                                                                                                                                                                                                                                                                                                                     "le") return false;
taken:
    return true;
}
#undef A64_COMPARE_BRANCH_CASE
#undef A64_COMPARE_BRANCH_ASM
#else
inline constexpr bool HasNativeCompareBranch = false;
#endif

template <Kind kind, unsigned width, unsigned condition>
A64_COMPARE_BRANCH_INLINE bool CompareAndTest(State& state, const DecodedInstruction& compare,
                                              uint32_t& flags_cache A64_HOT_PIN_REF) {
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    const auto [left, right] = ReadCompareOperands<kind, width>(registers, compare);
#if defined(A64_USE_NATIVE_COMPARE_BRANCH)
    return NativeCompareBranch<condition>(flags_cache, left, right);
#else
    AddSub<false>(flags_cache, left, right, width, true, true);
    return ConditionHolds(flags_cache, condition);
#endif
}

template <Kind kind, unsigned width, unsigned condition>
FIBERCPU_AARCH64_HANDLER_ABI int64_t CompareBranchHandler(A64_HANDLER_PARAMS) {
    const auto compare = GetOpOperands<kind>(*current);
    const bool taken = CompareAndTest<kind, width, condition>(state, compare, flags_cache A64_HANDLER_PIN_ARGS);
    current = current->NextOp<false>();
    if (taken) A64_RESOLVE_DIRECT_BRANCH_TARGET(Kind::BranchCondition);
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

#undef A64_USE_NATIVE_COMPARE_BRANCH
#undef A64_COMPARE_BRANCH_INLINE
#endif

}  // namespace fiberish::aarch64::detail
