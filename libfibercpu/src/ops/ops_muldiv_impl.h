#pragma once

#include <type_traits>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"

namespace fiberish {

enum class ImulSource { Register, OperandWidthImmediate, SignedByteImmediate };

template <typename T, ImulSource Source, AddrModeSpec Form, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpImul_T(LogicFuncParams) {
    using Signed = std::make_signed_t<T>;
    using Wide = std::conditional_t<sizeof(T) == 2, int32_t, int64_t>;
    const uint8_t reg = (operand_modrm >> 3) & 7;
    const Signed lhs = [&]() -> Signed {
        if constexpr (Source == ImulSource::Register) return static_cast<Signed>(gpr.GetReg(state, reg));
        if constexpr (Source == ImulSource::SignedByteImmediate) return static_cast<int8_t>(imm);
        return static_cast<Signed>(imm);
    }();
    const auto rhs = ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb);
    if (!rhs) return LogicFlow::RestartMemoryOp;

    const Wide product = static_cast<Wide>(lhs) * static_cast<Signed>(*rhs);
    const T result = static_cast<T>(product);
    WriteRegGeneric<T>(state, gpr, reg, result);
    if constexpr (Flags != FlagsSpec::Dead) {
        if (product != static_cast<Signed>(result))
            SetFlagBits(flags_cache, OF_MASK | CF_MASK);
        else
            ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
    }
    return LogicFlow::Continue;
}

template <ImulSource Source, OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpImul_Internal(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<Size>(op, operands)) return OpImul_T<uint16_t, Source, Form, Flags>(LogicPassParams);
    return OpImul_T<uint32_t, Source, Form, Flags>(LogicPassParams);
}

namespace op {

FORCE_INLINE LogicFlow OpImul_GvEv(LogicFuncParams) { return OpImul_Internal<ImulSource::Register>(LogicPassParams); }

FORCE_INLINE LogicFlow OpImul_GvEvIz(LogicFuncParams) {
    return OpImul_Internal<ImulSource::OperandWidthImmediate>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpImul_GvEvIb(LogicFuncParams) {
    return OpImul_Internal<ImulSource::SignedByteImmediate>(LogicPassParams);
}

#define FIBERCPU_BIND_INTEGER_MULDIV
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_INTEGER_MULDIV

}  // namespace op
}  // namespace fiberish
