#pragma once
// Control Flow
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>
#include <array>
#include <cstring>
#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

extern "C" uint64_t FiberReadRealtimeTsc(const EmuState* state);

template <bool IsRel8>
FORCE_INLINE LogicFlow OpJmp_Rel_Internal(LogicFuncParams) {
    // E9: JMP rel32, EB: JMP rel8
    (void)IsRel8;
    *branch = GetControlTargetEip(op);
    return LogicFlow::ExitToBranch;
}

template <uint8_t Cond, bool IsRel8>
FORCE_INLINE LogicFlow OpJcc_Rel_Internal(LogicFuncParams) {
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    bool taken = CheckConditionFixed<Cond>(flags_cache);
    if (taken) {
        (void)IsRel8;
        *branch = GetControlTargetEip(op);
        return LogicFlow::ExitToBranch;
    }
    return LogicFlow::Continue;
}

template <typename T, uint8_t Condition, AddrModeSpec Form>
FORCE_INLINE LogicFlow OpCmovWidth(LogicFuncParams) {
    // Memory must be read even when the condition is false.
    if constexpr (Form == AddrModeSpec::ModReg) {
        if (!CheckConditionFixed<Condition>(flags_cache)) return LogicFlow::Continue;
    }
    const auto value = ReadModRM<T, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, operand_modrm, utlb);
    if (!value) return LogicFlow::RestartMemoryOp;
    if (Form == AddrModeSpec::ModReg || CheckConditionFixed<Condition>(flags_cache))
        WriteRegGeneric<T>(state, gpr, (operand_modrm >> 3) & 7, *value);
    return LogicFlow::Continue;
}

// Helper for interrupts (Traps/Interrupts - EIP points to next instruction)
inline void RaiseInterrupt(EmuState* state, GprCache& gpr, uint8_t vector, DecodedOp* op) {
    // Sync EIP
    state->ctx.eip = GetNextEip(state, op);

    // Check if hook handles it
    bool handled = false;
    if (state->interrupt_handlers[vector]) {
        handled = gpr.WithCanonical(
            state, [&] { return state->interrupt_handlers[vector](state, vector, state->interrupt_userdata[vector]); });
    }

    // For now, we just fault if not handled, as we don't fully emulate IDT in user mode runner.
    if (!handled) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector;
    }
}

namespace op {

template <uint8_t Condition, RmOperandSpec Rm = RmOperandSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic,
          OpsizeSpec Size = OpsizeSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpCmov_Internal(LogicFuncParams, const Operands& operands = {}) {
    constexpr auto operand_form = Rm == RmOperandSpec::ModReg ? AddrModeSpec::ModReg : Form;
    if (OperandIs16<Size>(op, operands)) return OpCmovWidth<uint16_t, Condition, operand_form>(LogicPassParams);
    return OpCmovWidth<uint32_t, Condition, operand_form>(LogicPassParams);
}

#define FIBERCPU_BIND_OPS_CONTROL_IMPL_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_CONTROL_IMPL_0

template <AddrsizeSpec Size = AddrsizeSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpJecxz_Internal(LogicFuncParams, const Operands& operands = {}) {
    const uint32_t count = gpr.GetReg(state, ECX);
    const bool zero = AddressIs16<Size>(op, operands) ? static_cast<uint16_t>(count) == 0 : count == 0;
    if (!zero) return LogicFlow::Continue;
    *branch = GetControlTargetEip(op);
    return LogicFlow::ExitToBranch;
}

FORCE_INLINE LogicFlow OpJecxz(LogicFuncParams) { return OpJecxz_Internal<>(LogicPassParams); }

template <bool CheckZF, bool ExpectedZF, AddrsizeSpec Size = AddrsizeSpec::Dynamic,
          class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpLoop_Internal(LogicFuncParams, const Operands& operands = {}) {
    uint32_t count;
    if (AddressIs16<Size>(op, operands)) {
        count = gpr.GetReg(state, ECX) & 0xFFFF;
        count = (count - 1) & 0xFFFF;
        uint32_t current = gpr.GetReg(state, ECX);
        gpr.SetReg(state, ECX, (current & 0xFFFF0000u) | count);
    } else {
        count = gpr.GetReg(state, ECX) - 1;
        gpr.SetReg(state, ECX, count);
    }

    bool jump = count != 0;
    if constexpr (CheckZF) {
        jump = jump && (TestFlagBits(flags_cache, ZF_MASK) == ExpectedZF);
    }

    if (jump) {
        *branch = GetControlTargetEip(op);
        return LogicFlow::ExitToBranch;
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_CONTROL_IMPL_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_CONTROL_IMPL_1

// Named wrappers for Jcc specializations

#define FIBERCPU_BIND_OPS_CONTROL_IMPL_MACRO_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_CONTROL_IMPL_MACRO_0

template <typename T>
FORCE_INLINE LogicFlow OpCallRelative(LogicFuncParams) {
    if (!Push<T, true>(state, gpr, static_cast<T>(GetCallReturnEip(state, op)), utlb, op))
        return LogicFlow::RestartMemoryOp;
    *branch = static_cast<T>(GetControlTargetEip(op));
    return LogicFlow::ExitToBranch;
}

template <class Operands>
FORCE_INLINE LogicFlow OpCallRelWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpCallRelative<uint16_t>(LogicPassParams);
    return OpCallRelative<uint32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCall_Rel(LogicFuncParams) {
    return OpCallRelWithOperands(LogicPassParams, OperandSpec::Dynamic{});
}

enum class RetStackAdjustment { None, Immediate };

template <typename T, RetStackAdjustment Adjustment>
FORCE_INLINE LogicFlow OpReturnNear(LogicFuncParams) {
    const auto target = Pop<T, true>(state, gpr, utlb, op);
    if (!target) return LogicFlow::RestartMemoryOp;
    *branch = *target;
    if constexpr (Adjustment == RetStackAdjustment::Immediate)
        gpr.SetReg(state, ESP, gpr.GetReg(state, ESP) + static_cast<uint16_t>(imm));
    return LogicFlow::ExitToBranch;
}

template <RetStackAdjustment Adjustment, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpReturnNearWithOperands(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpReturnNear<uint16_t, Adjustment>(LogicPassParams);
    return OpReturnNear<uint32_t, Adjustment>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpRet(LogicFuncParams) {
    return OpReturnNearWithOperands<RetStackAdjustment::None>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpRet_Imm16(LogicFuncParams) {
    return OpReturnNearWithOperands<RetStackAdjustment::Immediate>(LogicPassParams);
}

template <OpsizeSpec SizeSpec = OpsizeSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpPushf_Internal(LogicFuncParams, const Operands& operands = {}) {
    CommitFlagsCache(state, flags_cache);
    if (OperandIs16<SizeSpec>(op, operands)) {
        if (!Push<uint16_t, true>(state, gpr, (uint16_t)GetArchitecturalEflags(state), utlb, op))
            return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, gpr, GetArchitecturalEflags(state) & 0x00FCFFFF, utlb, op))
            return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

template <OpsizeSpec SizeSpec = OpsizeSpec::Dynamic, class Operands = OperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpPopf_Internal(LogicFuncParams, const Operands& operands = {}) {
    if (OperandIs16<SizeSpec>(op, operands)) {
        auto val_res = Pop<uint16_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint16_t val = *val_res;

        uint32_t mask = state->ctx.eflags_mask & 0xFFFF;
        uint32_t original = GetArchitecturalEflags(state);
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        SetArchitecturalEflags(state, new_flags);
        flags_cache = InitFlagsCache(new_flags);
    } else {
        auto val_res = Pop<uint32_t, true>(state, gpr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint32_t val = *val_res;

        uint32_t mask = state->ctx.eflags_mask;
        uint32_t original = GetArchitecturalEflags(state);
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        SetArchitecturalEflags(state, new_flags);
        flags_cache = InitFlagsCache(new_flags);
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_CONTROL_IMPL_2
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_CONTROL_IMPL_2

FORCE_INLINE LogicFlow OpPushf(LogicFuncParams) { return OpPushf_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpPopf(LogicFuncParams) { return OpPopf_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpStc(LogicFuncParams) {
    // F9: STC
    SetCF(flags_cache);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpClc(LogicFuncParams) {
    // F8: CLC
    ClearCF(flags_cache);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmc(LogicFuncParams) {
    // F5: CMC (Complement Carry)
    AssignCF(flags_cache, !ReadCF(flags_cache));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStd(LogicFuncParams) {
    // FD: STD (Set Direction Flag)
    SetFlagBits(flags_cache, 0x400);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCld(LogicFuncParams) {
    // FC: CLD (Clear Direction Flag)
    ClearFlagBits(flags_cache, 0x400);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSti(LogicFuncParams) {
    // FB: STI (Set Interrupt Flag)
    // Privileged Instruction. In User Mode (CPL=3, IOPL=0), this faults.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;    // #GP
    return LogicFlow::Continue;  // Or ExitOnCurrentEIP? Standard flow handles Status check
}

FORCE_INLINE LogicFlow OpCli(LogicFuncParams) {
    // FA: CLI (Clear Interrupt Flag)
    // Privileged Instruction.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCpuid(LogicFuncParams) {
    // 0F A2: CPUID
    uint32_t leaf = gpr.GetReg(state, EAX);
    uint32_t ecx_in = gpr.GetReg(state, ECX);

    uint32_t eax = 0, ebx = 0, ecx = 0, edx = 0;

    switch (leaf) {
        case 0x00000000:
            // Vendor: "GenuineIntel". Advertise leaves up to 7.
            eax = 0x00000007;
            ebx = 0x756E6547;  // "Genu"
            edx = 0x49656E69;  // "ineI"
            ecx = 0x6C65746E;  // "ntel"
            break;
        case 0x00000001:
            // Family 6 model-style id (legacy i686-like baseline).
            // Keep capabilities conservative and aligned to implemented instruction families.
            eax = 0x00000680;
            ebx = 0;
            ecx = 0;
            edx = 0;
            // EDX bits
            edx |= (1u << 0);   // FPU
            edx |= (1u << 4);   // TSC
            edx |= (1u << 8);   // CMPXCHG8B
            edx |= (1u << 15);  // CMOV
            edx |= (1u << 19);  // CLFLUSH (supported as no-op in 0F AE /7)
            edx |= (1u << 23);  // MMX
            edx |= (1u << 25);  // SSE
            edx |= (1u << 26);  // SSE2
            break;
        case 0x00000007:
            // Structured extended features. We currently expose no additional feature bits.
            if (ecx_in == 0) {
                eax = 0;
                ebx = 0;
                ecx = 0;
                edx = 0;
            }
            break;
        case 0x80000000:
            eax = 0x80000001;
            break;
        case 0x80000001:
            // No extra extended features advertised.
            eax = 0;
            ebx = 0;
            ecx = 0;
            edx = 0;
            break;
        default:
            // Unknown leaf -> zeros
            break;
    }

    gpr.SetReg(state, EAX, eax);
    gpr.SetReg(state, EBX, ebx);
    gpr.SetReg(state, ECX, ecx);
    gpr.SetReg(state, EDX, edx);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRdtsc(LogicFuncParams) {
    // 0F 31: RDTSC
    uint64_t tsc = 0;
    if (state->tsc_mode == 1) {
        tsc = FiberReadRealtimeTsc(state);
    } else {
        // Fixed increment mode
        tsc = state->tsc_offset + state->tsc_fixed_counter;
        state->tsc_fixed_counter += 1000;  // Mock increment per call
    }

    uint32_t low = (uint32_t)tsc;
    uint32_t high = (uint32_t)(tsc >> 32);

    gpr.SetReg(state, EAX, low);
    gpr.SetReg(state, EDX, high);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpWait(LogicFuncParams) {
    // 9B: WAIT/FWAIT n
    // Check pending FPU exceptions?
    // For now NOP.
    return LogicFlow::Continue;
}

struct FxStateImage {
    std::array<uint8_t, 512> bytes{};

    template <typename T>
    T Read(size_t offset) const {
        T value;
        std::memcpy(&value, bytes.data() + offset, sizeof(value));
        return value;
    }

    template <typename T>
    void Write(size_t offset, T value) {
        std::memcpy(bytes.data() + offset, &value, sizeof(value));
    }
};

constexpr uint32_t kFxMxcsrMask = 0x0000FFFFu;

FORCE_INLINE uint16_t FxFullTag(const float80& value) {
    const uint16_t exponent = value.signExp & 0x7FFF;
    if (exponent == 0 && value.signif == 0) return 1;                           // Zero (either sign)
    if (exponent != 0 && exponent != 0x7FFF && (value.signif >> 63)) return 0;  // Valid
    return 2;  // Denormal, infinity, NaN or unsupported encoding
}

template <AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpFxState(LogicFuncParams, bool restore) {
    if (OperandIsReg<Form>(operand_modrm)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 6;
        return LogicFlow::ExitOnCurrentEIP;
    }
    const uint32_t address = ComputeLinearAddress(state, gpr, op);
    if (address & 15) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 13;
        return LogicFlow::ExitOnCurrentEIP;
    }

    FxStateImage image;
    if (restore) {
        // Blocking, bounded reads avoid partial architectural updates and the
        // single pending-memory slot used by restartable scalar operations.
        for (uint32_t offset = 0; offset < image.bytes.size(); offset += 16) {
            auto value =
                ReadMem<std::array<uint8_t, 16>, OpOnTLBMiss::Blocking>(state, gpr, address + offset, utlb, op);
            if (!value) return LogicFlow::ExitOnCurrentEIP;
            std::memcpy(image.bytes.data() + offset, value->data(), 16);
        }
        const uint32_t mxcsr = image.Read<uint32_t>(24);
        if (mxcsr & ~kFxMxcsrMask) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 13;
            return LogicFlow::ExitOnCurrentEIP;
        }

        const uint16_t sw = image.Read<uint16_t>(2);
        const uint8_t top = (sw >> 11) & 7;
        const uint8_t abridged_tags = image.bytes[4];
        uint16_t tags = 0xFFFF;
        std::array<float80, 8> registers{};
        for (uint32_t logical = 0; logical < 8; ++logical) {
            const uint32_t physical = (top + logical) & 7;
            std::memcpy(&registers[physical], image.bytes.data() + 32 + logical * 16, 10);
            // Abridged FTW bits describe physical registers; the saved payloads
            // are logical ST(0)..ST(7). Restore all 80 bits, including empty slots.
            if (abridged_tags & (1u << physical)) {
                tags = (tags & ~(3u << (physical * 2))) | (FxFullTag(registers[physical]) << (physical * 2));
            }
        }

        state->ctx.fpu_cw = image.Read<uint16_t>(0);
        state->ctx.fpu_sw = sw;
        state->ctx.fpu_top = top;
        state->ctx.fpu_tw = tags;
        state->ctx.fpu_opcode = image.Read<uint16_t>(6) & 0x7FF;
        state->ctx.fpu_ip = image.Read<uint32_t>(8);
        state->ctx.fpu_dp = image.Read<uint32_t>(16);
        state->ctx.mxcsr = mxcsr;
        std::memcpy(state->ctx.fpu_regs, registers.data(), sizeof(state->ctx.fpu_regs));
        std::memcpy(state->ctx.xmm, image.bytes.data() + 160, sizeof(state->ctx.xmm));
        // A following x87 instruction may execute in the same Run/block.
        f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);
    } else {
        f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);
        const uint8_t top = state->ctx.fpu_top & 7;
        image.Write<uint16_t>(0, state->ctx.fpu_cw);
        image.Write<uint16_t>(2, (state->ctx.fpu_sw & ~0x3800u) | (top << 11));
        image.Write<uint16_t>(6, state->ctx.fpu_opcode & 0x7FF);
        image.Write<uint32_t>(8, state->ctx.fpu_ip);
        image.Write<uint32_t>(16, state->ctx.fpu_dp);
        image.Write<uint32_t>(24, state->ctx.mxcsr);
        image.Write<uint32_t>(28, kFxMxcsrMask);
        for (uint32_t logical = 0; logical < 8; ++logical) {
            const uint32_t physical = (top + logical) & 7;
            if (((state->ctx.fpu_tw >> (physical * 2)) & 3) != 3) image.bytes[4] |= 1u << physical;
            std::memcpy(image.bytes.data() + 32 + logical * 16, &state->ctx.fpu_regs[physical], 10);
        }
        std::memcpy(image.bytes.data() + 160, state->ctx.xmm, sizeof(state->ctx.xmm));
        // Reserved fields are not architectural outputs. Emit zeros there, but
        // leave the software-owned final 48 bytes (464..511) untouched.
        for (uint32_t offset = 0; offset < 464; offset += 16) {
            std::array<uint8_t, 16> value;
            std::memcpy(value.data(), image.bytes.data() + offset, 16);
            if (!WriteMem<std::array<uint8_t, 16>, OpOnTLBMiss::Blocking>(state, gpr, address + offset, value, utlb,
                                                                          op)) {
                return LogicFlow::ExitOnCurrentEIP;
            }
        }
    }
    return LogicFlow::Continue;
}

template <GroupOpSpec Operation = GroupOpSpec::Dynamic, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpFxGroup(LogicFuncParams) {
    const auto subop = GroupOperation<Operation>(operand_modrm);
    const bool reg = OperandIsReg<Form>(operand_modrm);
    switch (subop) {
        case 0:
            return OpFxState<Form>(LogicPassParams, false);
        case 1:
            return OpFxState<Form>(LogicPassParams, true);
        case 2:
            if (reg) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
                return LogicFlow::ExitOnCurrentEIP;
            } else {
                const uint32_t addr = ComputeLinearAddress(state, gpr, op);
                const auto value = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
                if (!value) return LogicFlow::RestartMemoryOp;
                if (*value & ~kFxMxcsrMask) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 13;
                    return LogicFlow::ExitOnCurrentEIP;
                }
                state->ctx.mxcsr = *value;
            }
            break;
        case 3:
            if (!reg) {
                const uint32_t addr = ComputeLinearAddress(state, gpr, op);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, gpr, addr, state->ctx.mxcsr, utlb, op))
                    return LogicFlow::RetryMemoryOp;
            }
            break;
        case 5:
            if (reg && (operand_modrm & 7) == 0) std::atomic_thread_fence(std::memory_order_acquire);
            break;
        case 6:
            if (reg && (operand_modrm & 7) == 0) std::atomic_thread_fence(std::memory_order_seq_cst);
            break;
        case 7:
            if (reg && (operand_modrm & 7) == 0) std::atomic_thread_fence(std::memory_order_release);
            break;
        default:
            break;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup_0FAE(LogicFuncParams) { return OpFxGroup<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpNop(LogicFuncParams) {
    // NOP - Do nothing
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpHlt(LogicFuncParams) {
    // HLT
    gpr.Commit(state);
    LogMsgState(state, 4, "[ABORT] USER ABORT DETECTED: HLT instruction executed at EIP: 0x%08X",
                GetNextEip(state, op) - GetOpLength(state, op));
    gpr.Reload(state);
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
    return LogicFlow::ExitOnCurrentEIP;
}

FORCE_INLINE LogicFlow OpInt(LogicFuncParams) {
    // CD ib: INT n
    uint8_t vector = (uint8_t)imm;
    mem::InvalidateMicroTLB(utlb);

    CommitFlagsCache(state, flags_cache);
    // Set EIP to next instruction (return address)
    state->ctx.eip = GetNextEip(state, op);

    RaiseInterrupt(state, gpr, vector, op);

    // A handler may replace the image (execve) and reset EFLAGS. The local
    // flags_cache still describes the caller; reload it before this block can
    // commit the stale value on its next flag-modifying instruction.
    flags_cache = GetStateFlagsCache(state);

    // If EIP changed (e.g. signal handler, sigreturn), exit block
    if (state->ctx.eip != GetNextEip(state, op) || state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpInt3(LogicFuncParams) {
    // CC: INT 3
    mem::InvalidateMicroTLB(utlb);
    CommitFlagsCache(state, flags_cache);
    RaiseInterrupt(state, gpr, 3, op);
    if (state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpInto(LogicFuncParams) {
    // CE: INTO
    if (TestFlagBits(flags_cache, OF_MASK)) {
        mem::InvalidateMicroTLB(utlb);
        CommitFlagsCache(state, flags_cache);
        RaiseInterrupt(state, gpr, 4, op);
    }
    if (state->eip_dirty || state->status != EmuStatus::Running) {
        return LogicFlow::ExitOnNextEIP;
    }
    return LogicFlow::Continue;
}

// Named wrappers for Cmov specializations

#define FIBERCPU_BIND_OPS_CONTROL_IMPL_MACRO_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_CONTROL_IMPL_MACRO_1

template <typename T>
FORCE_INLINE LogicFlow OpBoundWidth(LogicFuncParams) {
    const uint32_t address = ComputeLinearAddress(state, gpr, op);
    const auto value = static_cast<T>(gpr.GetReg(state, (operand_modrm >> 3) & 7));
    const auto lower = ReadMem<T, OpOnTLBMiss::Restart>(state, gpr, address, utlb, op);
    if (!lower) return LogicFlow::RestartMemoryOp;
    const auto upper = ReadMem<T, OpOnTLBMiss::Restart>(state, gpr, address + sizeof(T), utlb, op);
    if (!upper) return LogicFlow::RestartMemoryOp;
    if (value < *lower || value > *upper) {
        mem::InvalidateMicroTLB(utlb);
        state->status = EmuStatus::Fault;
        state->fault_vector = 5;
        return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

template <class Operands>
FORCE_INLINE LogicFlow OpBoundWithOperands(LogicFuncParams, const Operands& operands) {
    if (OperandIs16<OpsizeSpec::Dynamic>(op, operands)) return OpBoundWidth<int16_t>(LogicPassParams);
    return OpBoundWidth<int32_t>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpBound(LogicFuncParams) { return OpBoundWithOperands(LogicPassParams, OperandSpec::Dynamic{}); }

#define FIBERCPU_BIND_CONTROL_SPECIALIZATIONS
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_CONTROL_SPECIALIZATIONS

}  // namespace op
}  // namespace fiberish
