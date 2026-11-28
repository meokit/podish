#pragma once

#include "block_operands.h"
#include "memory.h"

namespace fiberish::aarch64::detail {

struct CachedAtomicTransfers {
    State& state;
    const uint8_t* read;
    uint8_t* write;

    bool Read(uint64_t, void* dest, unsigned size) const {
        std::memcpy(dest, read, size);
        return true;
    }

    bool Write(uint64_t address, const void* src, unsigned size) const {
        std::memcpy(write, src, size);
        ClearOverlappingExclusive(state, address, size);
        return true;
    }
};

// Probe both permissions before changing registers or the exclusive monitor.
template <Kind kind>
A64_FORCE_INLINE StepResult ProbeAtomicOperation(State& state, const DecodedInstruction& op,
                                                 const HotRegisters& registers,
                                                 const mem::SoftTlb* data_tlb A64_UTLB_REF_PARAMS) {
    constexpr bool lse = kind == Kind::LoadStoreAtomic;
    const auto address = registers.Read(op.rn, 64, true);
    const bool pair = !lse && (op.mode == ExclusivePair || op.mode == CompareSwapPair);
    const bool cas = !lse && (op.mode == CompareSwap || op.mode == CompareSwapPair);
    const bool exclusive = !lse && (op.mode == Exclusive || op.mode == ExclusivePair);
    const bool load = !lse && !cas && op.operation == Load;
    const unsigned size = op.width / 8 * (pair ? 2 : 1);
    if ((op.rn == 31 && (address & 15)) || (address & (size - 1))) [[unlikely]]
        return Alignment(address, load ? Access::Read : Access::Write);

    uint8_t* readable = nullptr;
    uint8_t* writable = nullptr;
    const bool unmatched_store =
        exclusive && !load && (state.exclusive_size != size || state.exclusive_address != address);
    if (!unmatched_store) {
        if ((lse || cas || load || exclusive) &&
            !ProbeDataTlb<false>(state, address, size, data_tlb A64_UTLB_ARGS, readable)) [[unlikely]]
            return {Stop::Replay};
        if (!load && !ProbeDataTlb<true>(state, address, size, data_tlb A64_UTLB_ARGS, writable)) [[unlikely]]
            return {Stop::Replay};
    }

    const CachedAtomicTransfers memory{state, readable, writable};
    if constexpr (lse)
        return AtomicMemoryOp(op, address, registers, memory);
    else
        return ExclusiveMemoryOp(state, op, address, registers, memory);
}

template <Kind kind>
A64_FORCE_INLINE StepResult AtomicLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                        uint32_t& A64_HOT_PIN_REF) {
    const auto op = GetOpOperands<kind>(*current);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    auto step = ProbeAtomicOperation<kind>(state, op, registers, state.data_tlb A64_UTLB_ARGS);
    if constexpr (kind == Kind::LoadStoreExclusive) {
        if (step.reason == Stop::Undefined) [[unlikely]]
            step.address = current->GetStartPc(state);
    }
    return step;
}

template <Kind kind>
constexpr HandlerBinding BindAtomicHandler() {
    constexpr DispatchPolicy policy{.wide = false, .may_replay = true, .may_exit = true};
    return {&DispatchWrapper<AtomicLogic<kind>, policy>};
}

}  // namespace fiberish::aarch64::detail
