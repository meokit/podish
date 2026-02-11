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
    g_Handlers[0x1E7] = DispatchWrapper<OpMovntdq>;
    g_Handlers[0x1C3] = DispatchWrapper<OpMovnti>;
    g_Handlers[0x1F7] = DispatchWrapper<OpMaskmovdqu>;
    g_Handlers[0x16E] = DispatchWrapper<OpMovd_Load>;
    g_Handlers[0x17E] = DispatchWrapper<OpMovd_Store>;
    g_Handlers[0x16F] = DispatchWrapper<OpGroup_Mov6F>;
    g_Handlers[0x17F] = DispatchWrapper<OpGroup_Mov7F>;
    g_Handlers[0x1D6] = DispatchWrapper<OpMovq_Store>;  // 66 0F D6: MOVQ xmm/m64, xmm
}

}  // namespace fiberish
