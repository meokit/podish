#pragma once
// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>
#include <type_traits>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

// ------------------------------------------------------------------------------------------------
// Basic MOV / XCHG
// ------------------------------------------------------------------------------------------------

template <AddrIndexSpec IndexSpec = AddrIndexSpec::Dynamic, AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::Dynamic, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE uint32_t ComputeLinearAddressSpecialized(EmuState* state, GprCache& gpr, const DecodedOp* op) {
    if (gpr.baked_address) return ComputeLinearAddress(state, gpr, op);
    uint32_t addr = GetDisplacement(op);

    if constexpr (BaseRegSpec == AddrBaseRegSpec::Dynamic) {
        addr += gpr.GetReg(state, op->AddressBase());
    } else if constexpr (BaseRegSpec != AddrBaseRegSpec::Absent) {
        addr += gpr.GetReg(state, static_cast<int>(BaseRegSpec));
    }

    if constexpr (IndexSpec == AddrIndexSpec::Dynamic) {
        const uint32_t index = gpr.GetAddressIndex(state, op->AddressIndex());
        addr += index << op->AddressScale();
    } else if constexpr (IndexSpec != AddrIndexSpec::Absent) {
        addr += gpr.GetReg(state, static_cast<int>(IndexSpec)) << op->AddressScale();
    }

    if (AddressSize == AddrsizeSpec::Addr16 || (AddressSize == AddrsizeSpec::Dynamic && op->AddressIs16()))
        addr &= 0xFFFFu;

    if constexpr (SegmentSpec == AddrSegmentSpec::Dynamic) {
        addr += GetMemSegmentBase(state, gpr, op);
    } else if constexpr (SegmentSpec == AddrSegmentSpec::Default) {
        // "NoSegment" means no override, not an architectural zero base.
        // Keep specialized handlers correct if a caller supplies non-flat DS/SS.
        if constexpr (BaseRegSpec == AddrBaseRegSpec::Dynamic) {
            addr += GetMemSegmentBase(state, gpr, op);
        } else {
            constexpr int segment =
                (BaseRegSpec == AddrBaseRegSpec::Esp || BaseRegSpec == AddrBaseRegSpec::Ebp) ? 3 : 4;
            addr += state->ctx.seg_base[segment];
        }
    } else if constexpr (SegmentSpec != AddrSegmentSpec::Default) {
        addr += state->ctx.seg_base[static_cast<int>(SegmentSpec)];
    }

    return addr;
}

template <RmOperandSpec SrcSpec = RmOperandSpec::Dynamic, RmOperandSpec DstSpec = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_EvGv_Reg_Internal(LogicFuncParams) {
    // RmOperandSpec: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source (ModRM.Reg)
    if constexpr (SrcSpec >= RmOperandSpec::Reg0 && SrcSpec <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedSrc == 0)
            val = gpr.GetReg(state, 0);
        else if constexpr (FixedSrc == 1)
            val = gpr.GetReg(state, 1);
        else if constexpr (FixedSrc == 2)
            val = gpr.GetReg(state, 2);
        else if constexpr (FixedSrc == 3)
            val = gpr.GetReg(state, 3);
        else if constexpr (FixedSrc == 4)
            val = gpr.GetReg(state, 4);
        else if constexpr (FixedSrc == 5)
            val = gpr.GetReg(state, 5);
        else if constexpr (FixedSrc == 6)
            val = gpr.GetReg(state, 6);
        else if constexpr (FixedSrc == 7)
            val = gpr.GetReg(state, 7);
    } else {
        uint8_t src = (operand_modrm >> 3) & 7;
        val = gpr.GetReg(state, src);
    }

    // Write Destination (ModRM.RM)
    if constexpr (DstSpec >= RmOperandSpec::Reg0 && DstSpec <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedDst == 0)
            gpr.SetReg(state, 0, val);
        else if constexpr (FixedDst == 1)
            gpr.SetReg(state, 1, val);
        else if constexpr (FixedDst == 2)
            gpr.SetReg(state, 2, val);
        else if constexpr (FixedDst == 3)
            gpr.SetReg(state, 3, val);
        else if constexpr (FixedDst == 4)
            gpr.SetReg(state, 4, val);
        else if constexpr (FixedDst == 5)
            gpr.SetReg(state, 5, val);
        else if constexpr (FixedDst == 6)
            gpr.SetReg(state, 6, val);
        else if constexpr (FixedDst == 7)
            gpr.SetReg(state, 7, val);
    } else {
        uint8_t dst = operand_modrm & 7;
        gpr.SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <RmOperandSpec S = RmOperandSpec::Dynamic, AddrIndexSpec IndexSpec = AddrIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_EvGv_Mem_Internal(LogicFuncParams) {
    // RmOperandSpec: MOV [mem], r32
    // ModRM.Reg is the Source Register
    uint32_t val;
    if constexpr (S >= RmOperandSpec::Reg0 && S <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedReg == 0)
            val = gpr.GetReg(state, 0);
        else if constexpr (FixedReg == 1)
            val = gpr.GetReg(state, 1);
        else if constexpr (FixedReg == 2)
            val = gpr.GetReg(state, 2);
        else if constexpr (FixedReg == 3)
            val = gpr.GetReg(state, 3);
        else if constexpr (FixedReg == 4)
            val = gpr.GetReg(state, 4);
        else if constexpr (FixedReg == 5)
            val = gpr.GetReg(state, 5);
        else if constexpr (FixedReg == 6)
            val = gpr.GetReg(state, 6);
        else if constexpr (FixedReg == 7)
            val = gpr.GetReg(state, 7);
    } else {
        uint8_t reg = (operand_modrm >> 3) & 7;
        val = gpr.GetReg(state, reg);
    }

    uint32_t addr = ComputeLinearAddressSpecialized<IndexSpec, BaseRegSpec, SegmentSpec>(state, gpr, op);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, gpr, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <RmOperandSpec DstSpec = RmOperandSpec::Dynamic, RmOperandSpec SrcSpec = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_GvEv_Reg_Internal(LogicFuncParams) {
    // RmOperandSpec: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source
    if constexpr (SrcSpec >= RmOperandSpec::Reg0 && SrcSpec <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedSrc == 0)
            val = gpr.GetReg(state, 0);
        else if constexpr (FixedSrc == 1)
            val = gpr.GetReg(state, 1);
        else if constexpr (FixedSrc == 2)
            val = gpr.GetReg(state, 2);
        else if constexpr (FixedSrc == 3)
            val = gpr.GetReg(state, 3);
        else if constexpr (FixedSrc == 4)
            val = gpr.GetReg(state, 4);
        else if constexpr (FixedSrc == 5)
            val = gpr.GetReg(state, 5);
        else if constexpr (FixedSrc == 6)
            val = gpr.GetReg(state, 6);
        else if constexpr (FixedSrc == 7)
            val = gpr.GetReg(state, 7);
    } else {
        uint8_t src = operand_modrm & 7;
        val = gpr.GetReg(state, src);
    }

    // Write Destination
    if constexpr (DstSpec >= RmOperandSpec::Reg0 && DstSpec <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedDst == 0)
            gpr.SetReg(state, 0, val);
        else if constexpr (FixedDst == 1)
            gpr.SetReg(state, 1, val);
        else if constexpr (FixedDst == 2)
            gpr.SetReg(state, 2, val);
        else if constexpr (FixedDst == 3)
            gpr.SetReg(state, 3, val);
        else if constexpr (FixedDst == 4)
            gpr.SetReg(state, 4, val);
        else if constexpr (FixedDst == 5)
            gpr.SetReg(state, 5, val);
        else if constexpr (FixedDst == 6)
            gpr.SetReg(state, 6, val);
        else if constexpr (FixedDst == 7)
            gpr.SetReg(state, 7, val);
    } else {
        uint8_t dst = (operand_modrm >> 3) & 7;
        gpr.SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <RmOperandSpec S = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_OaMa_Internal(LogicFuncParams) {
    // A3: MOV m32, EAX
    uint32_t addr = ComputeLinearAddress(state, gpr, op);
    uint32_t val = gpr.GetReg(state, EAX);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, gpr, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <RmOperandSpec S = RmOperandSpec::Dynamic, AddrIndexSpec IndexSpec = AddrIndexSpec::Dynamic,
          AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_GvEv_Mem_Internal(LogicFuncParams) {
    // RmOperandSpec: MOV r32, [mem]
    uint32_t addr = ComputeLinearAddressSpecialized<IndexSpec, BaseRegSpec, SegmentSpec>(state, gpr, op);
    // Restart on TLB Miss
    auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t val = *val_res;

    if constexpr (S >= RmOperandSpec::Reg0 && S <= RmOperandSpec::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)RmOperandSpec::Reg0;
        if constexpr (FixedReg == 0)
            gpr.SetReg(state, 0, val);
        else if constexpr (FixedReg == 1)
            gpr.SetReg(state, 1, val);
        else if constexpr (FixedReg == 2)
            gpr.SetReg(state, 2, val);
        else if constexpr (FixedReg == 3)
            gpr.SetReg(state, 3, val);
        else if constexpr (FixedReg == 4)
            gpr.SetReg(state, 4, val);
        else if constexpr (FixedReg == 5)
            gpr.SetReg(state, 5, val);
        else if constexpr (FixedReg == 6)
            gpr.SetReg(state, 6, val);
        else if constexpr (FixedReg == 7)
            gpr.SetReg(state, 7, val);
    } else {
        uint8_t reg = (operand_modrm >> 3) & 7;
        gpr.SetReg(state, reg, val);
    }
    return LogicFlow::Continue;
}

namespace op {

template <RmOperandSpec Register>
FORCE_INLINE unsigned MovRegister(unsigned encoded) {
    if constexpr (Register == RmOperandSpec::Dynamic) return encoded;
    return static_cast<unsigned>(Register) - static_cast<unsigned>(RmOperandSpec::Reg0);
}

enum class MovDirection { ToRm, FromRm };

template <typename T, MovDirection Direction, AddrModeSpec Form = AddrModeSpec::Dynamic,
          RmOperandSpec Register = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovModRM(LogicFuncParams) {
    const unsigned reg = MovRegister<Register>((operand_modrm >> 3) & 7);
    if constexpr (Direction == MovDirection::ToRm) {
        const auto value = ReadRegGeneric<T>(state, gpr, reg);
        if (!WriteModRM<T, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, operand_modrm, value, utlb))
            return LogicFlow::RetryMemoryOp;
    } else {
        const auto value = ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb);
        if (!value) return LogicFlow::RestartMemoryOp;
        WriteRegGeneric<T>(state, gpr, reg, *value);
    }
    return LogicFlow::Continue;
}

template <MovDirection Direction, class Operands>
FORCE_INLINE LogicFlow OpMovWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpMovModRM<uint16_t, Direction>(LogicPassParams);
    return OpMovModRM<uint32_t, Direction>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_EvGv(LogicFuncParams) {
    return OpMovWithOperands<MovDirection::ToRm>(LogicPassParams, OperandSpec::Dynamic{});
}

// Named wrappers for OpMov_EvGv_Reg (0x89 Mod=3) - Specializing Source

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_0

FORCE_INLINE LogicFlow OpMov_EvGv_Reg(LogicFuncParams) { return OpMov_EvGv_Reg_Internal<>(LogicPassParams); }

// Named wrappers for OpMov_EvGv_Mem (Store) - Specializing Source

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_1

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_2
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_2

FORCE_INLINE LogicFlow OpMov_EvGv_Mem(LogicFuncParams) { return OpMov_EvGv_Mem_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpMov_GvEv(LogicFuncParams) {
    return OpMovWithOperands<MovDirection::FromRm>(LogicPassParams, OperandSpec::Dynamic{});
}

// Named wrappers for profiling visibility
#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_0

// Generic fallback
FORCE_INLINE LogicFlow OpMov_GvEv_Reg(LogicFuncParams) { return OpMov_GvEv_Reg_Internal<>(LogicPassParams); }

// Named wrappers for OpMov_GvEv_Mem (Load) - Specializing Destination

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_3
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_3

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_4
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_4

FORCE_INLINE LogicFlow OpMov_GvEv_Mem(LogicFuncParams) { return OpMov_GvEv_Mem_Internal<>(LogicPassParams); }

template <typename T, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXchgModRM(LogicFuncParams) {
    // A blocking store ensures the register is updated only after the store succeeds.
    constexpr auto read_policy = sizeof(T) == 1 ? OpOnTLBMiss::Restart : OpOnTLBMiss::Blocking;
    const ModRMRmwOperand<Form> operand(state, gpr, op, operand_modrm);
    const auto rm_value = operand.template Read<T, read_policy>(state, gpr, op, utlb);
    if (!rm_value) {
        if constexpr (read_policy == OpOnTLBMiss::Restart) return LogicFlow::RestartMemoryOp;
        return LogicFlow::ExitOnCurrentEIP;
    }

    const unsigned reg = (operand_modrm >> 3) & 7;
    const T reg_value = ReadRegGeneric<T>(state, gpr, reg);
    if (!operand.template Write<T, OpOnTLBMiss::Blocking>(state, gpr, op, reg_value, utlb))
        return LogicFlow::ExitOnCurrentEIP;
    WriteRegGeneric<T>(state, gpr, reg, *rm_value);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_EbGb(LogicFuncParams) { return OpXchgModRM<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpMov_EbGb(LogicFuncParams) { return OpMovModRM<uint8_t, MovDirection::ToRm>(LogicPassParams); }

FORCE_INLINE LogicFlow OpMov_GbEb(LogicFuncParams) {
    return OpMovModRM<uint8_t, MovDirection::FromRm>(LogicPassParams);
}

template <typename T, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovImmediateModRM(LogicFuncParams) {
    if (!WriteModRM<T, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, operand_modrm, static_cast<T>(imm), utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EbIb(LogicFuncParams) { return OpMovImmediateModRM<uint8_t>(LogicPassParams); }

template <AddrIndexSpec IndexSpec = AddrIndexSpec::Dynamic, AddrBaseRegSpec BaseRegSpec = AddrBaseRegSpec::Dynamic,
          AddrSegmentSpec SegmentSpec = AddrSegmentSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_EvIz_Mem_Internal(LogicFuncParams) {
    const uint32_t addr = ComputeLinearAddressSpecialized<IndexSpec, BaseRegSpec, SegmentSpec>(state, gpr, op);
    if (GetPrefixes(op).flags.opsize) {
        if (!WriteMem<uint16_t, OpOnTLBMiss::Retry>(state, gpr, addr, (uint16_t)imm, utlb, op))
            return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, gpr, addr, (uint32_t)imm, utlb, op))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <class Operands>
FORCE_INLINE LogicFlow OpMovImmediateRmWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpMovImmediateModRM<uint16_t>(LogicPassParams);
    return OpMovImmediateModRM<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_EvIz(LogicFuncParams) {
    return OpMovImmediateRmWithOperands(LogicPassParams, OperandSpec::Dynamic{});
}

#define FIBERCPU_BIND_OPS_DATA_MOV_IMMEDIATE_ADDRESS
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMMEDIATE_ADDRESS

template <typename T, RmOperandSpec Register = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovImmediateRegister(LogicFuncParams) {
    const unsigned reg = MovRegister<Register>(operand_modrm & 7);
    WriteRegGeneric<T>(state, gpr, reg, static_cast<T>(imm));
    return LogicFlow::Continue;
}

template <class Operands>
FORCE_INLINE LogicFlow OpMovImmediateRegWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpMovImmediateRegister<uint16_t>(LogicPassParams);
    return OpMovImmediateRegister<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_RegImm(LogicFuncParams) {
    return OpMovImmediateRegWithOperands(LogicPassParams, OperandSpec::Dynamic{});
}

FORCE_INLINE LogicFlow OpMov_RegImm8(LogicFuncParams) { return OpMovImmediateRegister<uint8_t>(LogicPassParams); }

template <typename T, MovDirection Direction>
FORCE_INLINE LogicFlow OpMovMoffs(LogicFuncParams) {
    const uint32_t linear = imm + GetSegmentBase(state, op);
    if constexpr (Direction == MovDirection::ToRm) {
        const T value = ReadRegGeneric<T>(state, gpr, EAX);
        if (!WriteMem<T, OpOnTLBMiss::Retry>(state, gpr, linear, value, utlb, op)) return LogicFlow::RetryMemoryOp;
    } else {
        const auto value = ReadMem<T, OpOnTLBMiss::Restart>(state, gpr, linear, utlb, op);
        if (!value) return LogicFlow::RestartMemoryOp;
        WriteRegGeneric<T>(state, gpr, EAX, *value);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Byte(LogicFuncParams) {
    return OpMovMoffs<uint8_t, MovDirection::FromRm>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpMovMoffs<uint16_t, MovDirection::FromRm>(LogicPassParams);
    return OpMovMoffs<uint32_t, MovDirection::FromRm>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Byte(LogicFuncParams) {
    return OpMovMoffs<uint8_t, MovDirection::ToRm>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpMovMoffs<uint16_t, MovDirection::ToRm>(LogicPassParams);
    return OpMovMoffs<uint32_t, MovDirection::ToRm>(LogicPassParams);
}

template <AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_Rm_Sreg_Internal(LogicFuncParams) {
    // The memory destination is always 16 bits; only a register destination is zero-extended.
    if (OperandIsReg<Form>(operand_modrm)) {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, operand_modrm, 0, utlb))
            return LogicFlow::RetryMemoryOp;
    } else if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, operand_modrm, 0, utlb)) {
        return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Rm_Sreg(LogicFuncParams) { return OpMov_Rm_Sreg_Internal<>(LogicPassParams); }

template <AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMov_Sreg_Rm_Internal(LogicFuncParams) {
    // Even though the emulator ignores the selector, the source read can fault.
    if (!ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb))
        return LogicFlow::RestartMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Sreg_Rm(LogicFuncParams) { return OpMov_Sreg_Rm_Internal<>(LogicPassParams); }

template <typename Source, typename Destination, AddrModeSpec Form = AddrModeSpec::Dynamic,
          RmOperandSpec Register = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovExtend(LogicFuncParams) {
    using ReadType = std::make_unsigned_t<Source>;
    const auto value = ReadModRM<ReadType, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb);
    if (!value) return LogicFlow::RestartMemoryOp;
    const unsigned reg = MovRegister<Register>((operand_modrm >> 3) & 7);
    WriteRegGeneric<Destination>(state, gpr, reg, static_cast<Destination>(static_cast<Source>(*value)));
    return LogicFlow::Continue;
}

template <typename Source, RmOperandSpec Register, OpsizeSpec Size, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovExtendWidth(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<Size>(op, operands))
        return OpMovExtend<Source, uint16_t, AddrModeSpec::Dynamic, Register>(LogicPassParams);
    return OpMovExtend<Source, uint32_t, AddrModeSpec::Dynamic, Register>(LogicPassParams);
}

template <RmOperandSpec RegSpec = RmOperandSpec::Dynamic, OpsizeSpec SizeSpec = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovzx_Byte_Impl(LogicFuncParams) {
    return OpMovExtendWidth<uint8_t, RegSpec, SizeSpec>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMovzx_Byte(LogicFuncParams) { return OpMovzx_Byte_Impl<>(LogicPassParams); }
#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_1

FORCE_INLINE LogicFlow OpMovzx_Word(LogicFuncParams) {
    return OpMovExtendWidth<uint16_t, RmOperandSpec::Dynamic, OpsizeSpec::Dynamic>(LogicPassParams);
}

template <RmOperandSpec RegSpec = RmOperandSpec::Dynamic, OpsizeSpec SizeSpec = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovsx_Byte_Impl(LogicFuncParams) {
    return OpMovExtendWidth<int8_t, RegSpec, SizeSpec>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMovsx_Byte(LogicFuncParams) { return OpMovsx_Byte_Impl<>(LogicPassParams); }
#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_2
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_2

template <RmOperandSpec RegSpec = RmOperandSpec::Dynamic, OpsizeSpec SizeSpec = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovsx_Word_Impl(LogicFuncParams) {
    return OpMovExtendWidth<int16_t, RegSpec, SizeSpec>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpMovsx_Word(LogicFuncParams) { return OpMovsx_Word_Impl<>(LogicPassParams); }

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_5
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_5

template <OpsizeSpec SizeSpec, RmOperandSpec RegSpec = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpLea_Impl(LogicFuncParams) {
    uint32_t offset = ComputeEA(state, gpr, op);

    if (OperandIs16<SizeSpec>(op)) {
        if constexpr (RegSpec >= RmOperandSpec::Reg0 && RegSpec <= RmOperandSpec::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)RmOperandSpec::Reg0;
            uint32_t current = gpr.GetReg(state, FixedReg);
            gpr.SetReg(state, FixedReg, (current & 0xFFFF0000) | (offset & 0xFFFF));
        } else {
            uint8_t reg = (operand_modrm >> 3) & 7;
            gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | (offset & 0xFFFF));
        }
    } else {
        if constexpr (RegSpec >= RmOperandSpec::Reg0 && RegSpec <= RmOperandSpec::Reg7) {
            constexpr uint8_t FixedReg = (uint8_t)RegSpec - (uint8_t)RmOperandSpec::Reg0;
            gpr.SetReg(state, FixedReg, offset);
        } else {
            uint8_t reg = (operand_modrm >> 3) & 7;
            gpr.SetReg(state, reg, offset);
        }
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_3
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_3

template <class Operands>
FORCE_INLINE LogicFlow OpLeaWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpLea_16(LogicPassParams);
    return OpLea_32(LogicPassParams);
}

FORCE_INLINE LogicFlow OpLea(LogicFuncParams) { return OpLeaWithOperands(LogicPassParams, OperandSpec::Dynamic{}); }

// ------------------------------------------------------------------------------------------------
// Stack Operations (Simple)
// ------------------------------------------------------------------------------------------------

template <RmOperandSpec RegSpec, OpsizeSpec SizeSpec>
FORCE_INLINE LogicFlow OpPush_Reg_Internal(LogicFuncParams) {
    constexpr uint8_t FixedReg = static_cast<uint8_t>(RegSpec) - static_cast<uint8_t>(RmOperandSpec::Reg0);
    if (OperandIs16<SizeSpec>(op)) {
        if (!Push<uint16_t, true>(state, gpr, static_cast<uint16_t>(gpr.GetReg(state, FixedReg)), utlb, op))
            return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, gpr, gpr.GetReg(state, FixedReg), utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

template <RmOperandSpec RegSpec, OpsizeSpec SizeSpec>
FORCE_INLINE LogicFlow OpPop_Reg_Internal(LogicFuncParams) {
    constexpr uint8_t FixedReg = static_cast<uint8_t>(RegSpec) - static_cast<uint8_t>(RmOperandSpec::Reg0);
    if (OperandIs16<SizeSpec>(op)) {
        auto val_res = Pop<uint16_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        gpr.SetReg(state, FixedReg, (gpr.GetReg(state, FixedReg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop<uint32_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        gpr.SetReg(state, FixedReg, *val_res);
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_6
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_DATA_MOV_IMPL_MACRO_6

FORCE_INLINE LogicFlow OpPush_Reg(LogicFuncParams) {
    // Generic fallback; opcode-specialized handlers should handle the hot path.
    uint8_t reg = operand_modrm & 7;
    if (GetPrefixes(op).flags.opsize) {
        if (!Push<uint16_t, true>(state, gpr, static_cast<uint16_t>(gpr.GetReg(state, reg)), utlb, op))
            return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, gpr, gpr.GetReg(state, reg), utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

enum class PushImmediate { FullWidth, SignExtendedByte };

template <typename T, PushImmediate Kind>
FORCE_INLINE LogicFlow OpPushImmediate(LogicFuncParams) {
    T value = static_cast<T>(imm);
    if constexpr (Kind == PushImmediate::SignExtendedByte) value = static_cast<T>(static_cast<int8_t>(imm));
    if (!Push<T, true>(state, gpr, value, utlb, op)) return LogicFlow::RestartMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPush_Imm32(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op))
        return OpPushImmediate<uint16_t, PushImmediate::FullWidth>(LogicPassParams);
    return OpPushImmediate<uint32_t, PushImmediate::FullWidth>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpPush_Imm8(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op))
        return OpPushImmediate<uint16_t, PushImmediate::SignExtendedByte>(LogicPassParams);
    return OpPushImmediate<uint32_t, PushImmediate::SignExtendedByte>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpPop_Reg(LogicFuncParams) {
    // Generic fallback; opcode-specialized handlers should handle the hot path.
    uint8_t reg = operand_modrm & 7;
    if (GetPrefixes(op).flags.opsize) {
        auto val_res = Pop<uint16_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop<uint32_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        gpr.SetReg(state, reg, *val_res);
    }
    return LogicFlow::Continue;
}

template <typename T, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpPopModRM(LogicFuncParams) {
    const auto value = Pop<T, true>(state, gpr, utlb, op);
    if (!value) return LogicFlow::RestartMemoryOp;
    // Resolve the destination after the stack increment, including POP [ESP].
    if (!WriteModRM<T, OpOnTLBMiss::Retry, 1, Form>(state, gpr, op, operand_modrm, *value, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPop_Ev(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpPopModRM<uint16_t>(LogicPassParams);
    return OpPopModRM<uint32_t>(LogicPassParams);
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Complex) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

template <typename T>
FORCE_INLINE LogicFlow PushaWithWidth(LogicFuncParams) {
    const uint32_t sp = gpr.GetReg(state, ESP);
    const uint32_t ss = state->ctx.seg_base[3];
    const uint32_t values[] = {
        gpr.GetReg(state, EAX), gpr.GetReg(state, ECX), gpr.GetReg(state, EDX), gpr.GetReg(state, EBX), sp,
        gpr.GetReg(state, EBP), gpr.GetReg(state, ESI), gpr.GetReg(state, EDI)};
    for (uint32_t i = 0; i < 8; ++i) {
        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + sp - (i + 1) * sizeof(T), static_cast<T>(values[i]),
                                                utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
    }
    gpr.SetReg(state, ESP, sp - 8 * sizeof(T));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPusha(LogicFuncParams) {
    return GetPrefixes(op).flags.opsize ? PushaWithWidth<uint16_t>(LogicPassParams)
                                        : PushaWithWidth<uint32_t>(LogicPassParams);
}

template <typename T>
FORCE_INLINE LogicFlow PopaWithWidth(LogicFuncParams) {
    const uint32_t sp = gpr.GetReg(state, ESP);
    const uint32_t ss = state->ctx.seg_base[3];
    constexpr uint8_t regs[] = {EDI, ESI, EBP, ESP, EBX, EDX, ECX, EAX};
    T values[8]{};
    for (uint32_t i = 0; i < 8; ++i) {
        if (i == 3) continue;  // The saved SP slot is not read or restored.
        auto value = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + sp + i * sizeof(T), utlb, op);
        if (!value) return LogicFlow::ExitOnCurrentEIP;
        values[i] = *value;
    }
    for (uint32_t i = 0; i < 8; ++i) {
        if (i != 3) WriteRegGeneric<T>(state, gpr, regs[i], values[i]);
    }
    gpr.SetReg(state, ESP, sp + 8 * sizeof(T));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPopa(LogicFuncParams) {
    return GetPrefixes(op).flags.opsize ? PopaWithWidth<uint16_t>(LogicPassParams)
                                        : PopaWithWidth<uint32_t>(LogicPassParams);
}

template <typename T>
FORCE_INLINE LogicFlow EnterWithWidth(LogicFuncParams) {
    const uint16_t size = static_cast<uint16_t>(imm);
    const uint8_t level = (imm >> 16) & 31;
    const uint32_t ss = state->ctx.seg_base[3];
    const uint32_t sp = gpr.GetReg(state, ESP);
    const uint32_t bp = gpr.GetReg(state, EBP);
    uint32_t cursor = sp - sizeof(T);
    if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + cursor, static_cast<T>(bp), utlb, op))
        return LogicFlow::ExitOnCurrentEIP;
    const uint32_t frame = cursor;
    uint32_t previous = static_cast<T>(bp);
    if (level != 0) {
        for (uint8_t i = 1; i < level; ++i) {
            previous = static_cast<T>(previous - sizeof(T));
            auto value = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + previous, utlb, op);
            if (!value) return LogicFlow::ExitOnCurrentEIP;
            cursor -= sizeof(T);
            if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + cursor, *value, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
        }
        cursor -= sizeof(T);
        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, ss + cursor, static_cast<T>(frame), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
    }
    WriteRegGeneric<T>(state, gpr, EBP, static_cast<T>(frame));
    gpr.SetReg(state, ESP, cursor - size);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpEnter(LogicFuncParams) {
    return GetPrefixes(op).flags.opsize ? EnterWithWidth<uint16_t>(LogicPassParams)
                                        : EnterWithWidth<uint32_t>(LogicPassParams);
}

template <typename T>
FORCE_INLINE LogicFlow OpLeaveWidth(LogicFuncParams) {
    const uint32_t ebp = gpr.GetReg(state, EBP);
    const uint32_t address = state->ctx.seg_base[3] + ebp;
    const auto value = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, address, utlb, op);
    if (!value) return LogicFlow::ExitOnCurrentEIP;
    gpr.SetReg(state, ESP, ebp + sizeof(T));
    WriteRegGeneric<T>(state, gpr, EBP, *value);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLeave(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpLeaveWidth<uint16_t>(LogicPassParams);
    return OpLeaveWidth<uint32_t>(LogicPassParams);
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------
FORCE_INLINE LogicFlow OpLahf(LogicFuncParams) {
    // 9F: LAHF
    uint8_t ah = 0x02;
    if (ReadSF(flags_cache)) ah |= SF_MASK;
    if (ReadZF(flags_cache)) ah |= ZF_MASK;
    if (EvaluatePF(flags_cache)) ah |= PF_MASK;
    if (ReadAF(flags_cache)) ah |= AF_MASK;
    if (ReadCF(flags_cache)) ah |= CF_MASK;
    uint32_t eax = gpr.GetReg(state, EAX);
    eax = (eax & 0xFFFF00FF) | (ah << 8);
    gpr.SetReg(state, EAX, eax);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSahf(LogicFuncParams) {
    // 9E: SAHF
    uint32_t eax = gpr.GetReg(state, EAX);
    uint8_t ah = (eax >> 8) & 0xFF;
    uint32_t flags = GetFlags32NoPF(flags_cache);
    flags = (flags & ~0xFF) | (ah & 0xD5) | 0x02;
    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

template <typename T, RmOperandSpec Register = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpXchgRegister(LogicFuncParams) {
    const unsigned reg = MovRegister<Register>(operand_modrm & 7);
    if (reg == EAX) return LogicFlow::Continue;
    const T accum = ReadRegGeneric<T>(state, gpr, EAX);
    const T value = ReadRegGeneric<T>(state, gpr, reg);
    WriteRegGeneric<T>(state, gpr, EAX, value);
    WriteRegGeneric<T>(state, gpr, reg, accum);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_Reg(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpXchgRegister<uint16_t>(LogicPassParams);
    return OpXchgRegister<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpXchg_EvGv(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpXchgModRM<uint16_t>(LogicPassParams);
    return OpXchgModRM<uint32_t>(LogicPassParams);
}

// ------------------------------------------------------------------------------------------------
// String Operations (LODS, SCAS, CMPS) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

// Address-size override (0x67) selects SI/DI/CX instead of ESI/EDI/ECX and
// wraps offsets at 0xFFFF while preserving the upper halves. String
// destinations always use ES, independent of any segment override.
FORCE_INLINE uint32_t GetStringOffset(EmuState* state, GprCache& gpr, int reg, bool addr16) {
    const uint32_t value = gpr.GetReg(state, reg);
    return addr16 ? (value & 0xFFFFu) : value;
}

FORCE_INLINE void SetStringOffset(EmuState* state, GprCache& gpr, int reg, uint32_t value, bool addr16) {
    if (addr16) {
        const uint32_t current = gpr.GetReg(state, reg);
        gpr.SetReg(state, reg, (current & 0xFFFF0000u) | (value & 0xFFFFu));
    } else {
        gpr.SetReg(state, reg, value);
    }
}

FORCE_INLINE uint32_t GetStringCount(EmuState* state, GprCache& gpr, bool addr16) {
    const uint32_t value = gpr.GetReg(state, ECX);
    return addr16 ? (value & 0xFFFFu) : value;
}

FORCE_INLINE void SetStringCount(EmuState* state, GprCache& gpr, uint32_t value, bool addr16) {
    if (addr16) {
        const uint32_t current = gpr.GetReg(state, ECX);
        gpr.SetReg(state, ECX, (current & 0xFFFF0000u) | (value & 0xFFFFu));
    } else {
        gpr.SetReg(state, ECX, value);
    }
}

FORCE_INLINE uint32_t StringDestinationAddress(EmuState* state, uint32_t offset) {
    return offset + state->ctx.seg_base[1];  // ES
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE bool Helper_Movs(LogicFuncParams) {
    const bool addr16 = AddressIs16<AddressSize>(op);
    const bool df = TestFlagBits(flags_cache, 0x400);  // DF
    const int32_t step = df ? -static_cast<int32_t>(sizeof(T)) : static_cast<int32_t>(sizeof(T));

    auto perform = [&]() -> bool {
        const uint32_t esi = GetStringOffset(state, gpr, ESI, addr16);
        const uint32_t edi = GetStringOffset(state, gpr, EDI, addr16);

        const uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, src_addr, utlb, op);
        if (!val_res) return false;  // Fault
        T val = *val_res;

        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, StringDestinationAddress(state, edi), val, utlb, op))
            return false;  // Fault

        SetStringOffset(state, gpr, ESI, esi + step, addr16);
        SetStringOffset(state, gpr, EDI, edi + step, addr16);
        return true;
    };

    if (GetPrefixes(op).flags.rep) {
        while (GetStringCount(state, gpr, addr16) > 0) {
            if (!perform()) return false;

            if (state->status != EmuStatus::Running) return false;
            SetStringCount(state, gpr, GetStringCount(state, gpr, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpMovsWithShape(LogicFuncParams) {
    if (!Helper_Movs<T, AddressSize>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovs_Byte(LogicFuncParams) { return OpMovsWithShape<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpMovs_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpMovsWithShape<uint16_t>(LogicPassParams);
    return OpMovsWithShape<uint32_t>(LogicPassParams);
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpStosWithShape(LogicFuncParams) {
    const bool addr16 = AddressIs16<AddressSize>(op);
    const bool df = TestFlagBits(flags_cache, 0x400);
    const int32_t step = df ? -static_cast<int32_t>(sizeof(T)) : static_cast<int32_t>(sizeof(T));
    const auto perform = [&] {
        const uint32_t edi = GetStringOffset(state, gpr, EDI, addr16);
        const T value = static_cast<T>(gpr.GetReg(state, EAX));
        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, gpr, StringDestinationAddress(state, edi), value, utlb, op))
            return false;
        SetStringOffset(state, gpr, EDI, edi + step, addr16);
        return true;
    };
    if (GetPrefixes(op).flags.rep) {
        while (GetStringCount(state, gpr, addr16) > 0) {
            if (!perform()) return LogicFlow::ExitOnCurrentEIP;
            if (state->status != EmuStatus::Running) break;
            SetStringCount(state, gpr, GetStringCount(state, gpr, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStos_Byte(LogicFuncParams) { return OpStosWithShape<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpStos_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpStosWithShape<uint16_t>(LogicPassParams);
    return OpStosWithShape<uint32_t>(LogicPassParams);
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE bool Helper_Lods(LogicFuncParams) {
    const bool addr16 = AddressIs16<AddressSize>(op);
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t esi = GetStringOffset(state, gpr, ESI, addr16);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, src_addr, utlb, op);
        if (!val_res) return false;
        T val = *val_res;

        if constexpr (sizeof(T) == 1) {
            gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFFFF00) | val);
        } else if constexpr (sizeof(T) == 2) {
            gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | val);
        } else {
            gpr.SetReg(state, EAX, val);
        }

        SetStringOffset(state, gpr, ESI, esi + step, addr16);
        return true;
    };

    if (GetPrefixes(op).flags.rep) {
        while (GetStringCount(state, gpr, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;
            SetStringCount(state, gpr, GetStringCount(state, gpr, addr16) - 1, addr16);
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpLodsWithShape(LogicFuncParams) {
    if (!Helper_Lods<T, AddressSize>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLods_Byte(LogicFuncParams) { return OpLodsWithShape<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpLods_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpLodsWithShape<uint16_t>(LogicPassParams);
    return OpLodsWithShape<uint32_t>(LogicPassParams);
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE bool Helper_Scas(LogicFuncParams) {
    const bool addr16 = AddressIs16<AddressSize>(op);
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t edi = GetStringOffset(state, gpr, EDI, addr16);
        // ES:[EDI]
        auto mem_val_res =
            ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, StringDestinationAddress(state, edi), utlb, op);
        if (!mem_val_res) return false;
        T mem_val = *mem_val_res;

        T acc;
        if constexpr (sizeof(T) == 1)
            acc = (T)gpr.GetReg(state, EAX);
        else if constexpr (sizeof(T) == 2)
            acc = (T)(gpr.GetReg(state, EAX) & 0xFFFF);
        else
            acc = (T)gpr.GetReg(state, EAX);

        AluSub<T>(state, flags_cache, acc, mem_val);  // CMP uses Sub.

        SetStringOffset(state, gpr, EDI, edi + step, addr16);
        return true;
    };

    if (GetPrefixes(op).flags.rep || GetPrefixes(op).flags.repne) {
        while (GetStringCount(state, gpr, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            SetStringCount(state, gpr, GetStringCount(state, gpr, addr16) - 1, addr16);

            bool zf = TestFlagBits(flags_cache, ZF_MASK);
            if (GetPrefixes(op).flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (GetPrefixes(op).flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpScasWithShape(LogicFuncParams) {
    if (!Helper_Scas<T, AddressSize>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpScas_Byte(LogicFuncParams) { return OpScasWithShape<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpScas_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpScasWithShape<uint16_t>(LogicPassParams);
    return OpScasWithShape<uint32_t>(LogicPassParams);
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE bool Helper_Cmps(LogicFuncParams) {
    const bool addr16 = AddressIs16<AddressSize>(op);
    bool df = TestFlagBits(flags_cache, 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t esi = GetStringOffset(state, gpr, ESI, addr16);
        uint32_t edi = GetStringOffset(state, gpr, EDI, addr16);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto src_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, src_addr, utlb, op);
        if (!src_val_res) return false;
        T src_val = *src_val_res;

        auto dst_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, gpr, StringDestinationAddress(state, edi), utlb,
                                                             op);  // ES:EDI
        if (!dst_val_res) return false;
        T dst_val = *dst_val_res;

        AluSub<T>(state, flags_cache, src_val, dst_val);

        SetStringOffset(state, gpr, ESI, esi + step, addr16);
        SetStringOffset(state, gpr, EDI, edi + step, addr16);
        return true;
    };

    if (GetPrefixes(op).flags.rep || GetPrefixes(op).flags.repne) {
        while (GetStringCount(state, gpr, addr16) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            SetStringCount(state, gpr, GetStringCount(state, gpr, addr16) - 1, addr16);

            bool zf = TestFlagBits(flags_cache, ZF_MASK);
            if (GetPrefixes(op).flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (GetPrefixes(op).flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

template <typename T, AddrsizeSpec AddressSize = AddrsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmpsWithShape(LogicFuncParams) {
    if (!Helper_Cmps<T, AddressSize>(LogicPassParams)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmps_Byte(LogicFuncParams) { return OpCmpsWithShape<uint8_t>(LogicPassParams); }

FORCE_INLINE LogicFlow OpCmps_Word(LogicFuncParams) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op)) return OpCmpsWithShape<uint16_t>(LogicPassParams);
    return OpCmpsWithShape<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpXlat(LogicFuncParams) {
    // D7: XLAT/XLATB
    // AL = [DS:EBX + AL]
    uint32_t ebx = gpr.GetReg(state, EBX);
    uint32_t al = gpr.GetReg(state, EAX) & 0xFF;
    // Segment override is possible, but default is DS.
    // op->mem.base_offset is not set up for this specific instruction by decoder usually,
    // so we rely on GetSegmentBase(state, op) and manual calculation.
    // XLAT implies DS unless overridden.
    uint32_t addr = ebx + al + GetSegmentBase(state, op);

    auto val_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;

    uint32_t eax = gpr.GetReg(state, EAX);
    eax = (eax & 0xFFFFFF00) | *val_res;
    gpr.SetReg(state, EAX, eax);
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_MOVE_SPECIALIZATIONS
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_MOVE_SPECIALIZATIONS

}  // namespace op

}  // namespace fiberish
