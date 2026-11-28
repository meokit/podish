#pragma once

#include "register_kernels.h"

namespace fiberish::aarch64::detail {

template <FloatFamily family>
A64_FORCE_INLINE StepResult FloatLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                       uint32_t& flags_cache A64_HOT_PIN_REF) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    bool valid;
#if defined(__clang__)
    [[clang::always_inline]]
#endif
    valid = FloatRegisterOp<family>(state, op, flags_cache, HotRegisters{state A64_HANDLER_PIN_ARGS});
    return valid ? StepResult{} : Undefined(current->GetStartPc(state));
}

template <VectorFamily family>
A64_FORCE_INLINE StepResult VectorLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                        uint32_t& A64_HOT_PIN_REF) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (family == VectorFamily::Structure) {
        auto step = VectorStructure<true>(state, nullptr, op, registers, state.data_tlb A64_UTLB_ARGS);
        if (step.reason == Stop::Undefined) [[unlikely]]
            step.address = current->GetStartPc(state);
        return step;
    } else {
        bool valid;
#if defined(__clang__)
        [[clang::always_inline]]
#endif
        valid = VectorRegisterOp<family>(state, op, registers);
        return valid ? StepResult{} : Undefined(current->GetStartPc(state));
    }
}

#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
template <ByteVectorOp op>
A64_FORCE_INLINE StepResult ByteVectorLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                            uint32_t& A64_HOT_PIN_REF) {
    ExecuteByteVector<op>(state, uint32_t(current->Operands()));
    return {};
}
#endif

template <FloatFamily family>
constexpr HandlerBinding BindFloatHandler() {
    constexpr DispatchPolicy policy{.wide = false, .may_exit = true};
    return {&DispatchWrapper<FloatLogic<family>, policy>};
}

template <VectorFamily family>
constexpr HandlerBinding BindVectorHandler([[maybe_unused]] uint32_t word) {
#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
    constexpr DispatchPolicy byte_policy{.wide = false};
    switch (DecodeByteVectorOp(word, family)) {
#define ARM_LowerFloat(Name)
#define ARM_LowerVector(Name)
#define ARM_LowerByteVector(Name) \
    case ByteVectorOp::Name:      \
        return {&DispatchWrapper<ByteVectorLogic<ByteVectorOp::Name>, byte_policy>};
#define ARM_REGISTER(Category, Name) ARM_Lower##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_LowerByteVector
#undef ARM_LowerVector
#undef ARM_LowerFloat
        case ByteVectorOp::None:
            break;
    }
#endif
    constexpr DispatchPolicy policy{
        .wide = false,
        .may_replay = family == VectorFamily::Structure,
        .may_exit = true,
    };
    return {&DispatchWrapper<VectorLogic<family>, policy>};
}

inline HandlerBinding LowerFloat(uint32_t word) {
    switch (DecodeFloatFamily(word)) {
#define ARM_LowerFloat(Name) \
    case FloatFamily::Name:  \
        return BindFloatHandler<FloatFamily::Name>();
#define ARM_LowerVector(Name)
#define ARM_LowerByteVector(Name)
#define ARM_REGISTER(Category, Name) ARM_Lower##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_LowerByteVector
#undef ARM_LowerVector
#undef ARM_LowerFloat
        case FloatFamily::Undefined:
            return {};
    }
}

inline HandlerBinding LowerVector(uint32_t word) {
    switch (DecodeVectorFamily(word)) {
#define ARM_LowerFloat(Name)
#define ARM_LowerVector(Name) \
    case VectorFamily::Name:  \
        return BindVectorHandler<VectorFamily::Name>(word);
#define ARM_LowerByteVector(Name)
#define ARM_REGISTER(Category, Name) ARM_Lower##Category(Name)
#include "operation_registers.def"
#undef ARM_REGISTER
#undef ARM_LowerByteVector
#undef ARM_LowerVector
#undef ARM_LowerFloat
        case VectorFamily::Undefined:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
