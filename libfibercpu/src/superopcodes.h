#pragma once

#include "decoder.h"
#include "dispatch.h"

namespace fiberish {

using RegisterHandwrittenCallback = void (*)(void*, HandlerFunc, HandlerFunc, HandlerFunc, HandlerFunc, HandlerFunc,
                                             unsigned);
void RegisterHandwrittenSuperOpcodes(void* context, RegisterHandwrittenCallback callback);
HandlerFunc FindSuperOpcode(const DecodedInstruction* ops);
uint32_t FindFusionTrainingId(HandlerFunc handler);
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
HandlerFunc GeneratedFindSuperOpcode(const DecodedInstruction* ops);
#endif
void ApplySuperOpcodesToBlockOps(DecodedInstruction* ops, uint32_t op_count, bool previously_lowered = false);

#define HANDLE_SUPEROPCODE_FLOW_WITH_POLICY(policy, flow, state, flow_op, instr_limit, utlb, utlb_tags, utlb_addend,   \
                                            branch, flags_cache)                                                       \
    do {                                                                                                               \
        switch (flow) {                                                                                                \
            case LogicFlow::Continue:                                                                                  \
                if (auto* next_op = OperandSpec::Advance<policy>(flow_op)) {                                           \
                    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                              \
                    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch, \
                                                          flags_cache FIBERCPU_PIN_RELOAD);                            \
                }                                                                                                      \
                __builtin_unreachable();                                                                               \
            case LogicFlow::ContinueSkipOne:                                                                           \
                if (auto* next_op = NextOp(OperandSpec::Advance<policy>(flow_op))) {                                   \
                    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                              \
                    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch, \
                                                          flags_cache FIBERCPU_PIN_RELOAD);                            \
                }                                                                                                      \
                __builtin_unreachable();                                                                               \
            case LogicFlow::ExitOnCurrentEIP:                                                                          \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                FIBERCPU_PIN_COMMIT(state);                                                                            \
                CommitFlagsCache(state, flags_cache);                                                                  \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_start(flow_op);                                       \
                return instr_limit;                                                                                    \
            case LogicFlow::ExitOnNextEIP:                                                                             \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                FIBERCPU_PIN_COMMIT(state);                                                                            \
                CommitFlagsCache(state, flags_cache);                                                                  \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_end(flow_op);                                         \
                return instr_limit;                                                                                    \
            case LogicFlow::RestartMemoryOp:                                                                           \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                  \
                ATTR_MUSTTAIL return MemoryOpRestart(state, flow_op, instr_limit, utlb_tags, utlb_addend, branch,      \
                                                     flags_cache FIBERCPU_PIN_RELOAD);                                 \
            case LogicFlow::RetryMemoryOp:                                                                             \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                  \
                ATTR_MUSTTAIL return MemoryOpRetry(state, flow_op, instr_limit, utlb_tags, utlb_addend, branch,        \
                                                   flags_cache FIBERCPU_PIN_RELOAD);                                   \
            case LogicFlow::ExitToBranch:                                                                              \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                  \
                ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(                                  \
                    state, flow_op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache FIBERCPU_PIN_RELOAD);     \
            default:                                                                                                   \
                FIBERCPU_PIN_COMMIT(state);                                                                            \
                CommitFlagsCache(state, flags_cache);                                                                  \
                return instr_limit;                                                                                    \
        }                                                                                                              \
    } while (false)

#define HANDLE_SUPEROPCODE_FLOW(flow, state, flow_op, instr_limit, utlb, utlb_tags, utlb_addend, branch, flags_cache) \
    HANDLE_SUPEROPCODE_FLOW_WITH_POLICY(OperandSpec::Dynamic, flow, state, flow_op, instr_limit, utlb, utlb_tags,     \
                                        utlb_addend, branch, flags_cache)

#define RUN_SUPEROPCODE_OP_WITH_POLICY(policy, target, state, cur_op, instr_limit, utlb, branch, flags_cache) \
    do {                                                                                                      \
        policy::Assume(cur_op);                                                                               \
        auto flow = target(state, cur_op, &utlb, policy::Immediate(cur_op), policy::ModRM(cur_op), &branch,   \
                           flags_cache, FIBERCPU_GPR_CACHE);                                                  \
        if (flow != LogicFlow::Continue) [[unlikely]] {                                                       \
            HANDLE_SUPEROPCODE_FLOW_WITH_POLICY(policy, flow, state, cur_op, instr_limit, utlb, utlb_tags,    \
                                                utlb_addend, branch, flags_cache);                            \
        }                                                                                                     \
    } while (false)

#define RUN_SUPEROPCODE_OP(target, state, cur_op, instr_limit, utlb, branch, flags_cache) \
    RUN_SUPEROPCODE_OP_WITH_POLICY(OperandSpec::Dynamic, target, state, cur_op, instr_limit, utlb, branch, flags_cache)

}  // namespace fiberish
