#include "superopcodes.h"
#include "operand_specialization.h"

#include <array>
#include <initializer_list>

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

#define DEFINE_HANDWRITTEN_SUPEROPCODE_2(Name, Op0, Op1)                                                               \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        RUN_SUPEROPCODE_OP(Op0, state, op, instr_limit, utlb, branch, flags_cache);                                    \
                                                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        RUN_SUPEROPCODE_OP(Op1, state, second_op, instr_limit, utlb, branch, flags_cache);                             \
                                                                                                                       \
        if (auto* next_op = NextOp(second_op)) {                                                                       \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
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

#define DEFINE_HANDWRITTEN_SUPEROPCODE_3(Name, Op0, Op1, Op2)                                                          \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                                     \
        DecodedOp* exit_op = op;                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        DecodedOp* third_op = NextOp(second_op);                                                                       \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                                 \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);                          \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);                           \
                                                                                                                       \
        if (auto* next_op = NextOp(third_op)) {                                                                        \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
    superopcode_exit:                                                                                                  \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, utlb_tags, utlb_addend, branch,          \
                                flags_cache);                                                                          \
    }

#define DEFINE_HANDWRITTEN_SUPEROPCODE_4(Name, Op0, Op1, Op2, Op3)                                                     \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                                     \
        DecodedOp* exit_op = op;                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        DecodedOp* third_op = NextOp(second_op);                                                                       \
        DecodedOp* fourth_op = NextOp(third_op);                                                                       \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                                 \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);                          \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);                           \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op3, fourth_op, exit_flow, exit_op);                          \
                                                                                                                       \
        if (auto* next_op = NextOp(fourth_op)) {                                                                       \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
    superopcode_exit:                                                                                                  \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, utlb_tags, utlb_addend, branch,          \
                                flags_cache);                                                                          \
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

#if !FIBERCPU_GENERATED_WITH_HANDWRITTEN || defined(FIBERCPU_VALIDATE_SPECIALIZATION_INDEX)
HandlerFunc FindHandWrittenSuperOpcode(const DecodedOp* ops) {
    // Keep this first-handler set in sync with the handwritten patterns below;
    // false positives are harmless, but omitting a handler prevents a match.
    static const auto filter = [] {
        std::array<uint64_t, 16> bits{};
        const HandlerFunc first[] = {
            (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment>,
            (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
            (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edi_EspBaseNoIndexNoSegment>,
            (HandlerFunc)DispatchWrapper<op::OpMov_Load_Esi_EspBaseNoIndexNoSegment>,
            (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>,
            (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
            (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
            (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>,
            (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>,
            (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>,
            (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>,
            (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edx>,
            (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>,
        };
        for (auto h : first) {
            auto key = reinterpret_cast<uintptr_t>(h) >> 2;
            bits[(key >> 6) & 15] |= uint64_t{1} << (key & 63);
        }
        return bits;
    }();
    const auto key = reinterpret_cast<uintptr_t>(ops->handler) >> 2;
    if (!(filter[(key >> 6) & 15] & (uint64_t{1} << (key & 63)))) return nullptr;
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

#endif

#ifdef FIBERCPU_VALIDATE_SPECIALIZATION_INDEX
bool ValidateHandwrittenSuperOpcodes() {
    auto check = [](HandlerFunc expected, std::initializer_list<HandlerFunc> handlers) {
        DecodedOp ops[4]{};
        size_t i = 0;
        for (auto handler : handlers) ops[i++].handler = handler;
        if (FindHandWrittenSuperOpcode(ops) != expected || FindSuperOpcode(ops) != expected) return false;
        ops[i - 1].handler = nullptr;
        return FindHandWrittenSuperOpcode(ops) != expected;
    };
    if (!check(HandWrittenSuperOpcode_MovLoadEdxEsp_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_MovLoadEbxEsp_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_MovLoadEdiEsp_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpMov_Load_Edi_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_MovLoadEsiEsp_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpMov_Load_Esi_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_MovLoadEaxEsp_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_Group1Sub_Group5Push_MovLoadEbxEsp_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PushEdi_PushEsi_PushEbx_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>,
                (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PushEbp_PushEdi_PushEsi_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>,
                (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PushEsi_PushEbx_Group1Sub_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>,
                (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PopEbx_PopEsi_PopEdi_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>,
                (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PopEdx_PopEbx_PopEsi_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edx>, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>,
                (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    if (!check(HandWrittenSuperOpcode_Group1Sub_Group5Push_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_Group5Push_MovLoadEbxEsp_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
                (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PushEbp_PushEdi_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>,
                (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_Group1Sub_PushEdi_CallRel,
               {(HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
                (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>}))
        return false;
    if (!check(HandWrittenSuperOpcode_PopEdi_PopEsi_Ret,
               {(HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>,
                (HandlerFunc)DispatchWrapper<op::OpRet>}))
        return false;
    return true;
}
#endif

}  // namespace handwritten_superopcodes

// Register in reverse legacy order: the generated table prepends each entry,
// preserving handwritten precedence and order within each prefix bucket.
// Keep this list synchronized with the legacy matcher and its validation.
void RegisterHandwrittenSuperOpcodes(void* context, RegisterHandwrittenCallback callback) {
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>,
             (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_PopEdi_PopEsi_Ret, 3);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
             (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_Group1Sub_PushEdi_CallRel, 3);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>,
             (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_PushEbp_PushEdi_CallRel, 3);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
             (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpCall_Rel>, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_Group5Push_MovLoadEbxEsp_CallRel, 3);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
             (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>,
             nullptr, handwritten_superopcodes::HandWrittenSuperOpcode_Group1Sub_Group5Push_CallRel, 3);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edx>,
             (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>,
             (HandlerFunc)DispatchWrapper<op::OpRet>,
             handwritten_superopcodes::HandWrittenSuperOpcode_PopEdx_PopEbx_PopEsi_Ret, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>,
             (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>,
             (HandlerFunc)DispatchWrapper<op::OpRet>,
             handwritten_superopcodes::HandWrittenSuperOpcode_PopEbx_PopEsi_PopEdi_Ret, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>,
             (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>,
             (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>, (HandlerFunc)DispatchWrapper<op::OpCall_Rel>,
             handwritten_superopcodes::HandWrittenSuperOpcode_PushEsi_PushEbx_Group1Sub_CallRel, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>,
             (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>,
             (HandlerFunc)DispatchWrapper<op::OpCall_Rel>,
             handwritten_superopcodes::HandWrittenSuperOpcode_PushEbp_PushEdi_PushEsi_CallRel, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>,
             (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>, (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>,
             (HandlerFunc)DispatchWrapper<op::OpCall_Rel>,
             handwritten_superopcodes::HandWrittenSuperOpcode_PushEdi_PushEsi_PushEbx_CallRel, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>,
             (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>,
             (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpCall_Rel>,
             handwritten_superopcodes::HandWrittenSuperOpcode_Group1Sub_Group5Push_MovLoadEbxEsp_CallRel, 4);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_MovLoadEaxEsp_Ret, 2);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpMov_Load_Esi_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_MovLoadEsiEsp_Ret, 2);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edi_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_MovLoadEdiEsp_Ret, 2);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_MovLoadEbxEsp_Ret, 2);
    callback(context, (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment>,
             (HandlerFunc)DispatchWrapper<op::OpRet>, nullptr, nullptr,
             handwritten_superopcodes::HandWrittenSuperOpcode_MovLoadEdxEsp_Ret, 2);
}

HandlerFunc FindSuperOpcode(const DecodedOp* ops) {
#if !FIBERCPU_GENERATED_WITH_HANDWRITTEN
    if (HandlerFunc handwritten = handwritten_superopcodes::FindHandWrittenSuperOpcode(ops)) {
        return handwritten;
    }
#endif

#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
    return GeneratedFindSuperOpcode(ops);
#else
    (void)ops;
    return nullptr;
#endif
}

void ApplySuperOpcodesToBlockOps(DecodedOp* ops, uint32_t op_count, bool previously_lowered) {
#if FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY
    if (!ops) return;
    if (previously_lowered)
        for (uint32_t i = 0; i < op_count; ++i)
            if (ops[i].len) ops[i].handler = CanonicalOperandHandler(ops[i].handler);
#endif
#if FIBERCPU_ENABLE_SUPEROPCODES
    if (!ops) return;

    for (uint32_t i = 0; i + 1 < op_count; ++i) {
        if (HandlerFunc superopcode = FindSuperOpcode(&ops[i])) {
            ops[i].handler = superopcode;
        }
    }
#endif
#if FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY
    for (uint32_t i = 0; i < op_count; ++i)
        if (ops[i].len)
            if (auto handler = LowerOperandHandler(&ops[i])) ops[i].handler = handler;
#endif
}

}  // namespace fiberish
