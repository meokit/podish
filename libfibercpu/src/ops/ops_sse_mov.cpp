#include "ops_registration_utils.h"
#include "ops_sse_mov_impl.h"

namespace fiberish {
void RegisterSseMovOps() {
    using namespace op;
    g_Handlers[0x110] = DispatchWrapper<OpMov_Sse_Load>;
    g_Handlers[0x111] = DispatchWrapper<OpMov_Sse_Store>;
    g_Handlers[0x112] = DispatchWrapper<OpGroup_Mov12>;
    g_Handlers[0x113] = DispatchWrapper<OpGroup_Mov13>;
    g_Handlers[0x116] = DispatchWrapper<OpGroup_Mov16>;
    g_Handlers[0x117] = DispatchWrapper<OpGroup_Mov17>;
    g_Handlers[0x150] = DispatchWrapper<OpMovmsk_Unified>;
    g_Handlers[0x12B] = DispatchWrapper<OpMovnt_Sse>;
    // 0F E7 is MOVNTQ (MMX) without prefix; MOVNTDQ is 66-prefixed.
    RegisterSseOp<OpMovntdq>(0x1E7);
    g_Handlers[0x1C3] = DispatchWrapper<OpMovnti>;
    // 0F F7 is MASKMOVQ (MMX) without prefix; MASKMOVDQU is 66-prefixed.
    RegisterSseOp<OpMaskmovdqu>(0x1F7);
    // 0F 6E / 0F 7E are MMX without prefix, SSE with 66 prefix.
    // Keep MMX base handlers from RegisterMmxOps and add SSE specializations only.
    RegisterSseOp<OpMovd_Load>(0x16E);   // 66 0F 6E: MOVD xmm, r/m32
    RegisterSseOp<OpMovd_Store>(0x17E);  // 66 0F 7E: MOVD r/m32, xmm

    SpecCriteria criteria_7e_f3;
    criteria_7e_f3.prefix_mask = prefix::REP;
    criteria_7e_f3.prefix_val = prefix::REP;
    RegisterSpecializedHandler(0x17E, criteria_7e_f3, (HandlerFunc)DispatchWrapper<OpMovd_Store>);

    // 0F 6F: MOVQ (MMX, No Prefix) / MOVDQA (66) / MOVDQU (F3)
    // We register specialized handlers for SSE versions to avoid overwriting MMX
    RegisterSseOp<OpMovdqa_Load>(0x16F);  // 66 0F 6F

    // Register specialized for F3 0F 6F (MOVDQU)
    SpecCriteria criteria_f3;
    criteria_f3.prefix_mask = prefix::REP;
    criteria_f3.prefix_val = prefix::REP;
    RegisterSpecializedHandler(0x16F, criteria_f3, (HandlerFunc)DispatchWrapper<OpMovdqu_Load>);

    // 0F 7F: MOVQ (MMX, No Prefix) / MOVDQA (66) / MOVDQU (F3)
    RegisterSseOp<OpMovdqa_Store>(0x17F);  // 66 0F 7F

    SpecCriteria criteria_f3_store;
    criteria_f3_store.prefix_mask = prefix::REP;
    criteria_f3_store.prefix_val = prefix::REP;
    RegisterSpecializedHandler(0x17F, criteria_f3_store, (HandlerFunc)DispatchWrapper<OpMovdqu_Store>);

    // 0F D6: MOVQ (SSE2 Store, 66) / MOVQ2DQ (F3) / MOVDQ2Q (F2)
    RegisterSseOp<OpMovq_Store>(0x1D6);  // 66 0F D6

    // MOVQ2DQ xmm, mm (F3 0F D6)
    SpecCriteria criteria_movq2dq;
    criteria_movq2dq.prefix_mask = prefix::REP;
    criteria_movq2dq.prefix_val = prefix::REP;
    RegisterSpecializedHandler(0x1D6, criteria_movq2dq, (HandlerFunc)DispatchWrapper<OpMovq2dq>);

    // MOVDQ2Q mm, xmm (F2 0F D6)
    SpecCriteria criteria_movdq2q;
    criteria_movdq2q.prefix_mask = prefix::REPNE;
    criteria_movdq2q.prefix_val = prefix::REPNE;
    RegisterSpecializedHandler(0x1D6, criteria_movdq2q, (HandlerFunc)DispatchWrapper<OpMovdq2q>);
}

}  // namespace fiberish
