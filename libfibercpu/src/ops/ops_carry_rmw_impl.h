#pragma once

#include "../dispatch.h"
#include "../exec_utils.h"

namespace fiberish {

enum class CarryRmwKind { Adc, Sbb, Rcl, Rcr };

template <CarryRmwKind Kind, typename T, bool UpdateFlags = true>
FORCE_INLINE T CalculateCarryRmw(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    if constexpr (Kind == CarryRmwKind::Adc)
        return AluAdc<T, UpdateFlags>(state, flags_cache, dest, src);
    else if constexpr (Kind == CarryRmwKind::Sbb)
        return AluSbb<T, UpdateFlags>(state, flags_cache, dest, src);
    else if constexpr (Kind == CarryRmwKind::Rcl)
        return AluRcl<T, UpdateFlags>(state, flags_cache, dest, static_cast<uint8_t>(src));
    else
        return AluRcr<T, UpdateFlags>(state, flags_cache, dest, static_cast<uint8_t>(src));
}

// Only a write-TLB miss needs transactional flags. A rejected store must leave
// the instruction's input flags intact for either an SMC rerun or a real fault.
template <CarryRmwKind Kind, typename T, bool UpdateFlags>
__attribute__((noinline, cold)) LogicFlow CarryRmwSlow(EmuState* state, DecodedOp* op, uint32_t addr, T dest, T src,
                                                       uint64_t input_flags) {
    state->sync_eip_to_op_start(op);
    state->clear_pending_mem_op();
    // Also make flags from earlier instructions visible to fault callbacks.
    SetStateFlagsCache(state, input_flags);
    uint64_t candidate_flags = input_flags;
    const T result = CalculateCarryRmw<Kind, T, UpdateFlags>(state, candidate_flags, dest, src);
    RestartableMemoryFaultScope restartable(state);
    if (!state->mmu.write_slow<T>(addr, result)) return LogicFlow::ExitOnCurrentEIP;
    SetStateFlagsCache(state, candidate_flags);
    return LogicFlow::Continue;
}

template <CarryRmwKind Kind, typename T, bool UpdateFlags>
FORCE_INLINE LogicFlow ExecuteCarryMemRmw(EmuState* state, GprCache& gpr, DecodedOp* op, uint32_t addr, T dest, T src,
                                          mem::MicroTLB* utlb, uint64_t& flags_cache) {
    // Move, don't duplicate, the existing write-TLB check. Once this succeeds,
    // pure ALU work and a direct store cannot trigger a guest fault or callback.
    auto target = state->mmu.try_write_target_tlb<T>(addr, utlb);
    if (!target) [[unlikely]] {
        // Pass by value: taking the hot flags local's address makes clang-cl
        // spill it and insert /GS checks into every invocation of the handler.
        const auto flow = gpr.WithCanonical(
            state, [&] { return CarryRmwSlow<Kind, T, UpdateFlags>(state, op, addr, dest, src, flags_cache); });
        flags_cache = GetStateFlagsCache(state);
        return flow;
    }
    const T result = CalculateCarryRmw<Kind, T, UpdateFlags>(state, flags_cache, dest, src);
    mem::StoreGuestValue(state, *target, result);
    return LogicFlow::Continue;
}

template <CarryRmwKind Kind, typename T, bool UpdateFlags = true, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow ExecuteCarryRmw(EmuState* state, GprCache& gpr, DecodedOp* op,
                                       const ModRMRmwOperand<Form>& operand, T dest, T src, mem::MicroTLB* utlb,
                                       uint64_t& flags_cache) {
    if (operand.IsRegister()) {
        const T result = CalculateCarryRmw<Kind, T, UpdateFlags>(state, flags_cache, dest, src);
        WriteRegGeneric<T>(state, gpr, operand.Reg(), result);
        return LogicFlow::Continue;
    }
    return ExecuteCarryMemRmw<Kind, T, UpdateFlags>(state, gpr, op, operand.Address(), dest, src, utlb, flags_cache);
}

}  // namespace fiberish
