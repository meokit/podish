#pragma once
#include <array>
#include <utility>

#include "decoder.h"
#include "dispatch.h"

namespace fiberish {

HandlerFunc FindSuperOpcode(const DecodedInstruction* ops);
uint32_t FindFusionTrainingId(HandlerFunc handler);
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
HandlerFunc GeneratedFindSuperOpcode(const DecodedInstruction* ops);
#endif
void ApplySuperOpcodesToBlockOps(DecodedInstruction* ops, uint32_t op_count, bool previously_lowered = false);

template <bool TakenOnZero>
FORCE_INLINE LogicFlow BranchOnTestZero(LogicFuncParams, bool zero) {
    // TEST has already cleared the cached ZF. Both exits publish the actual
    // guest flag while the branch can consume the TEST result directly.
    if (zero) flags_cache |= ZF_MASK;
    if (zero != TakenOnZero) return LogicFlow::Continue;
    *branch = GetControlTargetEip(op);
    return LogicFlow::ExitToBranch;
}

#define DEFINE_FUSION_HANDLERS(name, width, selectable) \
    extern constinit const std::array<HandlerFunc, 1u << (width)> name##_handlers = {DecodeHandlerKey<[] {}>}

#define HANDLE_SUPEROPCODE_FLOW(flow, state, flow_op, instr_limit, utlb, utlb_tags, utlb_addend, branch, flags_cache) \
    do {                                                                                                              \
        switch (flow) {                                                                                               \
            case LogicFlow::ExitOnCurrentEIP:                                                                         \
                RecordBlockHandlersThrough(state, flow_op);                                                           \
                FIBERCPU_PIN_COMMIT(state);                                                                           \
                CommitFlagsCache(state, flags_cache);                                                                 \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_start(flow_op);                                      \
                return instr_limit;                                                                                   \
            case LogicFlow::ExitOnNextEIP:                                                                            \
                RecordBlockHandlersThrough(state, flow_op);                                                           \
                FIBERCPU_PIN_COMMIT(state);                                                                           \
                CommitFlagsCache(state, flags_cache);                                                                 \
                if (!(state)->eip_dirty) (state)->sync_eip_to_op_end(flow_op);                                        \
                return instr_limit;                                                                                   \
            case LogicFlow::RestartMemoryOp:                                                                          \
                RecordBlockHandlersThrough(state, flow_op);                                                           \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                 \
                ATTR_MUSTTAIL return MemoryOpRestart(state, flow_op, instr_limit, utlb_tags, utlb_addend, branch,     \
                                                     flags_cache FIBERCPU_PIN_RELOAD);                                \
            case LogicFlow::RetryMemoryOp:                                                                            \
                RecordBlockHandlersThrough(state, flow_op);                                                           \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                 \
                ATTR_MUSTTAIL return MemoryOpRetry(state, flow_op, instr_limit, utlb_tags, utlb_addend, branch,       \
                                                   flags_cache FIBERCPU_PIN_RELOAD);                                  \
            case LogicFlow::ExitToBranch:                                                                             \
                RecordBlockHandlersThrough(state, flow_op);                                                           \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                 \
                ATTR_MUSTTAIL return ResolveBranchTargetInline<1>(                                 \
                    state, flow_op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache FIBERCPU_PIN_RELOAD);    \
            case LogicFlow::ExitToBranchSingleSlot:                                                                    \
                RecordBlockHandlersThrough(state, flow_op);                                                            \
                mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                    \
                ATTR_MUSTTAIL return ResolveBranchTargetInline<0>(                                                     \
                    state, flow_op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache FIBERCPU_PIN_RELOAD);      \
            default:                                                                                                  \
                FIBERCPU_PIN_COMMIT(state);                                                                           \
                CommitFlagsCache(state, flags_cache);                                                                 \
                return instr_limit;                                                                                   \
        }                                                                                                             \
    } while (false)

#define RUN_SUPEROPCODE_OP_WITH_POLICY(policy, target, state, cur_op, instr_limit, utlb, branch, flags_cache, ...) \
    do {                                                                                                      \
        static_assert(OperandSpec::Executable<policy> && OperandSpec::FixedModRM<policy>);                    \
        policy::Assume(cur_op);                                                                               \
        auto flow = target(state, cur_op, &utlb, policy::Immediate(cur_op), policy::ModRM(cur_op), &branch,   \
                           flags_cache, OperandGprCache<policy>(FIBERCPU_GPR_CACHE) __VA_OPT__(,) __VA_ARGS__); \
        if (flow != LogicFlow::Continue) [[unlikely]] {                                                       \
            HANDLE_SUPEROPCODE_FLOW(flow, state, cur_op, instr_limit, utlb, utlb_tags, utlb_addend, branch,   \
                                    flags_cache);                                                             \
        }                                                                                                     \
    } while (false)

}  // namespace fiberish
