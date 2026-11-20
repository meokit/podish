#pragma once
// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../ops_helpers_template.h"
#include "../state.h"

namespace fiberish {

// =========================================================================================
// Group 1: 0x80 (Eb,Ib), 0x81 (Ev,Iz), 0x82 (Eb,Ib - alias), 0x83 (Ev,Ib)
// =========================================================================================

enum class Group1Immediate { Byte, OperandWidth, SignExtendedByte };

template <typename T, Group1Immediate Immediate, FlagsSpec Flags, GroupOpSpec SubOp, RmOperandSpec Rm>
FORCE_INLINE LogicFlow OpGroup1_T_Internal(LogicFuncParams) {
    constexpr auto form = Rm == RmOperandSpec::Dynamic ? AddrModeSpec::Dynamic : AddrModeSpec::ModReg;
    const bool register_operand = OperandIsReg<form>(operand_modrm);
    const uint8_t rm = Rm >= RmOperandSpec::Reg0 && Rm <= RmOperandSpec::Reg7
                           ? uint8_t(Rm) - uint8_t(RmOperandSpec::Reg0)
                           : operand_modrm & 7;
    T dest;
    uint32_t address = 0;
    if (register_operand) {
        dest = ReadRegGeneric<T>(state, gpr, rm);
    } else {
        // The read either completes or restarts the instruction. Reuse the
        // address for writeback once the read has completed.
        address = ComputeLinearAddress(state, gpr, op);
        const auto res = ReadMem<T, OpOnTLBMiss::Restart>(state, gpr, address, utlb, op);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest = *res;
    }

    auto src = static_cast<T>(imm);
    if constexpr (Immediate == Group1Immediate::SignExtendedByte) src = static_cast<T>(static_cast<int8_t>(imm));

    return Helper_Group1<T, Flags != FlagsSpec::Dead, SubOp>(state, gpr, op, operand_modrm, flags_cache, dest, src,
                                                             utlb, register_operand, rm, address);
}

template <Group1Immediate Immediate, FlagsSpec Flags = FlagsSpec::Dynamic, GroupOpSpec SubOp = GroupOpSpec::Dynamic,
          OpsizeSpec Size = OpsizeSpec::Dynamic, RmOperandSpec Rm = RmOperandSpec::Dynamic,
          class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup1_Internal(LogicFuncParams, const Operands& operands = {}) {
    if constexpr (Immediate == Group1Immediate::Byte) {
        static_assert(Size == OpsizeSpec::Dynamic);
        return OpGroup1_T_Internal<uint8_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op, operands))
            return OpGroup1_T_Internal<uint16_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
        return OpGroup1_T_Internal<uint32_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
    }
}

// =========================================================================================
// Group 3: 0xF6 (Eb), 0xF7 (Ev)
// =========================================================================================

template <typename T, bool UpdateFlags, GroupOpSpec SubOp = GroupOpSpec::Dynamic,
          AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup3_Ev_T_Internal(LogicFuncParams) {
    const ModRMRmwOperand<Form> operand(state, gpr, op, operand_modrm);
    const auto value = operand.template Read<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!value) return LogicFlow::RestartMemoryOp;
    return Helper_Group3<T, UpdateFlags, SubOp>(state, gpr, op, operand_modrm, operand, flags_cache, *value, utlb, imm);
}

template <bool UpdateFlags, GroupOpSpec SubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup3_Eb_Internal(LogicFuncParams) {
    return OpGroup3_Ev_T_Internal<uint8_t, UpdateFlags, SubOp>(LogicPassParams);
}

template <bool Byte, GroupOpSpec SubOp = GroupOpSpec::Dynamic, OpsizeSpec Size = OpsizeSpec::Dynamic,
          AddrModeSpec Form = AddrModeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic,
          class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup3_Internal(LogicFuncParams, const Operands& operands = {}) {
    constexpr bool update_flags = Flags != FlagsSpec::Dead;
    if constexpr (Byte) {
        return OpGroup3_Ev_T_Internal<uint8_t, update_flags, SubOp, Form>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op, operands))
            return OpGroup3_Ev_T_Internal<uint16_t, update_flags, SubOp, Form>(LogicPassParams);
        return OpGroup3_Ev_T_Internal<uint32_t, update_flags, SubOp, Form>(LogicPassParams);
    }
}

// =========================================================================================
// Group 4: 0xFE (Eb) - INC/DEC
// =========================================================================================

template <bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup4_Eb_Internal(LogicFuncParams) {
    // FE
    const ModRMRmwOperand<> operand(state, gpr, op, operand_modrm);
    const auto val_res = operand.Read<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    return Helper_Group4<uint8_t, UpdateFlags, FixedSubOp>(state, gpr, op, operand_modrm, operand, flags_cache,
                                                           *val_res, utlb);
}

// =========================================================================================
// Group 5: 0xFF (Ev)
// =========================================================================================

template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup5_Ev_T_Internal(LogicFuncParams) {
    const ModRMRmwOperand<> operand(state, gpr, op, operand_modrm);
    const auto value = operand.Read<T, OpOnTLBMiss::Blocking>(state, gpr, op, utlb);
    if (!value) return LogicFlow::ExitOnCurrentEIP;
    return Helper_Group5<T, UpdateFlags, FixedSubOp>(state, gpr, op, operand_modrm, operand, flags_cache, *value, utlb,
                                                     branch);
}

template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp>
FORCE_INLINE LogicFlow OpGroup5_Ev_Control_Internal(LogicFuncParams) {
    static_assert(FixedSubOp == GroupOpSpec::Ext2 || FixedSubOp == GroupOpSpec::Ext4 ||
                  FixedSubOp == GroupOpSpec::Ext6);
    return OpGroup5_Ev_T_Internal<T, UpdateFlags, FixedSubOp>(LogicPassParams);
}

template <typename T, AddrModeSpec Form = AddrModeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpXadd_T_Internal(LogicFuncParams) {
    const ModRMRmwOperand<Form> operand(state, gpr, op, operand_modrm);
    const auto destination = operand.template Read<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!destination) return LogicFlow::RestartMemoryOp;

    const uint8_t reg = (operand_modrm >> 3) & 7;
    const T source = ReadRegGeneric<T>(state, gpr, reg);
    // Commit flags only after the write succeeds; the source can participate
    // in the destination address and must not be modified before that write.
    uint64_t updated_flags = flags_cache;
    const T result = AluAdd<T, Flags != FlagsSpec::Dead>(state, updated_flags, *destination, source);
    if (!operand.template Write<T, OpOnTLBMiss::Blocking>(state, gpr, op, result, utlb))
        return LogicFlow::ExitOnCurrentEIP;
    flags_cache = updated_flags;
    WriteRegGeneric<T>(state, gpr, reg, *destination);
    return LogicFlow::Continue;
}

template <bool Byte, OpsizeSpec Size = OpsizeSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          FlagsSpec Flags = FlagsSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpXadd_Internal(LogicFuncParams, const Operands& operands = {}) {
    if constexpr (Byte) {
        return OpXadd_T_Internal<uint8_t, Form, Flags>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op, operands)) return OpXadd_T_Internal<uint16_t, Form, Flags>(LogicPassParams);
        return OpXadd_T_Internal<uint32_t, Form, Flags>(LogicPassParams);
    }
}

namespace op {

FORCE_INLINE LogicFlow OpGroup3_Ev_Generic(LogicFuncParams) { return OpGroup3_Internal<false>(LogicPassParams); }

template <class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup5_Ev_Internal(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpGroup5_Ev_T_Internal<uint16_t, true>(LogicPassParams);
    return OpGroup5_Ev_T_Internal<uint32_t, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup5_Ev_Generic(LogicFuncParams) { return OpGroup5_Ev_Internal(LogicPassParams); }

#define FIBERCPU_BIND_OPS_GROUPS_IMPL_0
#define FIBERCPU_BIND_GROUP1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_GROUP1
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_0

// For Group 3 and 5, which use distinct templates without IsImm8

// Group 3
#define FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_3
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_3

// Group 3 Eb

#define FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_4
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_4

// Group 4 Eb

#define FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_5
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_MACRO_5

// Misc Ops (unchanged)
FORCE_INLINE LogicFlow OpPrefetch(LogicFuncParams) {
    // 0F 18 /r: PREFETCHh (T0, T1, T2, NTA)
    // These are hints for the processor about future memory access.
    // In our emulator, they are pure NOPs. They never fault even if address is invalid.
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCdq(LogicFuncParams) {
    uint32_t eax = gpr.GetReg(state, EAX);
    uint32_t edx = ((int32_t)eax < 0) ? 0xFFFFFFFF : 0;
    gpr.SetReg(state, EDX, edx);
    return LogicFlow::Continue;
}

template <OpsizeSpec Size = OpsizeSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpCwde_Internal(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<Size>(op, operands)) {
        int8_t val = (int8_t)gpr.GetReg(state, EAX);
        uint32_t current = gpr.GetReg(state, EAX);
        uint32_t res = (current & 0xFFFF0000) | (uint16_t)(int16_t)val;
        gpr.SetReg(state, EAX, res);
    } else {
        int16_t val = (int16_t)gpr.GetReg(state, EAX);
        gpr.SetReg(state, EAX, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCwde(LogicFuncParams) { return OpCwde_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpUd2_Groups(LogicFuncParams) {
    state->fault_vector = 6;
    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
        state->status = EmuStatus::Fault;
    }
    // Always exit to sync EIP to the start of this instruction.
    // If handled correctly by C#, status might be set to Yield or EIP changed.
    return LogicFlow::ExitOnCurrentEIP;
}

template <GroupOpSpec SubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup9_Internal(LogicFuncParams) {
    const uint8_t sub = GroupOperation<SubOp>(operand_modrm);
    if (sub == 1) {  // CMPXCHG8B
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
        if (!mem_res) return LogicFlow::RestartMemoryOp;  // Restart on read fail
        uint64_t mem_val = *mem_res;

        uint32_t eax = gpr.GetReg(state, EAX);
        uint32_t edx = gpr.GetReg(state, EDX);
        uint64_t edx_eax = ((uint64_t)edx << 32) | eax;
        if (mem_val == edx_eax) {
            SetFlagBits(flags_cache, ZF_MASK);
            uint32_t ebx = gpr.GetReg(state, EBX);
            uint32_t ecx = gpr.GetReg(state, ECX);
            uint64_t ecx_ebx = ((uint64_t)ecx << 32) | ebx;

            // Retry on write fail
            if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, gpr, addr, ecx_ebx, utlb, op))
                return LogicFlow::RetryMemoryOp;
        } else {
            ClearFlagBits(flags_cache, ZF_MASK);
            gpr.SetReg(state, EAX, (uint32_t)mem_val);
            gpr.SetReg(state, EDX, (uint32_t)(mem_val >> 32));
        }
    } else {
        return OpUd2_Groups(LogicPassParams);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup9(LogicFuncParams) { return OpGroup9_Internal<>(LogicPassParams); }

#define FIBERCPU_BIND_OPS_GROUPS_IMPL_2
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_2

FORCE_INLINE LogicFlow OpXadd_Word(LogicFuncParams) { return OpXadd_Internal<false>(LogicPassParams); }

#define FIBERCPU_BIND_INTEGER_GROUPS
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_INTEGER_GROUPS

}  // namespace op

// =========================================================================================
// Registration
// =========================================================================================

}  // namespace fiberish
