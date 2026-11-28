#pragma once

#include <utility>

#include "atomic_handlers.h"
#include "block_operands.h"
#include "dispatch.h"
#include "pin_touch.h"
#include "register_handlers.h"
#include "system_handlers.h"

namespace fiberish::aarch64::detail {

#if FIBERCPU_AARCH64_PIN_COUNT > 0 && FIBERCPU_AARCH64_PIN_TOUCH_SPLIT
struct GenericHandlerBinding : HandlerBinding {
    HandlerFunc unpinned = nullptr;

    constexpr GenericHandlerBinding() = default;
    constexpr GenericHandlerBinding(HandlerBinding binding, HandlerFunc unpinned = nullptr)
        : HandlerBinding(binding), unpinned(unpinned) {}
};

inline HandlerBinding SelectPinHandler(GenericHandlerBinding handlers, const DecodedInstruction& op) {
    if (handlers.unpinned && !InstructionTouchesPinnedReg(op)) handlers.handler = handlers.unpinned;
    return handlers;
}
#else
using GenericHandlerBinding = HandlerBinding;

inline HandlerBinding SelectPinHandler(GenericHandlerBinding binding, const DecodedInstruction&) { return binding; }
#endif

template <Stop reason>
A64_FORCE_INLINE StepResult TrapLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                      uint32_t& A64_HOT_PIN_REF) {
    const uint16_t immediate = reason == Stop::Undefined ? 0 : uint16_t(current->Operands());
    return {reason, Access::Execute, immediate, current->GetStartPc(state)};
}

template <Stop reason>
constexpr HandlerBinding BindTrapHandler() {
    constexpr DispatchPolicy policy{.wide = false, .may_exit = true};
    return {&DispatchWrapper<TrapLogic<reason>, policy>};
}

A64_FORCE_INLINE StepResult NopLogic(State&, const DecodedOp*& A64_UTLB_REF_PARAMS, uint32_t& A64_HOT_PIN_REF) {
    return {};
}

template <Kind kind, unsigned width, int operation, int mode, int shift, int vector, bool TouchesPin>
A64_FORCE_INLINE StepResult LoadStoreLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                           uint32_t& A64_HOT_PIN_REF) {
    const auto op = GetOpOperands<kind>(*current);
    if (!ProbeLoadStore<kind, width, operation, mode, shift, vector, TouchesPin>(
            state, op, BlockTarget<kind>(op), state.data_tlb A64_UTLB_ARGS A64_HANDLER_PIN_ARGS)) [[unlikely]]
        return {Stop::Replay};
    return {};
}

template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
constexpr GenericHandlerBinding BindMemoryHandler() {
    constexpr DispatchPolicy policy{.wide = HasExtraOperandSlot(kind), .may_replay = true};
    constexpr HandlerBinding binding{
        &DispatchWrapper<LoadStoreLogic<kind, width, operation, mode, shift, vector, true>, policy>};
    if constexpr (SplitPinTouches)
        return {binding, &DispatchWrapper<LoadStoreLogic<kind, width, operation, mode, shift, vector, false>, policy>};
    else
        return binding;
}

template <Kind kind, unsigned width, int operation, int shift, bool write_nzcv, IntegerForm form, bool TouchesPin>
A64_FORCE_INLINE StepResult IntegerLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                         uint32_t& flags_cache A64_HOT_PIN_REF) {
    const auto op = GetOpOperands<kind>(*current);
    uint64_t pc = 0;
    if constexpr (kind == Kind::BranchImmediate) {
        if (op.operation != 0) pc = current->GetStartPc(state);
    } else if constexpr (kind == Kind::BranchRegister) {
        pc = GetExtraOperands(*current).primary;
    }
    return HotInteger<kind, width, operation, shift, write_nzcv, form, TouchesPin>(state, op, pc, BlockTarget<kind>(op),
                                                                                   flags_cache A64_HANDLER_PIN_ARGS);
}

template <Kind kind, unsigned width = 0, int operation = -1, int shift = -1, bool write_nzcv = true,
          IntegerForm form = IntegerForm::Default>
constexpr GenericHandlerBinding BindIntegerKernel() {
    constexpr DispatchPolicy policy{.wide = HasExtraOperandSlot(kind), .branch = BranchPolicy(kind)};
    constexpr bool touches_pin = kind != Kind::BranchCondition;
    constexpr HandlerBinding binding{
        &DispatchWrapper<IntegerLogic<kind, width, operation, shift, write_nzcv, form, touches_pin>, policy>};
    if constexpr (SplitPinTouches && touches_pin)
        return {binding,
                &DispatchWrapper<IntegerLogic<kind, width, operation, shift, write_nzcv, form, false>, policy>};
    else
        return binding;
}

template <size_t... condition>
constexpr auto ConditionBranchVariants(std::index_sequence<condition...>) {
    return std::array{BindIntegerKernel<Kind::BranchCondition, 0, -1, condition>()...};
}

constexpr auto Handlers = [] {
    std::array<GenericHandlerBinding, size_t(Kind::Count)> handlers;
    handlers.fill(BindTrapHandler<Stop::Undefined>());
#define ARM_INTEGER(Name) handlers[size_t(Kind::Name)] = BindIntegerKernel<Kind::Name>();
#define ARM_BRANCH(Name, Form) ARM_INTEGER(Name)
#define ARM_MEMORY(Name, FallbackWidth) handlers[size_t(Kind::Name)] = BindMemoryHandler<Kind::Name, FallbackWidth>();
#define ARM_EXTRA(Name)
#include "operation_catalog.def"
#undef ARM_EXTRA
#undef ARM_MEMORY
#undef ARM_BRANCH
#undef ARM_INTEGER
    handlers[size_t(Kind::LoadStoreExclusive)] = BindAtomicHandler<Kind::LoadStoreExclusive>();
    handlers[size_t(Kind::LoadStoreAtomic)] = BindAtomicHandler<Kind::LoadStoreAtomic>();
    handlers[size_t(Kind::Syscall)] = BindTrapHandler<Stop::Syscall>();
    handlers[size_t(Kind::Breakpoint)] = BindTrapHandler<Stop::Breakpoint>();
    return handlers;
}();

template <Kind kind, unsigned operations, int shift, bool write_nzcv, IntegerForm form, size_t... index>
constexpr auto IntegerVariants(std::index_sequence<index...>) {
    return std::array<GenericHandlerBinding, sizeof...(index)>{
        BindIntegerKernel < kind, index<operations ? 32 : 64, index % operations, shift, write_nzcv, form>()...};
}

template <Kind kind, unsigned operations = 4, int shift = -1, bool write_nzcv = true,
          IntegerForm form = IntegerForm::Default>
GenericHandlerBinding BindInteger(const DecodedInstruction& op) {
    constexpr auto variants =
        IntegerVariants<kind, operations, shift, write_nzcv, form>(std::make_index_sequence<2 * operations>{});
    return variants[(op.width == 64 ? operations : 0) + op.operation];
}

template <bool write_nzcv>
GenericHandlerBinding BindExtended(const DecodedInstruction& op) {
    if (op.shift == 0) {
        if (op.mode == 0) return BindInteger<Kind::AddSubExtended, 4, 0, write_nzcv, IntegerForm::Uxtb>(op);
        if (op.mode == 2) return BindInteger<Kind::AddSubExtended, 4, 0, write_nzcv, IntegerForm::Uxtw>(op);
    }
    return BindInteger<Kind::AddSubExtended, 4, -1, write_nzcv>(op);
}

template <int mode, unsigned operations, size_t... index>
constexpr auto ImmediateMemoryVariants(std::index_sequence<index...>) {
    return std::array<GenericHandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreImmediate, 8u << (index / operations), index % operations, mode, -1, 0>()...};
}

template <int mode, unsigned operations>
GenericHandlerBinding BindImmediateMemory(const DecodedInstruction& op) {
    constexpr auto variants = ImmediateMemoryVariants<mode, operations>(std::make_index_sequence<4 * operations>{});
    return variants[operations * Field(op.word, 30, 2) + op.operation];
}

template <int extension, unsigned operations, size_t... index>
constexpr auto RegisterMemoryVariants(std::index_sequence<index...>) {
    constexpr auto stride = 2 * operations;
    return std::array<GenericHandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreRegister, 8u << (index / stride), index % operations, extension,
                          (index / operations) % 2 ? index / stride : 0, 0>()...};
}

template <int extension, unsigned operations>
GenericHandlerBinding BindRegisterMemory(const DecodedInstruction& op) {
    constexpr auto variants = RegisterMemoryVariants<extension, operations>(std::make_index_sequence<8 * operations>{});
    return variants[operations * (2 * Field(op.word, 30, 2) + Field(op.word, 12, 1)) + op.operation];
}

template <size_t... index>
constexpr auto PairMemoryVariants(std::index_sequence<index...>) {
    // The non-temporal and signed-offset encodings both use the offset address path.
    constexpr std::array modes{Offset, PostIndex, Offset, PreIndex};
    return std::array<GenericHandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStorePair, 64, index / modes.size(), modes[index % modes.size()], -1, 0>()...};
}

inline GenericHandlerBinding Lower(const DecodedInstruction& op) {
    if (op.word == NopWord) {
        constexpr DispatchPolicy nop_policy{.wide = false};
        return HandlerBinding{&DispatchWrapper<NopLogic, nop_policy>};
    }
    switch (op.kind) {
        case Kind::System:
            if (auto binding = LowerSystem(op.word); binding.handler) return binding;
            break;
        case Kind::ScalarFloat:
            if (auto binding = LowerFloat(op.word); binding.handler) return binding;
            break;
        case Kind::Vector:
            if (auto binding = LowerVector(op.word); binding.handler) return binding;
            break;
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted>(op) : BindInteger<Kind::AddSubShifted, 4, 0>(op);
        case Kind::AddSubExtended:
            return BindExtended<true>(op);
        case Kind::Extract:
            if (IsRotateImmediate(op)) {
                return op.width == 32 ? BindIntegerKernel<Kind::Extract, 32, 0, -1, true, IntegerForm::RotateRight>()
                                      : BindIntegerKernel<Kind::Extract, 64, 0, -1, true, IntegerForm::RotateRight>();
            }
            break;
        case Kind::AddSubCarry:
            return BindInteger<Kind::AddSubCarry>(op);
        case Kind::LogicalImmediate:
            return BindInteger<Kind::LogicalImmediate>(op);
        case Kind::LogicalShifted:
            return op.shift ? BindInteger<Kind::LogicalShifted, 8>(op) : BindInteger<Kind::LogicalShifted, 8, 0>(op);
        case Kind::MoveWide:
            return BindInteger<Kind::MoveWide>(op);
        case Kind::Bitfield:
            return BindInteger<Kind::Bitfield, 3>(op);
        case Kind::ConditionalSelect:
            return BindInteger<Kind::ConditionalSelect>(op);
        case Kind::BranchCondition: {
            // The last two condition encodings are both unconditional.
            constexpr auto variants = ConditionBranchVariants(std::make_index_sequence<14>{});
            if (op.shift < variants.size()) return variants[op.shift];
            break;
        }
        case Kind::CompareBranch:
            return BindInteger<Kind::CompareBranch, 2>(op);
        case Kind::LoadStoreImmediate:
            if (Field(op.word, 26, 1)) break;
            if (op.mode == Offset && op.operation < Prefetch) return BindImmediateMemory<Offset, 4>(op);
            if (op.operation == Load || op.operation == Store) {
                if (op.mode == PostIndex) return BindImmediateMemory<PostIndex, 2>(op);
                if (op.mode == PreIndex) return BindImmediateMemory<PreIndex, 2>(op);
            }
            break;
        case Kind::LoadStorePair:
            if (op.width == 64 && !Field(op.word, 26, 1)) {
                constexpr auto variants = PairMemoryVariants(std::make_index_sequence<8>{});
                return variants[4 * op.operation + op.mode];
            }
            // Both constituents have the same decoded width. A fixed transfer
            // size lets the compiler keep the pair in registers on a TLB hit.
            if (op.width == 64) return BindMemoryHandler<Kind::LoadStorePair, 64>();
            if (op.width == 128) return BindMemoryHandler<Kind::LoadStorePair, 128>();
            break;
        case Kind::LoadStoreRegister:
            if (Field(op.word, 26, 1) || op.operation >= Prefetch) break;
            if (op.mode == 2) return BindRegisterMemory<2, 4>(op);  // UXTW
            if (op.mode == 3) return BindRegisterMemory<3, 4>(op);  // LSL
            if (op.mode == 6 && (op.operation == Load || op.operation == Store))
                return BindRegisterMemory<6, 2>(op);  // SXTW
            break;
        default:
            break;
    }
    return Handlers[size_t(op.kind)];
}

inline GenericHandlerBinding LowerDeadNZCV(const DecodedInstruction& op) {
    if (!WritesIntegerNZCV(op)) return {};
    switch (op.kind) {
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate, 4, -1, false>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted, 4, -1, false>(op)
                            : BindInteger<Kind::AddSubShifted, 4, 0, false>(op);
        case Kind::AddSubExtended:
            return BindExtended<false>(op);
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
