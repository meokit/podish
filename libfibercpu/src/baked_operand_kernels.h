#pragma once

#include "dispatch.h"

namespace fiberish::baked_operand {

// Use the same kernel expressions as exact LLVM compilation.
#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions)                \
    template <class Policy>                                                       \
    FORCE_INLINE LogicFlow Name(LogicFuncParams) {                                \
        [[maybe_unused]] constexpr auto metadata = Policy::metadata;             \
        [[maybe_unused]] constexpr const auto& operands = Policy::operands;       \
        return Exact;                                                             \
    }
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP

}  // namespace fiberish::baked_operand
