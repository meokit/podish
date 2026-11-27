#pragma once

#include "register_kernels.h"

namespace fiberish::aarch64::detail {

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <FloatFamily family>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t FloatHandler(A64_HANDLER_PARAMS) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    bool valid;
#if defined(__clang__)
    [[clang::always_inline]]
#endif
    valid = FloatRegisterOp<family>(state, op, flags_cache, HotRegisters{state A64_HANDLER_PIN_ARGS});
    if (!valid) [[unlikely]]
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                               Undefined(current->GetStartPc(state)));
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

template <VectorFamily family>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t VectorHandler(A64_HANDLER_PARAMS) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (family == VectorFamily::Structure) {
        auto step = VectorStructure<true>(state, nullptr, op, registers, state.data_tlb, utlb_tags, utlb_addend);
        if (step.reason == Stop::Replay) [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
        if (step.reason != Stop::Continue) [[unlikely]] {
            if (step.reason == Stop::Undefined) step.address = current->GetStartPc(state);
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
        }
    } else {
        bool valid;
#if defined(__clang__)
        [[clang::always_inline]]
#endif
        valid = VectorRegisterOp<family>(state, op, registers);
        if (!valid) [[unlikely]]
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                                   Undefined(current->GetStartPc(state)));
    }
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
template <ByteVectorOp op>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t ByteVectorHandler(A64_HANDLER_PARAMS) {
    ExecuteByteVector<op>(state, uint32_t(current->Operands()));
    A64_DISPATCH_NEXT(current->NextOp<false>());
}
#endif
#endif

template <FloatFamily family>
constexpr HandlerBinding BindFloatHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {&ScalarFloat<family>, &FloatHandler<family>};
#else
    return {&ScalarFloat<family>, nullptr};
#endif
}

template <VectorFamily family>
constexpr HandlerBinding BindVectorHandler([[maybe_unused]] uint32_t word) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
    switch (DecodeByteVectorOp(word, family)) {
#define ARM_LowerFloat(Name)
#define ARM_LowerVector(Name)
#define ARM_LowerByteVector(Name) \
    case ByteVectorOp::Name:      \
        return {&Vector<family>, &ByteVectorHandler<ByteVectorOp::Name>};
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
    return {&Vector<family>, &VectorHandler<family>};
#else
    return {&Vector<family>, nullptr};
#endif
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
