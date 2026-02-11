#include "ops_sse_fp_impl.h"

namespace fiberish {
void RegisterSseFpOps() {
    using namespace op;
    g_Handlers[0x1C2] = DispatchWrapper<OpCmp_Sse>;
    g_Handlers[0x15E] = DispatchWrapper<OpDiv_Sse>;
    g_Handlers[0x128] = DispatchWrapper<OpMovAp_Load>;
    g_Handlers[0x129] = DispatchWrapper<OpMovAp_Store>;
    g_Handlers[0x15F] = DispatchWrapper<OpMax_Sse>;
    g_Handlers[0x15D] = DispatchWrapper<OpMin_Sse>;
    g_Handlers[0x158] = DispatchWrapper<OpAdd_Sse>;
    g_Handlers[0x159] = DispatchWrapper<OpMul_Sse>;
    g_Handlers[0x15C] = DispatchWrapper<OpSub_Sse>;
    g_Handlers[0x154] = DispatchWrapper<OpAnd_Sse>;
    g_Handlers[0x155] = DispatchWrapper<OpAndn_Sse>;
    g_Handlers[0x156] = DispatchWrapper<OpOr_Sse>;
    g_Handlers[0x157] = DispatchWrapper<OpXor_Sse>;
    g_Handlers[0x12E] = DispatchWrapper<OpUcomis_Unified>;  // 0F 2E: UCOMISS / UCOMISD
    g_Handlers[0x151] = DispatchWrapper<OpSqrt_Sse>;        // 0F 51: SQRTPS/PD/SS/SD
    g_Handlers[0x1C6] = DispatchWrapper<OpShuf_Unified>;    // 0F C6: SHUFPS / SHUFPD
    g_Handlers[0x114] = DispatchWrapper<OpUnpckl_Unified>;  // 0F 14: UNPCKLPS / PD
    g_Handlers[0x115] = DispatchWrapper<OpUnpckh_Unified>;  // 0F 15: UNPCKHPS / PD
    g_Handlers[0x12F] = DispatchWrapper<OpComis_Unified>;   // 0F 2F: COMISS / COMISD
    g_Handlers[0x153] = DispatchWrapper<OpRcp_Sse>;         // 0F 53: RCPPS / RCPSS
    g_Handlers[0x152] = DispatchWrapper<OpRsqrt_Sse>;       // 0F 52: RSQRTPS / RSQRTSS
}

}  // namespace fiberish
