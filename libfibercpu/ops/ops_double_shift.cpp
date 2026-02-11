#include "ops_double_shift_impl.h"

namespace fiberish {
void RegisterDoubleShiftOps() {
    g_Handlers[0x1A4] = DispatchWrapper<op::OpShld_EvGvIb>;
    g_Handlers[0x1A5] = DispatchWrapper<op::OpShld_EvGvCl>;
    g_Handlers[0x1AC] = DispatchWrapper<op::OpShrd_EvGvIb>;
    g_Handlers[0x1AD] = DispatchWrapper<op::OpShrd_EvGvCl>;
}

}  // namespace fiberish
