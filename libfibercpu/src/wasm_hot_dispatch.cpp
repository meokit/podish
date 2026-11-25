#if FIBERCPU_WASM_HOT_DISPATCH
#include "dispatch.h"
#include "ops.h"
#include "superopcodes.h"
#include "wasm_hot_kernels.inc"

namespace fiberish {

ATTR_PRESERVE_NONE int64_t RunWasmHotDispatch(EmuState*, DecodedOp*, int64_t, mem::MicroTlbAbiWord,
                                              mem::MicroTlbAbiWord, uint32_t, uint64_t);

namespace {

struct HotLocals {
    DecodedOp* op;
    int64_t limit;
    mem::MicroTlbAbiWord tags;
    mem::MicroTlbAbiWord addend;
    uint32_t branch;
    uint64_t flags;
};

template <unsigned Slot>
FORCE_INLINE bool TryFollowHotBranch(EmuState* state, HotLocals& locals) {
    state->clear_pending_mem_op();
    if (locals.limit <= 0) return false;
    if (auto next = TryChainBlock<Slot>(state, locals.op, locals.branch, locals.limit); next.op) {
        // The Wasm interpreted block entry is always the first op's handler.
        locals.op = next.op;
        locals.branch = std::numeric_limits<uint32_t>::max();
        return true;
    }
    return false;
}

template <LogicFunc Target, OperandSpec::Executable Policy>
FORCE_INLINE LogicFlow RunHotScalar(EmuState* state, HotLocals& locals) {
    auto* op = locals.op;
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    const auto history = BeginDispatchSequence(state, op);
#endif
    // Some kernels pass these values by address to a slow helper. Give the
    // helper per-instruction temporaries and keep the loop's state private.
    auto tags = locals.tags;
    auto addend = locals.addend;
    auto branch = locals.branch;
    auto flags = locals.flags;
    const auto flow = RunScalarLogic<Target, Policy>(state, op, tags, addend, branch, flags, GprCache{});
    locals.tags = tags;
    locals.addend = addend;
    locals.branch = branch;
    locals.flags = flags;
    if (flow != LogicFlow::Continue) return flow;
    locals.op = OperandSpec::Advance<Policy>(op);
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    RecordDispatchSequence(state, op, locals.op, history);
#endif
    return LogicFlow::Continue;
}

uint32_t HotId(HandlerFunc handler) {
    // Emscripten function pointers contain table slots. Resolve them once
    // while writing the decoded block and carry the case ID on the op.
    static const auto ids = [] {
        std::vector<uint32_t> by_slot;
        for (uint32_t id = 1; id < std::size(kHotHandlers); ++id) {
            const auto slot = reinterpret_cast<uintptr_t>(kHotHandlers[id]);
            if (slot >= by_slot.size()) by_slot.resize(slot + 1);
            by_slot[slot] = id;
        }
        return by_slot;
    }();
    const auto slot = reinterpret_cast<uintptr_t>(handler);
    return slot < ids.size() ? ids[slot] : 0;
}

}  // namespace

void DecodedOp::SetDispatchHandler(HandlerFunc target) {
    hot_dispatch_id = target ? HotId(target) : 0;
    handler = hot_dispatch_id ? RunWasmHotDispatch : target;
}

HandlerFunc DecodedOp::OriginalHandler() const { return hot_dispatch_id ? kHotHandlers[hot_dispatch_id] : handler; }

ATTR_PRESERVE_NONE int64_t RunWasmHotDispatch(EmuState* state, DecodedOp* op, int64_t instr_limit,
                                              mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                              uint32_t branch, uint64_t flags_cache) {
    static_assert(FIBERCPU_PIN_COUNT == 0);
    HotLocals locals{op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache};
#define HOT_TAIL(target)                                                                                     \
    ATTR_MUSTTAIL return (target)(state, locals.op, locals.limit, locals.tags, locals.addend, locals.branch, \
                                  locals.flags)
#define HOT_FUSION_OP(policy, target, state, constituent, instr_limit, utlb, branch, flags_cache, ...)             \
    do {                                                                                                           \
        static_assert(OperandSpec::Executable<policy> && OperandSpec::FixedModRM<policy>);                         \
        policy::Assume(constituent);                                                                               \
        const auto result =                                                                                        \
            target(state, constituent, &utlb, policy::Immediate(constituent), policy::ModRM(constituent), &branch, \
                   flags_cache, OperandGprCache<policy>(GprCache{}) __VA_OPT__(, ) __VA_ARGS__);                   \
        if (result != LogicFlow::Continue) [[unlikely]] {                                                          \
            mem::EncodeMicroTlbAbi(utlb, locals.tags, locals.addend);                                              \
            locals.op = constituent;                                                                               \
            locals.branch = branch;                                                                                \
            locals.flags = flags_cache;                                                                            \
            flow = result;                                                                                         \
            goto finish_instruction;                                                                               \
        }                                                                                                          \
    } while (false)
    for (;;) {
        LogicFlow flow;
        switch (locals.op->hot_dispatch_id) {
#include "wasm_hot_cases.inc"
            default:
                HOT_TAIL(locals.op->handler);
        }

    finish_instruction:
        switch (flow) {
            case LogicFlow::ExitOnCurrentEIP:
                RecordBlockHandlersThrough(state, locals.op);
                CommitFlagsCache(state, locals.flags);
                if (!state->eip_dirty) state->sync_eip_to_op_start(locals.op);
                return locals.limit;
            case LogicFlow::ExitOnNextEIP:
                RecordBlockHandlersThrough(state, locals.op);
                CommitFlagsCache(state, locals.flags);
                if (!state->eip_dirty) state->sync_eip_to_op_end(locals.op);
                return locals.limit;
            case LogicFlow::RestartMemoryOp:
                RecordBlockHandlersThrough(state, locals.op);
                HOT_TAIL(MemoryOpRestart);
            case LogicFlow::RetryMemoryOp:
                RecordBlockHandlersThrough(state, locals.op);
                HOT_TAIL(MemoryOpRetry);
            case LogicFlow::ExitToBranch:
                RecordBlockHandlersThrough(state, locals.op);
                if (TryFollowHotBranch<1>(state, locals)) continue;
                if (locals.limit > 0) HOT_TAIL(ResolveBranchTargetSlowDoubleSlot);
                break;
            case LogicFlow::ExitToBranchSingleSlot:
                RecordBlockHandlersThrough(state, locals.op);
                goto follow_single_link;
            default:
                CommitFlagsCache(state, locals.flags);
                return locals.limit;
        }
        goto finish_branch;

    follow_single_link:
        if (TryFollowHotBranch<0>(state, locals)) continue;
        if (locals.limit > 0) HOT_TAIL(ResolveBranchTargetSlowLink);

    finish_branch:
        state->ctx.eip = locals.branch;
        CommitFlagsCache(state, locals.flags);
        return locals.limit;
    }
#undef HOT_FUSION_OP
#undef HOT_TAIL
}

}  // namespace fiberish
#endif
