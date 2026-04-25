#include "superopcodes.h"

#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_groups_impl.h"

namespace fiberish {

namespace handwritten_superopcodes {

template <LogicFunc Target>
FORCE_INLINE bool MatchesHandler(const DecodedOp* op) {
    return op->handler == (HandlerFunc)DispatchWrapper<Target>;
}

template <LogicFunc Op0, LogicFunc Op1>
FORCE_INLINE bool Match2(const DecodedOp* ops) {
    return MatchesHandler<Op0>(ops) && MatchesHandler<Op1>(NextOp(ops));
}

template <LogicFunc Op0, LogicFunc Op1, LogicFunc Op2>
FORCE_INLINE bool Match3(const DecodedOp* ops) {
    return Match2<Op0, Op1>(ops) && MatchesHandler<Op2>(NextOp(NextOp(ops)));
}

template <LogicFunc Op0, LogicFunc Op1, LogicFunc Op2, LogicFunc Op3>
FORCE_INLINE bool Match4(const DecodedOp* ops) {
    return Match3<Op0, Op1, Op2>(ops) && MatchesHandler<Op3>(NextOp(NextOp(NextOp(ops))));
}

#define DEFINE_HANDWRITTEN_SUPEROPCODE_2(Name, Op0, Op1)                                                   \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, \
                                    mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {           \
        RUN_SUPEROPCODE_OP(Op0, state, op, instr_limit, utlb, branch, flags_cache);                        \
                                                                                                           \
        DecodedOp* second_op = NextOp(op);                                                                 \
        RUN_SUPEROPCODE_OP(Op1, state, second_op, instr_limit, utlb, branch, flags_cache);                 \
                                                                                                           \
        if (auto* next_op = NextOp(second_op)) {                                                           \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache); \
        }                                                                                                  \
        __builtin_unreachable();                                                                           \
    }

#define RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(target, current_op, exit_flow, exit_op) \
    do {                                                                                         \
        auto flow = target(state, current_op, &utlb, GetImm(current_op), &branch, flags_cache);  \
        if (flow != LogicFlow::Continue) [[unlikely]] {                                          \
            exit_flow = flow;                                                                    \
            exit_op = current_op;                                                                \
            goto superopcode_exit;                                                               \
        }                                                                                        \
    } while (false)

#define DEFINE_HANDWRITTEN_SUPEROPCODE_3(Name, Op0, Op1, Op2)                                              \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, \
                                    mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                         \
        DecodedOp* exit_op = op;                                                                           \
        DecodedOp* second_op = NextOp(op);                                                                 \
        DecodedOp* third_op = NextOp(second_op);                                                           \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                     \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);              \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);               \
                                                                                                           \
        if (auto* next_op = NextOp(third_op)) {                                                            \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache); \
        }                                                                                                  \
        __builtin_unreachable();                                                                           \
    superopcode_exit:                                                                                      \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, branch, flags_cache);        \
    }

#define DEFINE_HANDWRITTEN_SUPEROPCODE_4(Name, Op0, Op1, Op2, Op3)                                         \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, \
                                    mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                         \
        DecodedOp* exit_op = op;                                                                           \
        DecodedOp* second_op = NextOp(op);                                                                 \
        DecodedOp* third_op = NextOp(second_op);                                                           \
        DecodedOp* fourth_op = NextOp(third_op);                                                           \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                     \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);              \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);               \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op3, fourth_op, exit_flow, exit_op);              \
                                                                                                           \
        if (auto* next_op = NextOp(fourth_op)) {                                                           \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache); \
        }                                                                                                  \
        __builtin_unreachable();                                                                           \
    superopcode_exit:                                                                                      \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, branch, flags_cache);        \
    }

DEFINE_HANDWRITTEN_SUPEROPCODE_2(HandWrittenSuperOpcode_MovLoadEaxEsp_Ret, op::OpMov_Load_Eax_EspBaseNoIndexNoSegment,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_2(HandWrittenSuperOpcode_MovLoadEbxEsp_Ret, op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_2(HandWrittenSuperOpcode_MovLoadEdxEsp_Ret, op::OpMov_Load_Edx_EspBaseNoIndexNoSegment,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_2(HandWrittenSuperOpcode_MovLoadEsiEsp_Ret, op::OpMov_Load_Esi_EspBaseNoIndexNoSegment,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_2(HandWrittenSuperOpcode_MovLoadEdiEsp_Ret, op::OpMov_Load_Edi_EspBaseNoIndexNoSegment,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_3(HandWrittenSuperOpcode_Group5Push_MovLoadEbxEsp_CallRel, op::OpGroup5_Ev_Push_32_Flags,
                                 op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_3(HandWrittenSuperOpcode_PushEbp_PushEdi_CallRel, op::OpPush_Reg32_Ebp,
                                 op::OpPush_Reg32_Edi, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_3(HandWrittenSuperOpcode_Group1Sub_PushEdi_CallRel, op::OpGroup1_EvIb_Sub_32_Flags,
                                 op::OpPush_Reg32_Edi, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_3(HandWrittenSuperOpcode_PopEdi_PopEsi_Ret, op::OpPop_Reg32_Edi, op::OpPop_Reg32_Esi,
                                 op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_3(HandWrittenSuperOpcode_Group1Sub_Group5Push_CallRel, op::OpGroup1_EvIb_Sub_32_Flags,
                                 op::OpGroup5_Ev_Push_32_Flags, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_Group1Sub_Group5Push_MovLoadEbxEsp_CallRel,
                                 op::OpGroup1_EvIb_Sub_32_Flags, op::OpGroup5_Ev_Push_32_Flags,
                                 op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_PushEdi_PushEsi_PushEbx_CallRel, op::OpPush_Reg32_Edi,
                                 op::OpPush_Reg32_Esi, op::OpPush_Reg32_Ebx, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_PushEbp_PushEdi_PushEsi_CallRel, op::OpPush_Reg32_Ebp,
                                 op::OpPush_Reg32_Edi, op::OpPush_Reg32_Esi, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_PushEsi_PushEbx_Group1Sub_CallRel, op::OpPush_Reg32_Esi,
                                 op::OpPush_Reg32_Ebx, op::OpGroup1_EvIb_Sub_32_Flags, op::OpCall_Rel)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_PopEbx_PopEsi_PopEdi_Ret, op::OpPop_Reg32_Ebx,
                                 op::OpPop_Reg32_Esi, op::OpPop_Reg32_Edi, op::OpRet)

DEFINE_HANDWRITTEN_SUPEROPCODE_4(HandWrittenSuperOpcode_PopEdx_PopEbx_PopEsi_Ret, op::OpPop_Reg32_Edx,
                                 op::OpPop_Reg32_Ebx, op::OpPop_Reg32_Esi, op::OpRet)

#undef RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT
#undef DEFINE_HANDWRITTEN_SUPEROPCODE_2
#undef DEFINE_HANDWRITTEN_SUPEROPCODE_3
#undef DEFINE_HANDWRITTEN_SUPEROPCODE_4

HandlerFunc FindHandWrittenSuperOpcode(const DecodedOp* ops) {
    if (Match2<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_MovLoadEdxEsp_Ret;
    }

    if (Match2<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_MovLoadEbxEsp_Ret;
    }

    if (Match2<op::OpMov_Load_Edi_EspBaseNoIndexNoSegment, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_MovLoadEdiEsp_Ret;
    }

    if (Match2<op::OpMov_Load_Esi_EspBaseNoIndexNoSegment, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_MovLoadEsiEsp_Ret;
    }

    if (Match2<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_MovLoadEaxEsp_Ret;
    }

    if (Match4<op::OpGroup1_EvIb_Sub_32_Flags, op::OpGroup5_Ev_Push_32_Flags,
               op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_Group1Sub_Group5Push_MovLoadEbxEsp_CallRel;
    }

    if (Match4<op::OpPush_Reg32_Edi, op::OpPush_Reg32_Esi, op::OpPush_Reg32_Ebx, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_PushEdi_PushEsi_PushEbx_CallRel;
    }

    if (Match4<op::OpPush_Reg32_Ebp, op::OpPush_Reg32_Edi, op::OpPush_Reg32_Esi, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_PushEbp_PushEdi_PushEsi_CallRel;
    }

    if (Match4<op::OpPush_Reg32_Esi, op::OpPush_Reg32_Ebx, op::OpGroup1_EvIb_Sub_32_Flags, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_PushEsi_PushEbx_Group1Sub_CallRel;
    }

    if (Match4<op::OpPop_Reg32_Ebx, op::OpPop_Reg32_Esi, op::OpPop_Reg32_Edi, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_PopEbx_PopEsi_PopEdi_Ret;
    }

    if (Match4<op::OpPop_Reg32_Edx, op::OpPop_Reg32_Ebx, op::OpPop_Reg32_Esi, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_PopEdx_PopEbx_PopEsi_Ret;
    }

    if (Match3<op::OpGroup1_EvIb_Sub_32_Flags, op::OpGroup5_Ev_Push_32_Flags, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_Group1Sub_Group5Push_CallRel;
    }

    if (Match3<op::OpGroup5_Ev_Push_32_Flags, op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_Group5Push_MovLoadEbxEsp_CallRel;
    }

    if (Match3<op::OpPush_Reg32_Ebp, op::OpPush_Reg32_Edi, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_PushEbp_PushEdi_CallRel;
    }

    if (Match3<op::OpGroup1_EvIb_Sub_32_Flags, op::OpPush_Reg32_Edi, op::OpCall_Rel>(ops)) {
        return HandWrittenSuperOpcode_Group1Sub_PushEdi_CallRel;
    }

    if (Match3<op::OpPop_Reg32_Edi, op::OpPop_Reg32_Esi, op::OpRet>(ops)) {
        return HandWrittenSuperOpcode_PopEdi_PopEsi_Ret;
    }

    return nullptr;
}

}  // namespace handwritten_superopcodes

HandlerFunc FindSuperOpcode(const DecodedOp* ops) {
    if (HandlerFunc handwritten = handwritten_superopcodes::FindHandWrittenSuperOpcode(ops)) {
        return handwritten;
    }

#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
    return GeneratedFindSuperOpcode(ops);
#else
    (void)ops;
    return nullptr;
#endif
}

void ApplySuperOpcodesToBlockOps(DecodedOp* ops, uint32_t op_count) {
#if !FIBERCPU_ENABLE_SUPEROPCODES
    (void)ops;
    (void)op_count;
    return;
#else
    if (!ops || op_count < 2) return;

    for (uint32_t i = 0; i + 1 < op_count; ++i) {
        if (HandlerFunc superopcode = FindSuperOpcode(&ops[i])) {
            ops[i].handler = superopcode;
        }
    }
#endif
}

}  // namespace fiberish
