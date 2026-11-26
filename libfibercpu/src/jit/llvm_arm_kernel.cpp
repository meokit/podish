#include "aarch64/dispatch.h"
#include "aarch64/memory.h"
#include "aarch64/operation_binding.h"

#define ARM_OPERATION_KERNEL(Name)                                                                                 \
    extern "C" OperationFlow FiberArmJit##Name(State& state, [[maybe_unused]] const DecodedOp& slot,               \
                                               const DecodedInstruction& op, uint64_t pc, const mem::SoftTlb* tlb, \
                                               uint64_t& tags, uintptr_t& addend, uint32_t& nzcv, bool dead,       \
                                               uint64_t& target A64_HOT_PIN_REF)

namespace fiberish::aarch64::detail {

extern "C" FIBERCPU_AARCH64_HANDLER_ABI uint64_t FiberArmJitRetry(A64_HANDLER_PARAMS);

extern "C" FIBERCPU_AARCH64_HANDLER_ABI uint64_t FiberArmJitABI(A64_HANDLER_PARAMS) {
    ATTR_MUSTTAIL return FiberArmJitRetry(A64_HANDLER_ARGS);
}

extern "C" HandlerFunc FiberArmJitTryLink(State& state, const DecodedOp* sentinel, uint64_t& remaining,
                                          const DecodedOp*& next, uint64_t indirect_pc, BlockExitLink kind) {
    unsigned edge;
    uint64_t target_pc;
    if (kind == BlockExitLink::Direct) {
        const auto* branch = std::launder(reinterpret_cast<const DecodedOpWithDataSlot*>(
            reinterpret_cast<const std::byte*>(sentinel) - sizeof(DecodedOpWithDataSlot)));
        target_pc = branch->primary;
        edge = 0;
    } else if (kind == BlockExitLink::Fallthrough) {
        target_pc = sentinel->Operands();
        edge = 1;
    } else {
        target_pc = indirect_pc;
        edge = indirect_pc == sentinel->Operands();
    }
    const BasicBlock* target;
    if (const auto entry = TryChainBlock(state, target, *sentinel, edge, target_pc, remaining)) [[likely]] {
        A64_COUNT_DIRECT_LINK(sentinel, target);
        next = target->FirstOp();
        return entry;
    }
    state.pc = target_pc;
    state.active_run->exit_edge = edge;
    next = sentinel;
    return nullptr;
}

template <Kind kind>
OperationFlow RunInteger(State& state, const DecodedOp& slot, const DecodedInstruction& op, uint64_t pc, uint32_t& nzcv,
                         bool dead, uint64_t& target A64_HOT_PIN_REF) {
    uint64_t bound_target = 0;
    if constexpr (HasBlockTarget(kind)) bound_target = GetExtraOperands(slot).primary;
    if constexpr (kind == Kind::AddSubImmediate || kind == Kind::AddSubShifted || kind == Kind::AddSubExtended ||
                  kind == Kind::AddSubCarry || kind == Kind::LogicalImmediate || kind == Kind::LogicalShifted ||
                  kind == Kind::ConditionalCompare) {
        if (dead) {
            HotInteger<kind, 0, -1, -1, false>(state, op, pc, bound_target, nzcv A64_HANDLER_PIN_ARGS);
            return OperationFlow::Continue;
        }
    }
    const auto result = HotInteger<kind>(state, op, pc, bound_target, nzcv A64_HANDLER_PIN_ARGS);
    if constexpr (kind >= Kind::BranchImmediate && kind <= Kind::TestBranch) {
        if (result.reason == Stop::Branch) {
            target = result.address;
            return OperationFlow::Branch;
        }
    }
    return OperationFlow::Continue;
}

#define ARM_INTEGER(Name)                                                                            \
    ARM_OPERATION_KERNEL(Name) {                                                                     \
        [[clang::always_inline]]                                                                     \
        return RunInteger<Kind::Name>(state, slot, op, pc, nzcv, dead, target A64_HANDLER_PIN_ARGS); \
    }
#define ARM_BRANCH(Name, Form) ARM_INTEGER(Name)
#define ARM_MEMORY(Name, FallbackWidth)                                                                \
    ARM_OPERATION_KERNEL(Name) {                                                                       \
        const uint64_t address = Kind::Name == Kind::LoadLiteral ? GetExtraOperands(slot).primary : 0; \
        return ProbeLoadStore<Kind::Name>(state, op, address, tlb, tags, addend A64_HANDLER_PIN_ARGS)  \
                   ? OperationFlow::Continue                                                           \
                   : OperationFlow::Replay;                                                            \
    }
#define ARM_EXTRA(Name)
#include "aarch64/operation_catalog.def"
#undef ARM_EXTRA
#undef ARM_MEMORY
#undef ARM_BRANCH
#undef ARM_INTEGER

}  // namespace fiberish::aarch64::detail

#include "jit/llvm_arm_register_kernel.h"
#include "jit/llvm_arm_system_kernel.h"

#undef ARM_OPERATION_KERNEL
