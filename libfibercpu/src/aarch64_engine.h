#pragma once

#include <cstdint>
namespace fiberish {
struct EmuState;
int RunArmEngine(EmuState& engine, uint64_t end_pc, uint64_t budget, bool step);
}  // namespace fiberish
