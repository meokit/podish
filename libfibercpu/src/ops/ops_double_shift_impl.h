#pragma once

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"

namespace fiberish {

enum class DoubleShiftDirection { Left, Right };
enum class DoubleShiftCount { Immediate, Cl };

template <typename T, DoubleShiftDirection Direction, AddrModeSpec Form, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpDoubleShiftValue(LogicFuncParams, uint8_t count) {
    const ModRMRmwOperand<Form> operand(state, gpr, op, operand_modrm);
    const auto destination = operand.template Read<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!destination) return LogicFlow::RestartMemoryOp;

    constexpr unsigned width = sizeof(T) * 8;
    // For a word count of 16, the existing handler reads r/m before returning.
    count &= width - 1;
    if (count == 0) return LogicFlow::Continue;

    constexpr bool left = Direction == DoubleShiftDirection::Left;
    constexpr T sign_bit = T{1} << (width - 1);
    const T dst = *destination;
    const T src = ReadRegGeneric<T>(state, gpr, (operand_modrm >> 3) & 7);
    const T result = left ? (dst << count) | (src >> (width - count)) : (dst >> count) | (src << (width - count));
    if constexpr (Flags != FlagsSpec::Dead) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if ((dst >> (left ? width - count : count - 1)) & 1) flags |= CF_MASK;
        if (result == 0) flags |= ZF_MASK;
        if (result & sign_bit) flags |= SF_MASK;
        if (Parity(result & 0xff)) flags |= PF_MASK;
        if (count == 1 && ((dst ^ result) & sign_bit)) flags |= OF_MASK;
        SetFlags32AndSyncParityState(flags_cache, flags);
    }

    if (!operand.template Write<T, OpOnTLBMiss::Retry>(state, gpr, op, result, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <DoubleShiftDirection Direction, DoubleShiftCount CountSource, OpsizeSpec Size = OpsizeSpec::Dynamic,
          AddrModeSpec Form = AddrModeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpDoubleShift_Internal(LogicFuncParams) {
    const uint8_t count = (CountSource == DoubleShiftCount::Immediate ? imm : gpr.GetReg(state, ECX)) & 31;
    if (count == 0) return LogicFlow::Continue;
    if (OperandIs16<Size>(op)) return OpDoubleShiftValue<uint16_t, Direction, Form, Flags>(LogicPassParams, count);
    return OpDoubleShiftValue<uint32_t, Direction, Form, Flags>(LogicPassParams, count);
}

namespace op {

FORCE_INLINE LogicFlow OpShld_EvGvIb(LogicFuncParams) {
    return OpDoubleShift_Internal<DoubleShiftDirection::Left, DoubleShiftCount::Immediate>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpShld_EvGvCl(LogicFuncParams) {
    return OpDoubleShift_Internal<DoubleShiftDirection::Left, DoubleShiftCount::Cl>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpShrd_EvGvIb(LogicFuncParams) {
    return OpDoubleShift_Internal<DoubleShiftDirection::Right, DoubleShiftCount::Immediate>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpShrd_EvGvCl(LogicFuncParams) {
    return OpDoubleShift_Internal<DoubleShiftDirection::Right, DoubleShiftCount::Cl>(LogicPassParams);
}

#define FIBERCPU_BIND_INTEGER_DOUBLE_SHIFT
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_INTEGER_DOUBLE_SHIFT

}  // namespace op
}  // namespace fiberish
