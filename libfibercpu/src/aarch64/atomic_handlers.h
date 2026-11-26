#pragma once

#include "block_operands.h"
#include "memory.h"

namespace fiberish::aarch64::detail {

#if FIBERCPU_AARCH64_TAIL_DISPATCH
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

template <Kind kind>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t AtomicHandler(A64_HANDLER_PARAMS) {
    constexpr bool lse = kind == Kind::LoadStoreAtomic;
    const auto op = GetOpOperands<kind>(*current);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    const auto address = registers.Read(op.rn, 64, true);
    const bool pair = !lse && (op.mode == ExclusivePair || op.mode == CompareSwapPair);
    const bool cas = !lse && (op.mode == CompareSwap || op.mode == CompareSwapPair);
    const bool exclusive = !lse && (op.mode == Exclusive || op.mode == ExclusivePair);
    const bool load = !lse && !cas && op.operation == Load;
    const unsigned size = op.width / 8 * (pair ? 2 : 1);
    if ((op.rn == 31 && (address & 15)) || (address & (size - 1))) [[unlikely]] {
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                               Alignment(address, load ? Access::Read : Access::Write));
    }

    uint8_t* readable = nullptr;
    uint8_t* writable = nullptr;
    const bool unmatched_store =
        exclusive && !load && (state.exclusive_size != size || state.exclusive_address != address);
    if (!unmatched_store) {
        // Probe both permissions before the op can clear the monitor or change registers.
        if ((lse || cas || load || exclusive) &&
            !ProbeDataTlb<false>(state, address, size, data_tlb, utlb_tags, utlb_addend, readable)) [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
        if (!load && !ProbeDataTlb<true>(state, address, size, data_tlb, utlb_tags, utlb_addend, writable))
            [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
    }

    const CachedAtomicTransfers memory{state, readable, writable};
    if constexpr (lse) {
        AtomicMemoryOp(op, address, registers, memory);
    } else {
        auto step = ExclusiveMemoryOp(state, op, address, registers, memory);
        if (step.reason != Stop::Continue) [[unlikely]] {
            if (step.reason == Stop::Undefined) step.address = current->GetStartPc();
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
        }
    }
    A64_DISPATCH_NEXT(current->NextOp<false>());
}
#endif

template <Kind kind>
constexpr HandlerBinding BindAtomicHandler() {
    constexpr auto scalar = kind == Kind::LoadStoreAtomic ? &AtomicMemory : &ExclusiveMemory;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {scalar, &AtomicHandler<kind>};
#else
    return {scalar, nullptr};
#endif
}

}  // namespace fiberish::aarch64::detail
