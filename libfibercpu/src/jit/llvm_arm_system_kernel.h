#pragma once

#include "aarch64/atomic_handlers.h"
#include "aarch64/system_handlers.h"

namespace fiberish::aarch64::detail {

extern "C" uint64_t FiberArmReadCounter() noexcept;

template <SystemOp operation>
[[gnu::always_inline]] inline OperationFlow RunSystemOperation(State& state, const DecodedInstruction& op,
                                                               const mem::SoftTlb* tlb, uint64_t& tags,
                                                               uintptr_t& addend, uint32_t& nzcv A64_HOT_PIN_REF) {
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (operation == SystemOp::Refetch) {
        return OperationFlow::Refetch;
    } else if constexpr (operation == SystemOp::ZeroCacheLine) {
        return ProbeZeroCacheLine(state, op.rd, registers, tlb, tags, addend) ? OperationFlow::Continue
                                                                              : OperationFlow::Replay;
    } else {
        SystemRegisterOp<operation, HotRegisters, FiberArmReadCounter>(state, op.rd, nzcv, registers);
        return OperationFlow::Continue;
    }
}

#define ARM_SYSTEM(Name)                                                                                    \
    ARM_OPERATION_KERNEL(System##Name) {                                                                    \
        return RunSystemOperation<SystemOp::Name>(state, op, tlb, tags, addend, nzcv A64_HANDLER_PIN_ARGS); \
    }
#include "aarch64/system_operations.def"
#undef ARM_SYSTEM

#define ARM_ATOMIC(Name)                                                                             \
    ARM_OPERATION_KERNEL(Name) {                                                                     \
        const HotRegisters registers{state A64_HANDLER_PIN_ARGS};                                    \
        const auto step = ProbeAtomicOperation<Kind::Name>(state, op, registers, tlb, tags, addend); \
        return step.reason == Stop::Continue ? OperationFlow::Continue : OperationFlow::Replay;      \
    }
ARM_ATOMIC(LoadStoreExclusive)
ARM_ATOMIC(LoadStoreAtomic)
#undef ARM_ATOMIC

}  // namespace fiberish::aarch64::detail
