#pragma once

#include "decoder.h"
#include "dispatch.h"

namespace fiberish {

HandlerFunc FindSuperOpcode(const DecodedOp* ops);
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
HandlerFunc GeneratedFindSuperOpcode(const DecodedOp* ops);
#endif
void ApplySuperOpcodesToBlockOps(DecodedOp* ops, uint32_t op_count);

#define HANDLE_SUPEROPCODE_FLOW(flow, state, flow_op, instr_limit, utlb, branch, flags_cache)                      \
    do {                                                                                                           \
        switch (flow) {                                                                                            \
            case LogicFlow::Continue:                                                                              \
                if (auto* next_op = NextOp(flow_op)) {                                                             \
                    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache); \
                }                                                                                                  \
                __builtin_unreachable();                                                                           \
            case LogicFlow::ContinueSkipOne:                                                                       \
                if (auto* next_op = NextOp(NextOp(flow_op))) {                                                     \
                    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache); \
                }                                                                                                  \
                __builtin_unreachable();                                                                           \
            case LogicFlow::ExitOnCurrentEIP:                                                                      \
                RecordBlockHandlersThrough(state, flow_op);                                                        \
                CommitFlagsCache(state, flags_cache);                                                              \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_start(flow_op);                                   \
                return instr_limit;                                                                                \
            case LogicFlow::ExitOnNextEIP:                                                                         \
                RecordBlockHandlersThrough(state, flow_op);                                                        \
                CommitFlagsCache(state, flags_cache);                                                              \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_end(flow_op);                                     \
                return instr_limit;                                                                                \
            case LogicFlow::RestartMemoryOp:                                                                       \
                RecordBlockHandlersThrough(state, flow_op);                                                        \
                ATTR_MUSTTAIL return MemoryOpRestart(state, flow_op, instr_limit, utlb, branch, flags_cache);      \
            case LogicFlow::RetryMemoryOp:                                                                         \
                RecordBlockHandlersThrough(state, flow_op);                                                        \
                ATTR_MUSTTAIL return MemoryOpRetry(state, flow_op, instr_limit, utlb, branch, flags_cache);        \
            case LogicFlow::ExitToBranch:                                                                          \
                RecordBlockHandlersThrough(state, flow_op);                                                        \
                ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(state, flow_op, instr_limit,  \
                                                                                     utlb, branch, flags_cache);   \
            default:                                                                                               \
                CommitFlagsCache(state, flags_cache);                                                              \
                return instr_limit;                                                                                \
        }                                                                                                          \
    } while (false)

}  // namespace fiberish
