#pragma once

#include <cstdint>
#include <span>

namespace fiberish::jit {

struct JitOpRange {
    uint32_t index;
    uint16_t stencil_id;
    const char* name;
    uint8_t* start;
    uint8_t* end;
};

struct PeepholeStats {
    uint32_t branch_to_next_nops = 0;
    uint32_t prfm_nops = 0;
    uint32_t movwide_ubfm_constant_folds = 0;
};

PeepholeStats OptimizeBlockInPlace(uint8_t* block_start, uint8_t* block_end, std::span<const JitOpRange> ops);

}  // namespace fiberish::jit
