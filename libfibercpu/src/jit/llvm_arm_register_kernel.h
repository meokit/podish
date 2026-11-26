#pragma once

#include "aarch64/register_kernels.h"

namespace fiberish::aarch64::detail {

#define ARM_KERNELFloat(Name)                                                                                    \
    ARM_OPERATION_KERNEL(Float##Name) {                                                                          \
        bool valid;                                                                                              \
        [[clang::always_inline]] valid =                                                                         \
            FloatRegisterOp<FloatFamily::Name, true>(state, op, nzcv, HotRegisters{state A64_HANDLER_PIN_ARGS}); \
        return valid ? OperationFlow::Continue : OperationFlow::Replay;                                          \
    }

template <VectorFamily family>
[[gnu::always_inline]] inline OperationFlow ExecuteVectorOperation(State& state, const DecodedInstruction& op,
                                                                   const mem::SoftTlb* tlb, uint64_t& tags,
                                                                   uintptr_t& addend A64_HOT_PIN_REF) {
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (family == VectorFamily::Structure) {
        StepResult step;
        [[clang::always_inline]] step = VectorStructure<true>(state, nullptr, op, registers, tlb, tags, addend);
        return step.reason == Stop::Continue ? OperationFlow::Continue : OperationFlow::Replay;
    } else {
        bool valid;
        [[clang::always_inline]] valid = VectorRegisterOp<family, true>(state, op, registers);
        return valid ? OperationFlow::Continue : OperationFlow::Replay;
    }
}

#define ARM_KERNELVector(Name)                                                                                \
    ARM_OPERATION_KERNEL(Vector##Name) {                                                                      \
        return ExecuteVectorOperation<VectorFamily::Name>(state, op, tlb, tags, addend A64_HANDLER_PIN_ARGS); \
    }
#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
#define ARM_KERNELByteVector(Name)                             \
    ARM_OPERATION_KERNEL(ByteVector##Name) {                   \
        ExecuteByteVector<ByteVectorOp::Name>(state, op.word); \
        return OperationFlow::Continue;                        \
    }
#else
#define ARM_KERNELByteVector(Name)
#endif
#define ARM_REGISTER(Category, Name) ARM_KERNEL##Category(Name)
#include "aarch64/operation_registers.def"
#undef ARM_REGISTER
#undef ARM_KERNELByteVector
#undef ARM_KERNELVector
#undef ARM_KERNELFloat

}  // namespace fiberish::aarch64::detail
