#pragma once
// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#include <bit>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_carry_rmw_impl.h"

namespace fiberish {

enum class ShiftWidth { Byte, Scalar };
enum class ShiftCountSource { Immediate, One, Cl };

template <typename T, ShiftCountSource CountSource, GroupOpSpec SubOp, FlagsSpec Flags, AddrModeSpec Form>
FORCE_INLINE LogicFlow OpGroup2_T_Internal(LogicFuncParams) {
    const ModRMRmwOperand<Form> operand(state, gpr, op, operand_modrm);
    const auto dest = operand.template Read<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!dest) return LogicFlow::RestartMemoryOp;

    uint8_t count = 1;
    if constexpr (CountSource == ShiftCountSource::Immediate)
        count = imm & 31;
    else if constexpr (CountSource == ShiftCountSource::Cl)
        count = gpr.GetReg(state, ECX) & 31;
    // Preserve the operand read and any fault even for masked count zero.
    if (count == 0) return LogicFlow::Continue;

    constexpr bool update_flags = Flags != FlagsSpec::Dead;
    T result;
    switch (GroupOperation<SubOp>(operand_modrm)) {
        case 0:
            result = AluRol<T, update_flags>(state, flags_cache, *dest, count);
            break;
        case 1:
            result = AluRor<T, update_flags>(state, flags_cache, *dest, count);
            break;
        case 2:
            return ExecuteCarryRmw<CarryRmwKind::Rcl, T, update_flags>(state, gpr, op, operand, *dest, count, utlb,
                                                                       flags_cache);
        case 3:
            return ExecuteCarryRmw<CarryRmwKind::Rcr, T, update_flags>(state, gpr, op, operand, *dest, count, utlb,
                                                                       flags_cache);
        case 4:
        case 6:  // SAL alias
            result = AluShl<T, update_flags>(state, flags_cache, *dest, count);
            break;
        case 5:
            result = AluShr<T, update_flags>(state, flags_cache, *dest, count);
            break;
        case 7:
            result = AluSar<T, update_flags>(state, flags_cache, *dest, count);
            break;
        default:
            __builtin_unreachable();  // The three-bit group extension is exhaustive.
    }

    if (!operand.template Write<T, OpOnTLBMiss::Retry>(state, gpr, op, result, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <ShiftWidth Width, ShiftCountSource CountSource, GroupOpSpec SubOp = GroupOpSpec::Dynamic,
          OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup2_Internal(LogicFuncParams) {
    if constexpr (Width == ShiftWidth::Byte) {
        return OpGroup2_T_Internal<uint8_t, CountSource, SubOp, Flags, Form>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op))
            return OpGroup2_T_Internal<uint16_t, CountSource, SubOp, Flags, Form>(LogicPassParams);
        return OpGroup2_T_Internal<uint32_t, CountSource, SubOp, Flags, Form>(LogicPassParams);
    }
}

template <typename T>
FORCE_INLINE T ModifyBit(T value, unsigned offset, uint8_t subop) {
    const T mask = static_cast<T>(uint32_t{1} << offset);
    switch (subop) {
        case 5:
            return static_cast<T>(value | mask);
        case 6:
            return static_cast<T>(value & ~mask);
        case 7:
            return static_cast<T>(value ^ mask);
        default:
            return value;
    }
}

// A register bit index can address other bytes of the memory bit string.
template <GroupOpSpec Operation, OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpBitRegister_Internal(LogicFuncParams) {
    const uint32_t index = gpr.GetReg(state, (operand_modrm >> 3) & 7);
    constexpr auto subop = static_cast<uint8_t>(Operation);
    if (OperandIsReg<Form>(operand_modrm)) {
        const auto rm = operand_modrm & 7;
        const uint32_t value = gpr.GetReg(state, rm);
        const unsigned offset = index & (OperandIs16<Size>(op) ? 15 : 31);
        if constexpr (Flags != FlagsSpec::Dead) AssignCF(flags_cache, (value >> offset) & 1);
        if constexpr (Operation != GroupOpSpec::Ext4) gpr.SetReg(state, rm, ModifyBit(value, offset, subop));
    } else {
        const uint32_t address = ComputeLinearAddress(state, gpr, op) + (static_cast<int32_t>(index) >> 3);
        const unsigned offset = index & 7;
        const auto value = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, gpr, address, utlb, op);
        if (!value) return LogicFlow::RestartMemoryOp;
        if constexpr (Flags != FlagsSpec::Dead) AssignCF(flags_cache, (*value >> offset) & 1);
        if constexpr (Operation != GroupOpSpec::Ext4) {
            if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, gpr, address, ModifyBit(*value, offset, subop), utlb, op))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

// An immediate bit index wraps within the addressed word or dword.
template <typename T, GroupOpSpec Operation, AddrModeSpec Form, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpGroup8_T_Internal(LogicFuncParams) {
    const auto subop = GroupOperation<Operation>(operand_modrm);
    const auto rm = operand_modrm & 7;
    const unsigned offset = imm & (sizeof(T) * 8 - 1);
    const bool memory = !OperandIsReg<Form>(operand_modrm);
    uint32_t address = 0;
    T value;
    if (memory) {
        address = ComputeLinearAddress(state, gpr, op);
        const auto read = ReadMem<T, OpOnTLBMiss::Restart>(state, gpr, address, utlb, op);
        if (!read) return LogicFlow::RestartMemoryOp;
        value = *read;
    } else {
        value = ReadRegGeneric<T>(state, gpr, rm);
    }

    if constexpr (Flags != FlagsSpec::Dead) AssignCF(flags_cache, (value >> offset) & 1);
    if (subop >= 5) {
        const T result = ModifyBit(value, offset, subop);
        if (memory) {
            if (!WriteMem<T, OpOnTLBMiss::Retry>(state, gpr, address, result, utlb, op))
                return LogicFlow::RetryMemoryOp;
        } else {
            WriteRegGeneric<T>(state, gpr, rm, result);
        }
    }
    return LogicFlow::Continue;
}

template <GroupOpSpec Operation = GroupOpSpec::Dynamic, OpsizeSpec Size = OpsizeSpec::Dynamic,
          AddrModeSpec Form = AddrModeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup8_Internal(LogicFuncParams) {
    if (OperandIs16<Size>(op)) return OpGroup8_T_Internal<uint16_t, Operation, Form, Flags>(LogicPassParams);
    return OpGroup8_T_Internal<uint32_t, Operation, Form, Flags>(LogicPassParams);
}

enum class BitScanOperation { Dynamic, Forward, Reverse, TrailingCount };

template <typename T, BitScanOperation Operation, AddrModeSpec Form, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpBitScan_T_Internal(LogicFuncParams) {
    const auto source = ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb);
    if (!source) return LogicFlow::RestartMemoryOp;
    const auto reg = (operand_modrm >> 3) & 7;
    constexpr bool update_flags = Flags != FlagsSpec::Dead;

    if constexpr (Operation == BitScanOperation::Dynamic || Operation == BitScanOperation::TrailingCount) {
        if (Operation == BitScanOperation::TrailingCount || GetPrefixes(op).flags.rep) {
            const auto count = std::countr_zero(*source);
            if constexpr (update_flags) {
                AssignCF(flags_cache, *source == 0);
                AssignZF(flags_cache, count == 0);
            }
            gpr.SetReg(state, reg, count);
            return LogicFlow::Continue;
        }
    }

    if constexpr (update_flags) AssignZF(flags_cache, *source == 0);
    if (*source != 0) {
        const auto index =
            Operation == BitScanOperation::Reverse ? std::bit_width(*source) - 1 : std::countr_zero(*source);
        gpr.SetReg(state, reg, index);
    }
    return LogicFlow::Continue;
}

template <BitScanOperation Operation, OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpBitScan_Internal(LogicFuncParams) {
    if (OperandIs16<Size>(op)) return OpBitScan_T_Internal<uint16_t, Operation, Form, Flags>(LogicPassParams);
    return OpBitScan_T_Internal<uint32_t, Operation, Form, Flags>(LogicPassParams);
}

template <RmOperandSpec Reg = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpBswap_Internal(LogicFuncParams) {
    const auto reg = Reg == RmOperandSpec::Dynamic
                         ? operand_modrm & 7
                         : static_cast<uint8_t>(Reg) - static_cast<uint8_t>(RmOperandSpec::Reg0);
    gpr.SetReg(state, reg, __builtin_bswap32(gpr.GetReg(state, reg)));
    return LogicFlow::Continue;
}

namespace op {

#define FIBERCPU_BIND_SHIFT_BIT
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_SHIFT_BIT

}  // namespace op

}  // namespace fiberish
