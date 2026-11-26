#pragma once

#include "vector.h"

namespace fiberish::aarch64::detail {

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <FloatFamily family>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t FloatHandler(A64_HANDLER_PARAMS) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    bool valid;
#if defined(__clang__)
    [[clang::always_inline]]
#endif
    valid = FloatRegisterOp<family>(state, op, flags_cache, HotRegisters{state A64_HANDLER_PIN_ARGS});
    if (!valid) [[unlikely]]
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                               Undefined(current->GetStartPc()));
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

template <VectorFamily family>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t VectorHandler(A64_HANDLER_PARAMS) {
    const auto op = BaseInstruction(uint32_t(current->Operands()));
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (family == VectorFamily::Structure) {
        auto step = VectorStructure<true>(state, nullptr, op, registers, data_tlb, utlb_tags, utlb_addend);
        if (step.reason == Stop::Replay) [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
        if (step.reason != Stop::Continue) [[unlikely]] {
            if (step.reason == Stop::Undefined) step.address = current->GetStartPc();
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
        }
    } else {
        bool valid;
#if defined(__clang__)
        [[clang::always_inline]]
#endif
        valid = VectorRegisterOp<family>(state, op, registers);
        if (!valid) [[unlikely]]
            return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                                   Undefined(current->GetStartPc()));
    }
    A64_DISPATCH_NEXT(current->NextOp<false>());
}

#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
#if !FIBERCPU_AARCH64_SIMDE
// Keep the host SIMD types local. The NEON and SoftFloat headers both
// declare floating-point types with the same names.
using HostBytes8 = uint8_t __attribute__((vector_size(8)));
using HostBytes16 = uint8_t __attribute__((vector_size(16)));
#endif

enum class ByteVectorOp { PairMin, PairMax, Equal, EqualZero };

template <ByteVectorOp op>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t ByteVectorHandler(A64_HANDLER_PARAMS) {
    const auto word = uint32_t(current->Operands());
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
    A64_DISPATCH_NEXT(current->NextOp<false>());
}
#endif
#endif

template <FloatFamily family>
constexpr HandlerBinding BindFloatHandler() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {&ScalarFloat<family>, &FloatHandler<family>};
#else
    return {&ScalarFloat<family>, nullptr};
#endif
}

template <VectorFamily family>
constexpr HandlerBinding BindVectorHandler([[maybe_unused]] uint32_t word) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
#if (defined(__aarch64__) && !defined(_MSC_VER)) || FIBERCPU_AARCH64_SIMDE
    if constexpr (family == VectorFamily::Three && std::endian::native == std::endian::little) {
        switch (word & 0xffe0fc00) {
            case 0x6e20ac00:
                return {&Vector<family>, &ByteVectorHandler<ByteVectorOp::PairMin>};
            case 0x6e20a400:
                return {&Vector<family>, &ByteVectorHandler<ByteVectorOp::PairMax>};
            case 0x6e208c00:
                return {&Vector<family>, &ByteVectorHandler<ByteVectorOp::Equal>};
        }
    }
    if constexpr (family == VectorFamily::Misc && std::endian::native == std::endian::little) {
        if ((word & 0xfffffc00) == 0x0e209800) return {&Vector<family>, &ByteVectorHandler<ByteVectorOp::EqualZero>};
    }
#endif
    return {&Vector<family>, &VectorHandler<family>};
#else
    return {&Vector<family>, nullptr};
#endif
}

inline HandlerBinding LowerFloat(uint32_t word) {
    switch (DecodeFloatFamily(word)) {
        case FloatFamily::Convert:
            return BindFloatHandler<FloatFamily::Convert>();
        case FloatFamily::Fused:
            return BindFloatHandler<FloatFamily::Fused>();
        case FloatFamily::Compare:
            return BindFloatHandler<FloatFamily::Compare>();
        case FloatFamily::ConditionalCompare:
            return BindFloatHandler<FloatFamily::ConditionalCompare>();
        case FloatFamily::Select:
            return BindFloatHandler<FloatFamily::Select>();
        case FloatFamily::Binary:
            return BindFloatHandler<FloatFamily::Binary>();
        case FloatFamily::Immediate:
            return BindFloatHandler<FloatFamily::Immediate>();
        case FloatFamily::Unary:
            return BindFloatHandler<FloatFamily::Unary>();
        case FloatFamily::MoveFromVector:
            return BindFloatHandler<FloatFamily::MoveFromVector>();
        case FloatFamily::MoveToVector:
            return BindFloatHandler<FloatFamily::MoveToVector>();
        case FloatFamily::Integer:
            return BindFloatHandler<FloatFamily::Integer>();
        case FloatFamily::Undefined:
            return {};
    }
}

inline HandlerBinding LowerVector(uint32_t word) {
    switch (DecodeVectorFamily(word)) {
        case VectorFamily::Structure:
            return BindVectorHandler<VectorFamily::Structure>(word);
        case VectorFamily::Three:
            return BindVectorHandler<VectorFamily::Three>(word);
        case VectorFamily::ThreeFloat:
            return BindVectorHandler<VectorFamily::ThreeFloat>(word);
        case VectorFamily::Widen:
            return BindVectorHandler<VectorFamily::Widen>(word);
        case VectorFamily::Misc:
            return BindVectorHandler<VectorFamily::Misc>(word);
        case VectorFamily::MiscFloat:
            return BindVectorHandler<VectorFamily::MiscFloat>(word);
        case VectorFamily::Reduction:
            return BindVectorHandler<VectorFamily::Reduction>(word);
        case VectorFamily::PairAdd:
            return BindVectorHandler<VectorFamily::PairAdd>(word);
        case VectorFamily::PairFloatAdd:
            return BindVectorHandler<VectorFamily::PairFloatAdd>(word);
        case VectorFamily::ModifiedImmediate:
            return BindVectorHandler<VectorFamily::ModifiedImmediate>(word);
        case VectorFamily::ShiftImmediate:
            return BindVectorHandler<VectorFamily::ShiftImmediate>(word);
        case VectorFamily::Copy:
            return BindVectorHandler<VectorFamily::Copy>(word);
        case VectorFamily::Extract:
            return BindVectorHandler<VectorFamily::Extract>(word);
        case VectorFamily::Table:
            return BindVectorHandler<VectorFamily::Table>(word);
        case VectorFamily::Permute:
            return BindVectorHandler<VectorFamily::Permute>(word);
        case VectorFamily::Undefined:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
