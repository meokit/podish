#include "ops_muldiv_impl.h"

namespace fiberish {
void RegisterMulDivOps() {
    using namespace op;
    g_Handlers[0x69] = DispatchWrapper<OpImul_GvEvIz>;
    g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIb>;
    g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
}

}  // namespace fiberish
