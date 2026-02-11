#include "ops_fpu_impl.h"

namespace fiberish {
void RegisterFpuOps() {
    g_Handlers[0xD8] = DispatchWrapper<op::OpFpu_D8>;
    g_Handlers[0xD9] = DispatchWrapper<op::OpFpu_D9>;
    g_Handlers[0xDA] = DispatchWrapper<op::OpFpu_DA>;
    g_Handlers[0xDB] = DispatchWrapper<op::OpFpu_DB>;
    g_Handlers[0xDC] = DispatchWrapper<op::OpFpu_DC>;
    g_Handlers[0xDD] = DispatchWrapper<op::OpFpu_DD>;
    g_Handlers[0xDE] = DispatchWrapper<op::OpFpu_DE>;
    g_Handlers[0xDF] = DispatchWrapper<op::OpFpu_DF>;
}

}  // namespace fiberish
