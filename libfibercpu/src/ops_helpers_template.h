#pragma once

#include <limits>

#include "common.h"
#include "decoder.h"
#include "exec_utils.h"
#include "ops/ops_carry_rmw_impl.h"
#include "state.h"

namespace fiberish {

// Template Helper for Group 1 (ALU operations with immediate)
template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp>
FORCE_INLINE LogicFlow Helper_Group1(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t operand_modrm,
                                     uint64_t& flags_cache, T dest, T src, mem::MicroTLB* utlb, bool register_operand,
                                     uint8_t rm, uint32_t address) {
    const uint8_t subop = GroupOperation<FixedSubOp>(operand_modrm);

    uint64_t updated_flags = flags_cache;
    T res = 0;

    switch (subop) {
        case 0:
            res = AluAdd<T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 1:
            res = AluOr<T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 2:
            if (!register_operand)
                return ExecuteCarryMemRmw<CarryRmwKind::Adc, T, UpdateFlags>(state, gpr, op, address, dest, src, utlb,
                                                                             flags_cache);
            res = CalculateCarryRmw<CarryRmwKind::Adc, T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 3:
            if (!register_operand)
                return ExecuteCarryMemRmw<CarryRmwKind::Sbb, T, UpdateFlags>(state, gpr, op, address, dest, src, utlb,
                                                                             flags_cache);
            res = CalculateCarryRmw<CarryRmwKind::Sbb, T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 4:
            res = AluAnd<T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 5:
            res = AluSub<T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 6:
            res = AluXor<T, UpdateFlags>(state, updated_flags, dest, src);
            break;
        case 7:
            AluSub<T, UpdateFlags>(state, flags_cache, dest, src);
            return LogicFlow::Continue;  // CMP (No writeback)
        default:
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }

    if (register_operand) {
        WriteRegGeneric<T>(state, gpr, rm, res);
    } else if (!WriteMem<T, OpOnTLBMiss::BlockingRestartable>(state, gpr, address, res, utlb, op)) {
        return LogicFlow::ExitOnCurrentEIP;
    }
    flags_cache = updated_flags;
    return LogicFlow::Continue;
}

// Template Helper for Group 3 (MUL, DIV, TEST, NOT, NEG)
template <typename T, bool UpdateFlags, GroupOpSpec FixedSubOp, AddrModeSpec Form>
FORCE_INLINE LogicFlow Helper_Group3(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t operand_modrm,
                                     const ModRMRmwOperand<Form>& operand, uint64_t& flags_cache, T val,
                                     mem::MicroTLB* utlb, uint32_t imm) {
    const uint8_t subop = GroupOperation<FixedSubOp>(operand_modrm);

    switch (subop) {
        case 0:  // TEST imm
        case 1:  // TEST imm
        {
            AluAnd<T, UpdateFlags>(state, flags_cache, val, (T)imm);
            break;
        }
        case 2:  // NOT
            if (!operand.template Write<T, OpOnTLBMiss::BlockingRestartable>(state, gpr, op, static_cast<T>(~val),
                                                                             utlb))
                return LogicFlow::ExitOnCurrentEIP;
            return LogicFlow::Continue;
        case 3:  // NEG
        {
            uint64_t updated_flags = flags_cache;
            T res = AluSub<T, UpdateFlags>(state, updated_flags, (T)0, val);
            if constexpr (UpdateFlags) {
                if (val != 0)
                    SetFlagBits(updated_flags, CF_MASK);
                else
                    ClearFlagBits(updated_flags, CF_MASK);
            }

            if (!operand.template Write<T, OpOnTLBMiss::BlockingRestartable>(state, gpr, op, res, utlb))
                return LogicFlow::ExitOnCurrentEIP;
            flags_cache = updated_flags;
            return LogicFlow::Continue;
        }
        case 4:  // MUL (Unsigned)
        {
            if constexpr (sizeof(T) == 1) {  // Byte: AX = AL * r/m8
                uint8_t al = gpr.GetReg8(state, EAX);
                uint16_t res = (uint16_t)al * (uint16_t)val;

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | res);

                if constexpr (UpdateFlags) {
                    if ((res & 0xFF00) != 0)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            } else if constexpr (sizeof(T) == 2) {  // Word: DX:AX = AX * r/m16
                uint16_t ax = (uint16_t)(gpr.GetReg(state, EAX) & 0xFFFF);
                uint32_t res = (uint32_t)ax * (uint32_t)val;

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (res & 0xFFFF));
                gpr.SetReg(state, EDX, (gpr.GetReg(state, EDX) & 0xFFFF0000) | ((res >> 16) & 0xFFFF));

                if constexpr (UpdateFlags) {
                    if ((res >> 16) != 0)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            } else {  // Dword: EDX:EAX = EAX * r/m32
                uint32_t eax = gpr.GetReg(state, EAX);
                uint64_t res = (uint64_t)eax * (uint64_t)val;
                gpr.SetReg(state, EAX, (uint32_t)res);
                gpr.SetReg(state, EDX, (uint32_t)(res >> 32));

                if constexpr (UpdateFlags) {
                    if ((res >> 32) != 0)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            }
            break;
        }
        case 5:  // IMUL (Signed)
        {
            if constexpr (sizeof(T) == 1) {  // AX = AL * r/m8
                int8_t al = (int8_t)gpr.GetReg8(state, EAX);
                int16_t res = (int16_t)al * (int16_t)(int8_t)val;

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (uint16_t)res);

                if constexpr (UpdateFlags) {
                    if (res != (int16_t)(int8_t)res)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            } else if constexpr (sizeof(T) == 2) {  // Word: DX:AX = AX * r/m16
                int16_t ax = (int16_t)(gpr.GetReg(state, EAX) & 0xFFFF);
                int32_t res = (int32_t)ax * (int32_t)(int16_t)val;

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (res & 0xFFFF));
                gpr.SetReg(state, EDX, (gpr.GetReg(state, EDX) & 0xFFFF0000) | ((res >> 16) & 0xFFFF));

                if constexpr (UpdateFlags) {
                    if (res != (int32_t)(int16_t)res)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            } else {  // Dword: EDX:EAX = EAX * r/m32
                int32_t eax = (int32_t)gpr.GetReg(state, EAX);
                int64_t res = (int64_t)eax * (int64_t)(int32_t)val;
                gpr.SetReg(state, EAX, (uint32_t)res);
                gpr.SetReg(state, EDX, (uint32_t)(res >> 32));

                if constexpr (UpdateFlags) {
                    if (res != (int64_t)(int32_t)res)
                        SetFlagBits(flags_cache, OF_MASK | CF_MASK);
                    else
                        ClearFlagBits(flags_cache, OF_MASK | CF_MASK);
                }
            }
            break;
        }
        case 6:  // DIV (Unsigned)
        {
            if constexpr (sizeof(T) == 1) {  // AX / r/m8
                uint16_t ax = (uint16_t)gpr.GetReg(state, EAX) & 0xFFFF;
                if (val == 0) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }
                uint16_t q = ax / val;
                uint16_t r = ax % val;

                if (q > 0xFF) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (r << 8) | (q & 0xFF));
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                uint32_t dx_ax =
                    ((uint32_t)(gpr.GetReg(state, EDX) & 0xFFFF) << 16) | (gpr.GetReg(state, EAX) & 0xFFFF);
                if (val == 0) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }
                uint32_t q = dx_ax / val;
                uint32_t r = dx_ax % val;

                if (q > 0xFFFF) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (q & 0xFFFF));
                gpr.SetReg(state, EDX, (gpr.GetReg(state, EDX) & 0xFFFF0000) | (r & 0xFFFF));
            } else {  // EDX:EAX / r/m32
                uint64_t edx_eax = ((uint64_t)gpr.GetReg(state, EDX) << 32) | gpr.GetReg(state, EAX);
                if (val == 0) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }
                uint64_t q = edx_eax / val;
                uint64_t r = edx_eax % val;

                if (q > 0xFFFFFFFF) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (uint32_t)q);
                gpr.SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        case 7:  // IDIV (Signed)
        {
            if constexpr (sizeof(T) == 1) {  // AX / r/m8
                int16_t ax = (int16_t)(gpr.GetReg(state, EAX) & 0xFFFF);
                int8_t v = (int8_t)val;
                if (v == 0 || (ax == std::numeric_limits<int16_t>::min() && v == -1)) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                // Keep the promoted quotient wide until after the byte range
                // check; narrowing first could hide quotient overflow.
                int32_t q = static_cast<int32_t>(ax) / v;
                int32_t r = static_cast<int32_t>(ax) % v;

                if (q > 127 || q < -128) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | ((uint8_t)r << 8) | ((uint8_t)q));
            } else if constexpr (sizeof(T) == 2) {  // DX:AX / r/m16
                int32_t dx_ax =
                    (int32_t)(((uint32_t)(gpr.GetReg(state, EDX) & 0xFFFF) << 16) | (gpr.GetReg(state, EAX) & 0xFFFF));
                int16_t v = (int16_t)val;
                // The host divide must not execute INT32_MIN / -1: guest
                // quotient overflow is #DE, not a host arithmetic exception.
                if (v == 0 || (dx_ax == std::numeric_limits<int32_t>::min() && v == -1)) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }
                int32_t q = dx_ax / v;
                int32_t r = dx_ax % v;

                if (q > 32767 || q < -32768) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | (q & 0xFFFF));
                gpr.SetReg(state, EDX, (gpr.GetReg(state, EDX) & 0xFFFF0000) | (r & 0xFFFF));
            } else {  // EDX:EAX / r/m32
                // Assemble as an unsigned bitvector before interpreting the
                // signed dividend, avoiding overflow in a signed left shift.
                uint64_t bits = ((uint64_t)gpr.GetReg(state, EDX) << 32) | gpr.GetReg(state, EAX);
                int64_t edx_eax = static_cast<int64_t>(bits);
                int32_t v = (int32_t)val;
                // Guard both / and % before either host instruction can trap.
                // Return without committing EAX/EDX or advancing the guest EIP.
                if (v == 0 || (edx_eax == std::numeric_limits<int64_t>::min() && v == -1)) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }
                int64_t q = edx_eax / v;
                int64_t r = edx_eax % v;

                if (q > 2147483647LL || q < -2147483648LL) {
                    state->fault_vector = 0;  // #DE
                    if (!gpr.WithCanonical(state, [&] { return state->hooks.on_interrupt(state, 0); })) {
                        state->status = EmuStatus::Fault;
                    }
                    return LogicFlow::ExitOnCurrentEIP;
                }

                gpr.SetReg(state, EAX, (uint32_t)q);
                gpr.SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        default:
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

// Template Helper for Group 4 (INC, DEC)
template <typename T, bool UpdateFlags = true, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow Helper_Group4(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t operand_modrm,
                                     const ModRMRmwOperand<>& operand, uint64_t& flags_cache, T val,
                                     mem::MicroTLB* utlb) {
    const uint8_t subop = GroupOperation<FixedSubOp>(operand_modrm);

    switch (subop) {
        case 0:    // INC
        case 1: {  // DEC
            uint64_t updated_flags = flags_cache;
            const T result = AluIncDec<T, UpdateFlags>(state, updated_flags, val, subop == 1);
            // A faulting destination write must leave the old flags visible.
            if (!operand.Write<T, OpOnTLBMiss::BlockingRestartable>(state, gpr, op, result, utlb))
                return LogicFlow::ExitOnCurrentEIP;
            if constexpr (UpdateFlags) flags_cache = updated_flags;
            break;
        }
        default:
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

// Template Helper for Group 5 (INC, DEC, CALL, JMP, PUSH)
template <typename T, bool UpdateFlags = true, GroupOpSpec FixedSubOp = GroupOpSpec::Dynamic>
FORCE_INLINE LogicFlow Helper_Group5(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t operand_modrm,
                                     const ModRMRmwOperand<>& operand, uint64_t& flags_cache, T val,
                                     mem::MicroTLB* utlb, uint32_t* branch) {
    const uint8_t subop = GroupOperation<FixedSubOp>(operand_modrm);

    switch (subop) {
        case 0:    // INC Ev
        case 1: {  // DEC Ev
            uint64_t updated_flags = flags_cache;
            const T result = AluIncDec<T, UpdateFlags>(state, updated_flags, val, subop == 1);
            // A faulting destination write must leave the old flags visible.
            if (!operand.Write<T, OpOnTLBMiss::BlockingRestartable>(state, gpr, op, result, utlb))
                return LogicFlow::ExitOnCurrentEIP;
            if constexpr (UpdateFlags) flags_cache = updated_flags;
            break;
        }
        case 2:  // CALL Ev (Near Indirect)
            if (!Push<T>(state, gpr, static_cast<T>(GetCallReturnEip(state, op)), utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            *branch = val;
            break;
        case 4:  // JMP Ev (Near Indirect)
            *branch = val;
            break;
        case 6:  // PUSH Ev
            if (!Push<T>(state, gpr, val, utlb, op)) return LogicFlow::ExitOnCurrentEIP;
            break;
        default:
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

}  // namespace fiberish
