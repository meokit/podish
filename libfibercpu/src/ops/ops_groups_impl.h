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
    T dest;
    if constexpr (Rm == RmOperandSpec::ModReg) {
        dest = ReadRegGeneric<T>(state, gpr, op->modrm & 7);
    } else if constexpr (Rm >= RmOperandSpec::Reg0 && Rm <= RmOperandSpec::Reg7) {
        constexpr uint8_t index = static_cast<uint8_t>(Rm) - static_cast<uint8_t>(RmOperandSpec::Reg0);
        dest = ReadRegGeneric<T>(state, gpr, index);
    } else {
        const auto res = ReadModRM<T, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest = *res;
    }

    auto src = static_cast<T>(imm);
    if constexpr (Immediate == Group1Immediate::SignExtendedByte) src = static_cast<T>(static_cast<int8_t>(imm));

    return Helper_Group1<T, Flags != FlagsSpec::Dead, SubOp, Rm>(state, gpr, op, flags_cache, dest, src, utlb);
}

template <Group1Immediate Immediate, FlagsSpec Flags = FlagsSpec::Dynamic, GroupOpSpec SubOp = GroupOpSpec::Dynamic,
          OpsizeSpec Size = OpsizeSpec::Dynamic, RmOperandSpec Rm = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup1_Internal(LogicFuncParams) {
    if constexpr (Immediate == Group1Immediate::Byte) {
        static_assert(Size == OpsizeSpec::Dynamic);
        return OpGroup1_T_Internal<uint8_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op)) return OpGroup1_T_Internal<uint16_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
        return OpGroup1_T_Internal<uint32_t, Immediate, Flags, SubOp, Rm>(LogicPassParams);
    }
}

// =========================================================================================
// Group 3: 0xF6 (Eb), 0xF7 (Ev)
// =========================================================================================

template <bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup3_Eb_Internal(LogicFuncParams) {
    // F6
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;

    return Helper_Group3<uint8_t, UpdateFlags, FixedSubOp>(state, gpr, op, flags_cache, val, utlb, imm);
}

template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup3_Ev_T_Internal(LogicFuncParams) {
    // F7
    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        val = *res;
    }

    return Helper_Group3<T, UpdateFlags, FixedSubOp>(state, gpr, op, flags_cache, val, utlb, imm);
}

// =========================================================================================
// Group 4: 0xFE (Eb) - INC/DEC
// =========================================================================================

template <bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup4_Eb_Internal(LogicFuncParams) {
    // FE
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;

    return Helper_Group4<uint8_t, UpdateFlags, FixedSubOp>(state, gpr, op, flags_cache, val, utlb);
}

// =========================================================================================
// Group 5: 0xFF (Ev)
// =========================================================================================

template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow OpGroup5_Ev_T_Internal(LogicFuncParams) {
    // FF
    // RMW Instructions!
    // Must use Blocking memory op to avoid looping
    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    }

    return Helper_Group5<T, UpdateFlags, FixedSubOp>(state, gpr, op, flags_cache, val, utlb, branch);
}

template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp>
FORCE_INLINE LogicFlow OpGroup5_Ev_Control_Internal(LogicFuncParams) {
    static_assert(FixedSubOp == GroupOpSpec::Ext2 || FixedSubOp == GroupOpSpec::Ext4 ||
                  FixedSubOp == GroupOpSpec::Ext6);

    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, op, utlb);
        if (!res) return LogicFlow::ExitOnCurrentEIP;
        val = *res;
    }

    if constexpr (FixedSubOp == GroupOpSpec::Ext2) {  // CALL Ev (Near Indirect)
        if constexpr (sizeof(T) == 2) {
            uint32_t esp = gpr.GetReg(state, ESP) - 2;
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, esp, static_cast<uint16_t>(op->next_eip), utlb,
                                                           op))
                return LogicFlow::ExitOnCurrentEIP;
            gpr.SetReg(state, ESP, esp);
            *branch = val & 0xFFFF;
        } else {
            uint32_t esp = gpr.GetReg(state, ESP) - 4;
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, esp, op->next_eip, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            gpr.SetReg(state, ESP, esp);
            *branch = val;
        }
    } else if constexpr (FixedSubOp == GroupOpSpec::Ext4) {  // JMP Ev (Near Indirect)
        if constexpr (sizeof(T) == 2)
            *branch = val & 0xFFFF;
        else
            *branch = val;
    } else {  // PUSH Ev
        if constexpr (sizeof(T) == 2) {
            uint32_t esp = gpr.GetReg(state, ESP) - 2;
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, esp, static_cast<uint16_t>(val), utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            gpr.SetReg(state, ESP, esp);
        } else {
            uint32_t esp = gpr.GetReg(state, ESP) - 4;
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, esp, val, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            gpr.SetReg(state, ESP, esp);
        }
    }

    return LogicFlow::Continue;
}

template <typename T>
FORCE_INLINE LogicFlow OpXadd_T_Internal(LogicFuncParams) {
    // Restart on read fail
    T dest_val = 0;
    if constexpr (sizeof(T) == 1) {
        auto res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    } else if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    } else {
        auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, gpr, op, utlb);
        if (!res) return LogicFlow::RestartMemoryOp;
        dest_val = *res;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    T src_val = 0;
    if constexpr (sizeof(T) == 1)
        src_val = (T)gpr.GetReg8(state, reg);
    else if constexpr (sizeof(T) == 2)
        src_val = (T)gpr.GetReg(state, reg) & 0xFFFF;
    else
        src_val = (T)gpr.GetReg(state, reg);

    // Keep the flag update local until the store is known to have completed: a
    // faulting instruction must not commit partial architectural state.
    uint64_t xadd_flags = flags_cache;
    T res = AluAdd(state, xadd_flags, dest_val, src_val);

    // Write the result BEFORE overwriting the source register. WriteModRM
    // recomputes the effective address from the register file, and the source
    // register can participate in that address (e.g. xadd [eax], eax).
    if constexpr (sizeof(T) == 1) {
        if (!WriteModRM<uint8_t, OpOnTLBMiss::Blocking>(state, gpr, op, res, utlb)) return LogicFlow::ExitOnCurrentEIP;
    } else if constexpr (sizeof(T) == 2) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, gpr, op, res, utlb)) return LogicFlow::ExitOnCurrentEIP;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, gpr, op, res, utlb)) return LogicFlow::ExitOnCurrentEIP;
    }
    flags_cache = xadd_flags;

    // Swap: Original Dest -> Src Reg
    if constexpr (sizeof(T) == 1) {
        uint32_t curr = gpr.GetReg(state, reg & 3);
        if (reg < 4)
            curr = (curr & 0xFFFFFF00) | (dest_val & 0xFF);
        else
            curr = (curr & 0xFFFF00FF) | ((dest_val & 0xFF) << 8);
        gpr.SetReg(state, reg & 3, curr);
    } else if constexpr (sizeof(T) == 2) {
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | (dest_val & 0xFFFF));
    } else {
        gpr.SetReg(state, reg, dest_val);
    }

    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpGroup3_Ev_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup3_Ev_T_Internal<uint16_t, true>(LogicPassParams);
    else
        return OpGroup3_Ev_T_Internal<uint32_t, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpGroup5_Ev_Generic(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpGroup5_Ev_T_Internal<uint16_t, true>(LogicPassParams);
    else
        return OpGroup5_Ev_T_Internal<uint32_t, true>(LogicPassParams);
}

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

FORCE_INLINE LogicFlow OpCwde(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {
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

FORCE_INLINE LogicFlow OpUd2_Groups(LogicFuncParams) {
    state->fault_vector = 6;
    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
        state->status = EmuStatus::Fault;
    }
    // Always exit to sync EIP to the start of this instruction.
    // If handled correctly by C#, status might be set to Yield or EIP changed.
    return LogicFlow::ExitOnCurrentEIP;
}

FORCE_INLINE LogicFlow OpGroup9(LogicFuncParams) {
    uint8_t sub = (op->modrm >> 3) & 7;
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

#define FIBERCPU_BIND_OPS_GROUPS_IMPL_2
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_GROUPS_IMPL_2

FORCE_INLINE LogicFlow OpXadd_Word(LogicFuncParams) {
    if (op->prefixes.flags.opsize)
        return OpXadd_T_Internal<uint16_t>(LogicPassParams);
    else
        return OpXadd_T_Internal<uint32_t>(LogicPassParams);
}

}  // namespace op

// =========================================================================================
// Registration
// =========================================================================================

}  // namespace fiberish
