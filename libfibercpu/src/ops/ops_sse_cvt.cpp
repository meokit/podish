#include "ops_sse_cvt_impl.h"

namespace fiberish {
void RegisterSseCvtOps() {
    using namespace op;
    g_Handlers[0x12A] = DispatchWrapper<OpCvt_2A>;
    g_Handlers[0x15A] = DispatchWrapper<OpCvt_5A>;
    g_Handlers[0x15B] = DispatchWrapper<OpCvt_5B>;
    g_Handlers[0x1E6] = DispatchWrapper<OpCvt_E6>;
    g_Handlers[0x12C] = DispatchWrapper<OpCvt_2C>;
    g_Handlers[0x12D] = DispatchWrapper<OpCvt_2D>;
}

}  // namespace fiberish
