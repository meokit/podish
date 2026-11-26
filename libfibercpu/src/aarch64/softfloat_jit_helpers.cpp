#if FIBERCPU_AARCH64_SOFTFLOAT && FIBERCPU_LLVM_JIT

#include "softfloat_jit_helpers.h"
#include "floating.h"

namespace fiberish::aarch64::detail {

// SoftFloat operates on compiler-managed thread-local data. Keep that state
// inside the native library and expose only the interpreter's FP operations.
extern "C" uint64_t FiberArmSoftFloatArithmetic(State& state, unsigned width, FPOp op, uint64_t a, uint64_t b,
                                                uint64_t c, FPRound direction, bool exact) {
    return FPArithmetic(state, width, op, a, b, c, direction, exact);
}

extern "C" uint64_t FiberArmSoftFloatConvert(State& state, unsigned source, unsigned dest, uint64_t bits) {
    return FPConvert(state, source, dest, bits);
}

extern "C" uint64_t FiberArmSoftFloatIntToFP(State& state, unsigned width, uint64_t bits, unsigned integer_width,
                                             bool is_unsigned, unsigned scale) {
    return IntToFP(state, width, bits, integer_width, is_unsigned, scale);
}

}  // namespace fiberish::aarch64::detail
#endif
