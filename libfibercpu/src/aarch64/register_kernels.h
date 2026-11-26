#pragma once

#include "vector.h"

namespace fiberish::aarch64::detail {

#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
#if !FIBERCPU_AARCH64_SIMDE
// Keep the host SIMD types local. The NEON and SoftFloat headers both
// declare floating-point types with the same names.
using HostBytes8 = uint8_t __attribute__((vector_size(8)));
using HostBytes16 = uint8_t __attribute__((vector_size(16)));
#endif

template <ByteVectorOp op>
#if defined(__GNUC__) || defined(__clang__)
[[gnu::always_inline]]
#endif
inline void ExecuteByteVector(State& state, uint32_t word) {
    const auto rd = Field(word, 0, 5);
    const auto rn = Field(word, 5, 5);
#if FIBERCPU_AARCH64_SIMDE
    const auto left = std::bit_cast<simde_uint8x16_t>(state.v[rn]);
    if constexpr (op == ByteVectorOp::EqualZero) {
        const auto result = simde_vceq_u8(simde_vget_low_u8(left), simde_vdup_n_u8(0));
        state.v[rd] = {std::bit_cast<uint64_t>(result), 0};
    } else {
        const auto right = std::bit_cast<simde_uint8x16_t>(state.v[Field(word, 16, 5)]);
        simde_uint8x16_t result;
        if constexpr (op == ByteVectorOp::PairMin)
            result = simde_vpminq_u8(left, right);
        else if constexpr (op == ByteVectorOp::PairMax)
            result = simde_vpmaxq_u8(left, right);
        else
            result = simde_vceqq_u8(left, right);
        state.v[rd] = std::bit_cast<Vector128>(result);
    }
#else
    if constexpr (op == ByteVectorOp::EqualZero) {
        const auto left = std::bit_cast<HostBytes8>(state.v[rn].lo);
        HostBytes8 result;
        asm("cmeq %0.8b, %1.8b, #0" : "=w"(result) : "w"(left));
        state.v[rd] = {std::bit_cast<uint64_t>(result), 0};
    } else {
        const auto left = std::bit_cast<HostBytes16>(state.v[rn]);
        const auto right = std::bit_cast<HostBytes16>(state.v[Field(word, 16, 5)]);
        HostBytes16 result;
        if constexpr (op == ByteVectorOp::PairMin)
            asm("uminp %0.16b, %1.16b, %2.16b" : "=w"(result) : "w"(left), "w"(right));
        else if constexpr (op == ByteVectorOp::PairMax)
            asm("umaxp %0.16b, %1.16b, %2.16b" : "=w"(result) : "w"(left), "w"(right));
        else
            asm("cmeq %0.16b, %1.16b, %2.16b" : "=w"(result) : "w"(left), "w"(right));
        state.v[rd] = std::bit_cast<Vector128>(result);
    }
#endif
}
#endif

}  // namespace fiberish::aarch64::detail
