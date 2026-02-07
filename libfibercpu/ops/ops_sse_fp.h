// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {

simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred);
simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred);
simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred);
simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred);

void RegisterSseFpOps();

}  // namespace x86emu