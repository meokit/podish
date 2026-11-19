#pragma once
// Arithmetic & Logic
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_carry_rmw_impl.h"

namespace fiberish {

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 00: ADD r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    // retry on TLB miss
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb)) {
        return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 01: ADD r/m16/32, r16/32
    const bool opsize = OperandIs16<Opsize>(op);
    if (opsize) {
        uint8_t rm = op->modrm & 7;
        uint16_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = static_cast<uint16_t>(gpr.GetReg(state, rm));
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint16_t src = gpr.GetReg(state, (op->modrm >> 3) & 7) & 0xFFFF;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            uint32_t reg = gpr.GetReg(state, rm);
            gpr.SetReg(state, rm, (reg & 0xFFFF0000u) | res);
        } else {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    } else {
        uint8_t rm = op->modrm & 7;
        uint32_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, rm);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint32_t src = gpr.GetReg(state, (op->modrm >> 3) & 7);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            gpr.SetReg(state, rm, res);
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 02: ADD r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 03: ADD r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 10: ADC r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    return ExecuteCarryRmw<CarryRmwKind::Adc, uint8_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                              flags_cache);
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 11: ADC r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        return ExecuteCarryRmw<CarryRmwKind::Adc, uint16_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                                   flags_cache);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = gpr.GetReg(state, reg);
        return ExecuteCarryRmw<CarryRmwKind::Adc, uint32_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                                   flags_cache);
    }
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 12: ADC r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluAdc<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    // Write back to reg8
    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 13: ADC r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAdc<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluAdc<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 29: SUB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    const bool opsize = OperandIs16<Opsize>(op);
    if (opsize) {
        uint8_t rm = op->modrm & 7;
        uint16_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = static_cast<uint16_t>(gpr.GetReg(state, rm));
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            uint32_t reg_value = gpr.GetReg(state, rm);
            gpr.SetReg(state, rm, (reg_value & 0xFFFF0000u) | res);
        } else {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    } else {
        uint8_t rm = op->modrm & 7;
        uint32_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, rm);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint32_t src = gpr.GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            gpr.SetReg(state, rm, res);
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 20: AND r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 21: AND r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    const bool opsize = OperandIs16<Opsize>(op);
    if (opsize) {
        uint8_t rm = op->modrm & 7;
        uint16_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = static_cast<uint16_t>(gpr.GetReg(state, rm));
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            uint32_t reg_value = gpr.GetReg(state, rm);
            gpr.SetReg(state, rm, (reg_value & 0xFFFF0000u) | res);
        } else {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    } else {
        uint8_t rm = op->modrm & 7;
        uint32_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, rm);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint32_t src = gpr.GetReg(state, reg);
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            gpr.SetReg(state, rm, res);
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 22: AND r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 23: AND r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 09: OR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    const bool opsize = OperandIs16<Opsize>(op);
    if (opsize) {
        uint8_t rm = op->modrm & 7;
        uint16_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = static_cast<uint16_t>(gpr.GetReg(state, rm));
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            uint32_t reg_value = gpr.GetReg(state, rm);
            gpr.SetReg(state, rm, (reg_value & 0xFFFF0000u) | res);
        } else {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    } else {
        uint8_t rm = op->modrm & 7;
        uint32_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, rm);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint32_t src = gpr.GetReg(state, reg);
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            gpr.SetReg(state, rm, res);
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 31: XOR r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    const bool opsize = OperandIs16<Opsize>(op);
    if (opsize) {
        uint8_t rm = op->modrm & 7;
        uint16_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = static_cast<uint16_t>(gpr.GetReg(state, rm));
        } else {
            auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            uint32_t reg_value = gpr.GetReg(state, rm);
            gpr.SetReg(state, rm, (reg_value & 0xFFFF0000u) | res);
        } else {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    } else {
        uint8_t rm = op->modrm & 7;
        uint32_t dest;
        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            dest = gpr.GetReg(state, rm);
        } else {
            auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
            if (!dest_res) return LogicFlow::RestartMemoryOp;
            dest = *dest_res;
        }

        uint32_t src = gpr.GetReg(state, reg);
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, flags_cache, dest, src);

        if constexpr (AddrMode == AddrModeSpec::ModReg) {
            gpr.SetReg(state, rm, res);
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
                return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          RmOperandSpec FixedReg = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpInc_Reg_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 40+rd: INC r16/32
    const uint8_t reg = FixedReg == RmOperandSpec::Dynamic
                            ? op->modrm & 7
                            : static_cast<uint8_t>(FixedReg) - static_cast<uint8_t>(RmOperandSpec::Reg0);

    // INC does not affect CF
    bool old_cf = ReadCF(flags_cache);

    if (OperandIs16<Opsize>(op)) {
        uint16_t val = (uint16_t)gpr.GetReg(state, reg);
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, flags_cache, val, 1);
        if constexpr (UpdateFlags) AssignCF(flags_cache, old_cf);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = gpr.GetReg(state, reg);
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, flags_cache, val, 1U);
        if constexpr (UpdateFlags) AssignCF(flags_cache, old_cf);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          RmOperandSpec FixedReg = RmOperandSpec::Dynamic>
FORCE_INLINE LogicFlow OpDec_Reg_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 48+rd: DEC r16/32
    const uint8_t reg = FixedReg == RmOperandSpec::Dynamic
                            ? op->modrm & 7
                            : static_cast<uint8_t>(FixedReg) - static_cast<uint8_t>(RmOperandSpec::Reg0);

    // DEC does not affect CF
    bool old_cf = ReadCF(flags_cache);

    if (OperandIs16<Opsize>(op)) {
        uint16_t val = (uint16_t)gpr.GetReg(state, reg);
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, flags_cache, val, 1);
        if constexpr (UpdateFlags) AssignCF(flags_cache, old_cf);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        uint32_t val = gpr.GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, flags_cache, val, 1U);
        if constexpr (UpdateFlags) AssignCF(flags_cache, old_cf);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 04: ADD AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);  // AL is Reg 0 low byte
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    // Write back to AL
    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdd_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 05: ADD EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        // ADD AX, imm16
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluAdd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);

        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        // ADD EAX, imm32
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluAdd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 08: OR r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 0A: OR r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 0B: OR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 0C: OR AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluOr<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpOr_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 0D: OR EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluOr<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluOr<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 14: ADC AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluAdc<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAdc_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 15: ADC EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluAdc<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluAdc<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 18: SBB r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    return ExecuteCarryRmw<CarryRmwKind::Sbb, uint8_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                              flags_cache);
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_EvGv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 19: SBB r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto dest_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint16_t dest = *dest_res;

        uint16_t src = gpr.GetReg(state, reg) & 0xFFFF;
        return ExecuteCarryRmw<CarryRmwKind::Sbb, uint16_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                                   flags_cache);
    } else {
        auto dest_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!dest_res) return LogicFlow::RestartMemoryOp;
        uint32_t dest = *dest_res;

        uint32_t src = gpr.GetReg(state, reg);
        return ExecuteCarryRmw<CarryRmwKind::Sbb, uint32_t, UpdateFlags, AddrMode>(state, gpr, op, dest, src, utlb,
                                                                                   flags_cache);
    }
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 1A: SBB r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluSbb<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 1B: SBB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSbb<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluSbb<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 1C: SBB AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluSbb<uint8_t, UpdateFlags>(state, flags_cache, dest, src);
    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSbb_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 1D: SBB EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluSbb<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluSbb<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 24: AND AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluAnd<uint8_t, UpdateFlags>(state, flags_cache, dest, src);
    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpAnd_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 25: AND EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluAnd<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluAnd<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 28: SUB r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 2A: SUB r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 2B: SUB r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 2C: SUB AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluSub<uint8_t, UpdateFlags>(state, flags_cache, dest, src);
    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpSub_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 2D: SUB EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluSub<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluSub<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_EbGb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 30: XOR r/m8, r8
    auto dest_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!dest_res) return LogicFlow::RestartMemoryOp;
    uint8_t dest = *dest_res;

    uint8_t src = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, flags_cache, dest, src);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry, 1, AddrMode>(state, gpr, op, res, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_GbEb_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 32: XOR r8, r/m8
    auto src_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    uint8_t src = *src_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = gpr.GetReg8(state, reg);
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, flags_cache, dest, src);
    if (reg < 4)
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFFFF00) | res);
    else
        gpr.SetReg(state, reg & 3, (gpr.GetReg(state, reg & 3) & 0xFFFF00FF) | (res << 8));
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic,
          AddrModeSpec AddrMode = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_GvEv_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 33: XOR r16/32, r/m16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (OperandIs16<Opsize>(op)) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint16_t src = *src_res;

        uint16_t dest = gpr.GetReg(state, reg) & 0xFFFF;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, (gpr.GetReg(state, reg) & 0xFFFF0000) | res);
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart, 1, AddrMode>(state, gpr, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        uint32_t src = *src_res;

        uint32_t dest = gpr.GetReg(state, reg);
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, reg, res);
    }
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_AlImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 34: XOR AL, imm8
    uint8_t dest = gpr.GetReg8(state, EAX);
    uint8_t src = (uint8_t)imm;
    uint8_t res = AluXor<uint8_t, UpdateFlags>(state, flags_cache, dest, src);
    uint32_t val = gpr.GetReg(state, EAX);
    val = (val & 0xFFFFFF00) | res;
    gpr.SetReg(state, EAX, val);
    return LogicFlow::Continue;
}

template <FlagsSpec Flags = FlagsSpec::Dynamic, OpsizeSpec Opsize = OpsizeSpec::Dynamic>
FORCE_INLINE LogicFlow OpXor_EaxImm_Internal(LogicFuncParams) {
    constexpr bool UpdateFlags = Flags != FlagsSpec::Dead;
    // 35: XOR EAX, imm32
    if (OperandIs16<Opsize>(op)) {
        uint16_t dest = gpr.GetReg(state, EAX) & 0xFFFF;
        uint16_t src = (uint16_t)imm;
        uint16_t res = AluXor<uint16_t, UpdateFlags>(state, flags_cache, dest, src);
        uint32_t val = gpr.GetReg(state, EAX);
        val = (val & 0xFFFF0000) | res;
        gpr.SetReg(state, EAX, val);
    } else {
        uint32_t dest = gpr.GetReg(state, EAX);
        uint32_t src = imm;
        uint32_t res = AluXor<uint32_t, UpdateFlags>(state, flags_cache, dest, src);
        gpr.SetReg(state, EAX, res);
    }
    return LogicFlow::Continue;
}

enum class AccumulatorTest { Compare, Bitwise };

template <typename T, AccumulatorTest Test, FlagsSpec Flags>
FORCE_INLINE LogicFlow OpTestAccumulatorValue(LogicFuncParams) {
    const T value = ReadRegGeneric<T>(state, gpr, EAX);
    const auto operand = static_cast<T>(imm);
    if constexpr (Test == AccumulatorTest::Compare)
        AluSub<T, Flags != FlagsSpec::Dead>(state, flags_cache, value, operand);
    else
        AluAnd<T, Flags != FlagsSpec::Dead>(state, flags_cache, value, operand);
    return LogicFlow::Continue;
}

template <AccumulatorTest Test, bool Byte, OpsizeSpec Size = OpsizeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpTestAccumulator_Internal(LogicFuncParams) {
    if constexpr (Byte) {
        return OpTestAccumulatorValue<uint8_t, Test, Flags>(LogicPassParams);
    } else {
        if (OperandIs16<Size>(op)) return OpTestAccumulatorValue<uint16_t, Test, Flags>(LogicPassParams);
        return OpTestAccumulatorValue<uint32_t, Test, Flags>(LogicPassParams);
    }
}

template <AddrModeSpec Form = AddrModeSpec::Dynamic, FlagsSpec Flags = FlagsSpec::Dynamic>
FORCE_INLINE LogicFlow OpTest_EbGb_Internal(LogicFuncParams) {
    const auto destination = ReadModRM<uint8_t, OpOnTLBMiss::Restart, 1, Form>(state, gpr, op, utlb);
    if (!destination) return LogicFlow::RestartMemoryOp;
    const uint8_t source = gpr.GetReg8(state, (op->modrm >> 3) & 7);
    AluAnd<uint8_t, Flags != FlagsSpec::Dead>(state, flags_cache, *destination, source);
    return LogicFlow::Continue;
}

namespace op {

// Wrappers for Add
#define FIBERCPU_BIND_OPS_ALU_IMPL_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_ALU_IMPL_0

// Wrappers for Adc
// Keep register-only ADC/SBB independent of memory slow-path calls and their
// register-allocation/stack-frame requirements. Selected once during decoding.
template <CarryRmwKind Kind, bool IsByte, bool UpdateFlags>
FORCE_INLINE LogicFlow OpCarryRmw_ModReg_Internal(LogicFuncParams) {
    const uint8_t rm = op->modrm & 7;
    const uint8_t reg = (op->modrm >> 3) & 7;
    if constexpr (IsByte) {
        return ExecuteCarryRmw<Kind, uint8_t, UpdateFlags, AddrModeSpec::ModReg>(
            state, gpr, op, gpr.GetReg8(state, rm), gpr.GetReg8(state, reg), utlb, flags_cache);
    } else if (op->prefixes.flags.opsize) {
        return ExecuteCarryRmw<Kind, uint16_t, UpdateFlags, AddrModeSpec::ModReg>(
            state, gpr, op, static_cast<uint16_t>(gpr.GetReg(state, rm)), static_cast<uint16_t>(gpr.GetReg(state, reg)),
            utlb, flags_cache);
    } else {
        return ExecuteCarryRmw<Kind, uint32_t, UpdateFlags, AddrModeSpec::ModReg>(
            state, gpr, op, gpr.GetReg(state, rm), gpr.GetReg(state, reg), utlb, flags_cache);
    }
}

#define FIBERCPU_BIND_OPS_ALU_IMPL_1
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_ALU_IMPL_1

FORCE_INLINE LogicFlow OpCmp_AlImm(LogicFuncParams) {
    return OpTestAccumulator_Internal<AccumulatorTest::Compare, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpCmp_EaxImm(LogicFuncParams) {
    return OpTestAccumulator_Internal<AccumulatorTest::Compare, false>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpTest_EbGb(LogicFuncParams) { return OpTest_EbGb_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpTest_AlImm(LogicFuncParams) {
    return OpTestAccumulator_Internal<AccumulatorTest::Bitwise, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpTest_EaxImm(LogicFuncParams) {
    return OpTestAccumulator_Internal<AccumulatorTest::Bitwise, false>(LogicPassParams);
}

}  // namespace op

// ------------------------------------------------------------------------------------------------
// BCD / ASCII Operations
// ------------------------------------------------------------------------------------------------

template <typename T>
FORCE_INLINE void UpdateResultFlags(T res, uint32_t& flags) {
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
    if (CalcPflag(res & 0xFF)) flags |= PF_MASK;
}

namespace op {

FORCE_INLINE LogicFlow OpDaa(LogicFuncParams) {
    // 27: DAA
    // Decimal Adjust AL after Addition
    uint8_t al = gpr.GetReg8(state, EAX);
    uint32_t flags = GetFlags32(flags_cache);
    bool cf = (flags & CF_MASK);
    bool af = (flags & AF_MASK);
    bool new_cf = cf;
    bool new_af = false;

    if ((al & 0x0F) > 9 || af) {
        al += 6;
        new_af = true;
    }

    if (al > 0x9F || cf) {
        al += 0x60;
        new_cf = true;
    }

    // Update flags
    flags &= ~(CF_MASK | AF_MASK | SF_MASK | ZF_MASK | PF_MASK);
    if (new_cf) flags |= CF_MASK;
    if (new_af) flags |= AF_MASK;
    UpdateResultFlags<uint8_t>(al, flags);  // Updates SF, ZF, PF

    gpr.SetReg8(state, EAX, al);
    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpDas(LogicFuncParams) {
    // 2F: DAS
    // Decimal Adjust AL after Subtraction
    uint8_t al = gpr.GetReg8(state, EAX);
    uint32_t flags = GetFlags32(flags_cache);
    bool cf = (flags & CF_MASK);
    bool af = (flags & AF_MASK);
    bool new_cf = cf;
    bool new_af = false;

    // Logic from Intel Manual:
    // IF (AL AND 0Fh) > 9 OR AF = 1 THEN
    //   AL = AL - 6;
    //   AF = 1;
    // ELSE AF = 0; FI;
    // IF (old_AL > 99h) OR CF = 1 THEN
    //   AL = AL - 60h;
    //   CF = 1;
    // ELSE CF = 0; FI;

    uint8_t old_al = al;

    if ((al & 0x0F) > 9 || af) {
        al -= 6;
        new_af = true;
    }

    if (old_al > 0x99 || cf) {
        al -= 0x60;
        new_cf = true;
    }

    // Update flags
    flags &= ~(CF_MASK | AF_MASK | SF_MASK | ZF_MASK | PF_MASK);
    if (new_cf) flags |= CF_MASK;
    if (new_af) flags |= AF_MASK;
    UpdateResultFlags<uint8_t>(al, flags);

    gpr.SetReg8(state, EAX, al);
    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAaa(LogicFuncParams) {
    // 37: AAA
    // ASCII Adjust after Addition
    uint8_t al = gpr.GetReg8(state, EAX);
    uint8_t ah = gpr.GetReg8(state, EAX + 1);  // Not exactly +1 but high byte
    ah = (gpr.GetReg(state, EAX) >> 8) & 0xFF;

    uint32_t flags = GetFlags32(flags_cache);
    bool af = (flags & AF_MASK);
    bool new_af = false;
    bool new_cf = false;

    if ((al & 0x0F) > 9 || af) {
        // The adjustment is 16-bit AX arithmetic: an AL carry must also
        // propagate into AH in addition to the explicit AH increment.
        const uint16_t adjusted = static_cast<uint16_t>(gpr.GetReg(state, EAX) + 0x106);
        al = static_cast<uint8_t>(adjusted);
        ah = static_cast<uint8_t>(adjusted >> 8);
        new_af = true;
        new_cf = true;
    }

    al &= 0x0F;

    // Update flags: AF and CF are set to same value. Others undefined, but we leave them?
    // Manual says: SF, ZF, PF, OF are undefined. We won't touch them or clear them.
    flags &= ~(CF_MASK | AF_MASK);
    if (new_cf) flags |= CF_MASK;
    if (new_af) flags |= AF_MASK;

    // Update Regs
    uint32_t eax = gpr.GetReg(state, EAX);
    eax &= 0xFFFF0000;
    eax |= (uint16_t)((ah << 8) | al);
    gpr.SetReg(state, EAX, eax);

    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAas(LogicFuncParams) {
    // 3F: AAS
    // ASCII Adjust after Subtraction
    uint8_t al = gpr.GetReg8(state, EAX);
    uint8_t ah = (gpr.GetReg(state, EAX) >> 8) & 0xFF;

    uint32_t flags = GetFlags32(flags_cache);
    bool af = (flags & AF_MASK);
    bool new_af = false;
    bool new_cf = false;

    if ((al & 0x0F) > 9 || af) {
        // Preserve the borrow from AX-6 before subtracting the extra 0x100.
        const uint16_t adjusted = static_cast<uint16_t>(gpr.GetReg(state, EAX) - 0x106);
        al = static_cast<uint8_t>(adjusted);
        ah = static_cast<uint8_t>(adjusted >> 8);
        new_af = true;
        new_cf = true;
    }

    al &= 0x0F;

    flags &= ~(CF_MASK | AF_MASK);
    if (new_cf) flags |= CF_MASK;
    if (new_af) flags |= AF_MASK;

    uint32_t eax = gpr.GetReg(state, EAX);
    eax &= 0xFFFF0000;
    eax |= (uint16_t)((ah << 8) | al);
    gpr.SetReg(state, EAX, eax);

    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSalc(LogicFuncParams) {
    // Legacy SALC is accepted by the 32-bit compatibility model. It preserves
    // every flag and every register bit except AL.
    gpr.SetReg8(state, EAX, ReadCF(flags_cache) ? 0xFF : 0);
    return LogicFlow::Continue;
}

template <AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE LogicFlow OpArpl_Internal(LogicFuncParams) {
    // ARPL operates only on selector RPL bits; no descriptor lookup is needed.
    // Delay ZF and destination changes until the memory operation can complete.
    auto destination = ReadModRM<uint16_t, OpOnTLBMiss::Blocking, 1, Form>(state, gpr, op, utlb);
    if (!destination) return LogicFlow::ExitOnCurrentEIP;
    const uint16_t source = static_cast<uint16_t>(gpr.GetReg(state, (op->modrm >> 3) & 7));
    const bool adjusted = (*destination & 3) < (source & 3);
    if (adjusted) {
        const uint16_t result = static_cast<uint16_t>((*destination & ~3u) | (source & 3));
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking, 1, Form>(state, gpr, op, result, utlb))
            return LogicFlow::ExitOnCurrentEIP;
    }
    AssignZF(flags_cache, adjusted);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpArpl(LogicFuncParams) { return OpArpl_Internal<>(LogicPassParams); }

FORCE_INLINE LogicFlow OpAam(LogicFuncParams) {
    // D4 ib: AAM imm8
    uint8_t al = gpr.GetReg8(state, EAX);
    uint8_t base = (uint8_t)imm;
    if (base == 0) {
        // Divide by zero
        state->status = EmuStatus::Fault;
        state->fault_vector = 0;             // #DE
        return LogicFlow::ExitOnCurrentEIP;  // Handled by loop
    }

    uint8_t ah = al / base;
    uint8_t new_al = al % base;

    uint32_t eax = gpr.GetReg(state, EAX);
    eax &= 0xFFFF0000;
    eax |= (uint16_t)((ah << 8) | new_al);
    gpr.SetReg(state, EAX, eax);

    // SF, ZF, PF are modified based on AL logic (result in AL?)
    // Manual says: The flags SF, ZF, and PF are set according to the resulting binary value in the AL register.
    // OF, AF, CF undefined.
    uint32_t flags = GetFlags32(flags_cache);
    flags &= ~(SF_MASK | ZF_MASK | PF_MASK);
    UpdateResultFlags<uint8_t>(new_al, flags);
    SetFlags32AndSyncParityState(flags_cache, flags);

    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAad(LogicFuncParams) {
    // D5 ib: AAD imm8
    uint8_t al = gpr.GetReg8(state, EAX);
    uint8_t ah = (gpr.GetReg(state, EAX) >> 8) & 0xFF;
    uint8_t base = (uint8_t)imm;

    uint8_t temp_al = (uint8_t)(al + (ah * base));
    uint8_t temp_ah = 0;

    uint32_t eax = gpr.GetReg(state, EAX);
    eax &= 0xFFFF0000;
    eax |= (uint16_t)((temp_ah << 8) | temp_al);
    gpr.SetReg(state, EAX, eax);

    // SF, ZF, PF set according to AL
    uint32_t flags = GetFlags32(flags_cache);
    flags &= ~(SF_MASK | ZF_MASK | PF_MASK);
    UpdateResultFlags<uint8_t>(temp_al, flags);
    SetFlags32AndSyncParityState(flags_cache, flags);

    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_INTEGER_ALU
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_INTEGER_ALU

}  // namespace op

}  // namespace fiberish
