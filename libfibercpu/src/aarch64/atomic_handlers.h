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

// Leave the slow edge in its caller. The threaded handler must tail-call
// the memory replay, while the inlined JIT operation returns flow to its span.
// Probe both permissions before changing registers or the exclusive monitor.
#define A64_PREPARE_ATOMIC_ACCESS(on_alignment, on_replay)                                                         \
    const auto address = registers.Read(op.rn, 64, true);                                                          \
    const bool pair = !lse && (op.mode == ExclusivePair || op.mode == CompareSwapPair);                            \
    const bool cas = !lse && (op.mode == CompareSwap || op.mode == CompareSwapPair);                               \
    const bool exclusive = !lse && (op.mode == Exclusive || op.mode == ExclusivePair);                             \
    const bool load = !lse && !cas && op.operation == Load;                                                        \
    const unsigned size = op.width / 8 * (pair ? 2 : 1);                                                           \
    if ((op.rn == 31 && (address & 15)) || (address & (size - 1))) [[unlikely]] {                                  \
        on_alignment;                                                                                              \
    }                                                                                                              \
                                                                                                                   \
    uint8_t* readable = nullptr;                                                                                   \
    uint8_t* writable = nullptr;                                                                                   \
    const bool unmatched_store =                                                                                   \
        exclusive && !load && (state.exclusive_size != size || state.exclusive_address != address);                \
    if (!unmatched_store) {                                                                                        \
        if ((lse || cas || load || exclusive) &&                                                                   \
            !ProbeDataTlb<false>(state, address, size, data_tlb, utlb_tags, utlb_addend, readable)) [[unlikely]] { \
            on_replay;                                                                                             \
        }                                                                                                          \
        if (!load && !ProbeDataTlb<true>(state, address, size, data_tlb, utlb_tags, utlb_addend, writable))        \
            [[unlikely]] {                                                                                         \
            on_replay;                                                                                             \
        }                                                                                                          \
    }

template <Kind kind>
FIBERCPU_AARCH64_HANDLER_ABI int64_t AtomicHandler(A64_HANDLER_PARAMS) {
    constexpr bool lse = kind == Kind::LoadStoreAtomic;
    const auto op = GetOpOperands<kind>(*current);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    const auto* data_tlb = state.data_tlb;
    A64_PREPARE_ATOMIC_ACCESS(return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                                                     Alignment(address, load ? Access::Read : Access::Write)),
                                     A64_MEMORY_OP_GENERIC(current));

    const CachedAtomicTransfers memory{state, readable, writable};
    if constexpr (lse) {
        AtomicMemoryOp(op, address, registers, memory);
    } else {
        auto step = ExclusiveMemoryOp(state, op, address, registers, memory);
        if (step.reason != Stop::Continue) [[unlikely]] {
            if (step.reason == Stop::Undefined) step.address = current->GetStartPc(state);
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
        }
    }
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

template <Kind kind>
[[gnu::always_inline]] inline StepResult ProbeAtomicOperation(State& state, const DecodedInstruction& op,
                                                              const HotRegisters& registers,
                                                              const mem::SoftTlb* data_tlb, uint64_t& utlb_tags,
                                                              uintptr_t& utlb_addend) {
    constexpr bool lse = kind == Kind::LoadStoreAtomic;
    A64_PREPARE_ATOMIC_ACCESS(return Alignment(address, load ? Access::Read : Access::Write), return {Stop::Replay});

    const CachedAtomicTransfers memory{state, readable, writable};
    if constexpr (lse)
        return AtomicMemoryOp(op, address, registers, memory);
    else
        return ExclusiveMemoryOp(state, op, address, registers, memory);
}

#undef A64_PREPARE_ATOMIC_ACCESS
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
