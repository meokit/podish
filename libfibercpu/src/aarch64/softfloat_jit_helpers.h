#pragma once

#include <cstdint>

namespace fiberish::aarch64 {
struct State;

namespace detail {

enum class FPOp : uint8_t;
enum FPRound : unsigned;

extern "C" uint64_t FiberArmSoftFloatArithmetic(State& state, unsigned width, FPOp op, uint64_t a, uint64_t b,
                                                uint64_t c, FPRound direction, bool exact);
extern "C" uint64_t FiberArmSoftFloatConvert(State& state, unsigned source, unsigned dest, uint64_t bits);
extern "C" uint64_t FiberArmSoftFloatIntToFP(State& state, unsigned width, uint64_t bits, unsigned integer_width,
                                             bool is_unsigned, unsigned scale);

}  // namespace detail
}  // namespace fiberish::aarch64
