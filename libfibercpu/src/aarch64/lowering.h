#pragma once

#include <utility>

#include "atomic_handlers.h"
#include "block_operands.h"
#include "dispatch.h"
#include "register_handlers.h"
#include "system_handlers.h"

namespace fiberish::aarch64::detail {

template <Stop reason>
inline StepResult TrapInstruction(State& state, const Memory&, const DecodedInstruction& op) {
    return {reason, Access::Execute, reason == Stop::Undefined ? uint16_t{0} : uint16_t(op.immediate), state.pc};
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <Stop reason>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t TrapHandler(A64_HANDLER_PARAMS) {
    const uint16_t immediate = reason == Stop::Undefined ? 0 : uint16_t(current->Operands());
    return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                           {reason, Access::Execute, immediate, current->GetStartPc()});
}
#endif

template <Stop reason>
constexpr HandlerBinding BindTrapHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {&TrapInstruction<reason>, &TrapHandler<reason>};
#else
    return {&TrapInstruction<reason>, nullptr};
#endif
}

inline StepResult NopInstruction(State&, const Memory&, const DecodedInstruction&) { return {}; }

#if FIBERCPU_AARCH64_TAIL_DISPATCH
FIBERCPU_AARCH64_HANDLER_ABI inline uint64_t NopHandler(A64_HANDLER_PARAMS) {
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

template <Kind kind, unsigned width, int operation, int mode, int shift, int vector>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t LoadStoreHandler(A64_HANDLER_PARAMS) {
    const auto op = GetOpOperands<kind>(*current);
    if (!ProbeLoadStore<kind, width, operation, mode, shift, vector>(
            state, op, BlockTarget<kind>(op), data_tlb, utlb_tags, utlb_addend A64_HANDLER_PIN_ARGS)) [[unlikely]] {
        A64_MEMORY_OP_GENERIC(current);
    }
    A64_DISPATCH_NEXT(current->NextOp<HasExtraOperandSlot(kind)>());
}
#endif

template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
constexpr HandlerBinding BindMemoryHandler() {
    constexpr auto scalar = LoadStore<kind, width, operation, mode, shift, vector>;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {scalar, &LoadStoreHandler<kind, width, operation, mode, shift, vector>};
#else
    return {scalar, nullptr};
#endif
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <Kind kind, unsigned width, int operation, int shift, bool write_nzcv>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t IntegerHandler(A64_HANDLER_PARAMS) {
    const auto op = GetOpOperands<kind>(*current);
    uint64_t pc = 0;
    if constexpr (kind == Kind::BranchImmediate || kind == Kind::BranchRegister)
        pc = GetExtraOperands(*current).secondary;
    const auto result = HotInteger<kind, width, operation, shift, write_nzcv>(state, op, pc, BlockTarget<kind>(op),
                                                                              flags_cache A64_HANDLER_PIN_ARGS);
    if constexpr (kind >= Kind::BranchImmediate && kind <= Kind::TestBranch) {
        if (result.reason == Stop::Branch) {
            if constexpr (kind == Kind::BranchRegister) {
                A64_RESOLVE_BRANCH_TARGET_INLINE(current->NextOp<true>(), result.address == pc + 4, result.address);
            } else {
                A64_RESOLVE_DIRECT_BRANCH_TARGET();
            }
        }
    }
    A64_DISPATCH_NEXT(current->NextOp<HasExtraOperandSlot(kind)>());
}
#endif

template <Kind kind, unsigned width = 0, int operation = -1, int shift = -1, bool write_nzcv = true>
constexpr HandlerBinding BindIntegerKernel() {
    constexpr auto scalar = Integer<kind, width, operation, shift, write_nzcv>;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {scalar, &IntegerHandler<kind, width, operation, shift, write_nzcv>};
#else
    return {scalar, nullptr};
#endif
}

constexpr std::array<HandlerBinding, size_t(Kind::Count)> Handlers{
    BindTrapHandler<Stop::Undefined>(),           BindIntegerKernel<Kind::Address>(),
    BindIntegerKernel<Kind::AddSubImmediate>(),   BindIntegerKernel<Kind::AddSubShifted>(),
    BindIntegerKernel<Kind::AddSubExtended>(),    BindIntegerKernel<Kind::AddSubCarry>(),
    BindIntegerKernel<Kind::LogicalImmediate>(),  BindIntegerKernel<Kind::LogicalShifted>(),
    BindIntegerKernel<Kind::MoveWide>(),          BindIntegerKernel<Kind::Bitfield>(),
    BindIntegerKernel<Kind::Extract>(),           BindIntegerKernel<Kind::ConditionalCompare>(),
    BindIntegerKernel<Kind::ConditionalSelect>(), BindIntegerKernel<Kind::DataOneSource>(),
    BindIntegerKernel<Kind::DataTwoSource>(),     BindIntegerKernel<Kind::Multiply>(),
    BindIntegerKernel<Kind::BranchImmediate>(),   BindIntegerKernel<Kind::BranchRegister>(),
    BindIntegerKernel<Kind::BranchCondition>(),   BindIntegerKernel<Kind::CompareBranch>(),
    BindIntegerKernel<Kind::TestBranch>(),        BindMemoryHandler<Kind::LoadStoreImmediate>(),
    BindMemoryHandler<Kind::LoadStoreRegister>(), BindMemoryHandler<Kind::LoadStorePair, 32>(),
    BindMemoryHandler<Kind::LoadLiteral>(),       BindAtomicHandler<Kind::LoadStoreExclusive>(),
    BindAtomicHandler<Kind::LoadStoreAtomic>(),   BindTrapHandler<Stop::Syscall>(),
    BindTrapHandler<Stop::Breakpoint>(),          BindTrapHandler<Stop::Undefined>(),
    BindTrapHandler<Stop::Undefined>(),           BindTrapHandler<Stop::Undefined>(),
};

template <Kind kind, unsigned operations, int shift, bool write_nzcv, size_t... index>
constexpr auto IntegerVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindIntegerKernel < kind, index<operations ? 32 : 64, index % operations, shift, write_nzcv>()...};
}

template <Kind kind, unsigned operations = 4, int shift = -1, bool write_nzcv = true>
HandlerBinding BindInteger(const DecodedInstruction& op) {
    constexpr auto variants =
        IntegerVariants<kind, operations, shift, write_nzcv>(std::make_index_sequence<2 * operations>{});
    return variants[(op.width == 64 ? operations : 0) + op.operation];
}

template <size_t... index>
constexpr auto ImmediateMemoryVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreImmediate, 8u << (index / 4), index % 4, Offset, -1, 0>()...};
}

template <size_t... index>
constexpr auto RegisterMemoryVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreRegister, 8u << (index / 16), index % 4, 2 + (index / 4) % 2,
                          (index / 8) % 2 ? index / 16 : 0, 0>()...};
}

inline HandlerBinding Lower(const DecodedInstruction& op) {
    if (op.word == NopWord) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        return {&NopInstruction, &NopHandler};
#else
        return {&NopInstruction, nullptr};
#endif
    }
    switch (op.kind) {
        case Kind::System:
            if (auto binding = LowerSystem(op.word); binding.scalar_handler) return binding;
            break;
        case Kind::ScalarFloat:
            if (auto binding = LowerFloat(op.word); binding.scalar_handler) return binding;
            break;
        case Kind::Vector:
            if (auto binding = LowerVector(op.word); binding.scalar_handler) return binding;
            break;
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted>(op) : BindInteger<Kind::AddSubShifted, 4, 0>(op);
        case Kind::AddSubExtended:
            return BindInteger<Kind::AddSubExtended>(op);
        case Kind::AddSubCarry:
            return BindInteger<Kind::AddSubCarry>(op);
        case Kind::LogicalImmediate:
            return BindInteger<Kind::LogicalImmediate>(op);
        case Kind::LogicalShifted:
            return op.shift ? BindInteger<Kind::LogicalShifted, 8>(op) : BindInteger<Kind::LogicalShifted, 8, 0>(op);
        case Kind::MoveWide:
            return BindInteger<Kind::MoveWide>(op);
        case Kind::ConditionalSelect:
            return BindInteger<Kind::ConditionalSelect>(op);
        case Kind::CompareBranch:
            return BindInteger<Kind::CompareBranch, 2>(op);
        case Kind::LoadStoreImmediate:
            if (!Field(op.word, 26, 1) && op.operation < Prefetch && op.mode == Offset) {
                constexpr auto variants = ImmediateMemoryVariants(std::make_index_sequence<16>{});
                return variants[4 * Field(op.word, 30, 2) + op.operation];
            }
            break;
        case Kind::LoadStorePair:
            // Both constituents have the same decoded width. A fixed transfer
            // size lets the compiler keep the pair in registers on a TLB hit.
            if (op.width == 64) return BindMemoryHandler<Kind::LoadStorePair, 64>();
            if (op.width == 128) return BindMemoryHandler<Kind::LoadStorePair, 128>();
            break;
        case Kind::LoadStoreRegister:
            if (!Field(op.word, 26, 1) && op.operation < Prefetch && (op.mode == 2 || op.mode == 3)) {
                constexpr auto variants = RegisterMemoryVariants(std::make_index_sequence<64>{});
                return variants[16 * Field(op.word, 30, 2) + 8 * Field(op.word, 12, 1) + 4 * (op.mode - 2) +
                                op.operation];
            }
            break;
        default:
            break;
    }
    return Handlers[size_t(op.kind)];
}

inline HandlerBinding LowerDeadNZCV(const DecodedInstruction& op) {
    if (!WritesIntegerNZCV(op)) return {};
    switch (op.kind) {
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate, 4, -1, false>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted, 4, -1, false>(op)
                            : BindInteger<Kind::AddSubShifted, 4, 0, false>(op);
        case Kind::AddSubExtended:
            return BindInteger<Kind::AddSubExtended, 4, -1, false>(op);
        case Kind::AddSubCarry:
            return BindInteger<Kind::AddSubCarry, 4, -1, false>(op);
        case Kind::LogicalImmediate:
            return BindInteger<Kind::LogicalImmediate, 4, -1, false>(op);
        case Kind::LogicalShifted:
            return op.shift ? BindInteger<Kind::LogicalShifted, 8, -1, false>(op)
                            : BindInteger<Kind::LogicalShifted, 8, 0, false>(op);
        case Kind::ConditionalCompare:
            return BindIntegerKernel<Kind::ConditionalCompare, 0, -1, -1, false>();
        default:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
