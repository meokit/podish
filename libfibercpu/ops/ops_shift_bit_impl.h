#pragma once
// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_shift_bit.h"

namespace fiberish {

// Returns LogicFlow to propagate Restart/Retry
template <uint8_t FixedSubOp = 0xFF>
FORCE_INLINE LogicFlow Helper_Group2_Internal(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count,
                                              bool is_byte, mem::MicroTLB* utlb, uint64_t& flags_cache) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }
    uint32_t res = dest;

    // Mask count
    if (count == 0) return LogicFlow::Continue;  // Nothing

    // Perform Op
    bool is_opsize = op->prefixes.flags.opsize;

    switch (subop) {
        case 0:  // ROL
            if (is_byte)
                res = AluRol<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRol<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluRol<uint32_t>(state, flags_cache, dest, count);
            break;
        case 1:  // ROR
            if (is_byte)
                res = AluRor<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRor<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluRor<uint32_t>(state, flags_cache, dest, count);
            break;
        case 2:  // RCL
            if (is_byte)
                res = AluRcl<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRcl<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluRcl<uint32_t>(state, flags_cache, dest, count);
            break;
        case 3:  // RCR
            if (is_byte)
                res = AluRcr<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRcr<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluRcr<uint32_t>(state, flags_cache, dest, count);
            break;
        case 4:  // SHL/SAL
        case 6:  // SAL alias
            if (is_byte)
                res = AluShl<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluShl<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluShl<uint32_t>(state, flags_cache, dest, count);
            break;
        case 5:  // SHR
            if (is_byte)
                res = AluShr<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluShr<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluShr<uint32_t>(state, flags_cache, dest, count);
            break;
        case 7:  // SAR
            if (is_byte)
                res = AluSar<uint8_t>(state, flags_cache, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluSar<uint16_t>(state, flags_cache, (uint16_t)dest, count);
            else
                res = AluSar<uint32_t>(state, flags_cache, dest, count);
            break;
        default:
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }

    // Write Back
    // Use RetryMemoryOp on failure
    if (is_byte) {
        uint8_t mod = (op->modrm >> 6) & 3;
        uint8_t rm = op->modrm & 7;
        if (mod == 3) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            uint32_t val = *rptr;
            if (rm < 4) {
                val = (val & 0xFFFFFF00) | (res & 0xFF);
            } else {
                val = (val & 0xFFFF00FF) | ((res & 0xFF) << 8);
            }
            *rptr = val;
            return LogicFlow::Continue;
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, addr, (uint8_t)res, utlb, op))
                return LogicFlow::RetryMemoryOp;
            return LogicFlow::Continue;
        }
    } else {
        if (op->prefixes.flags.opsize) {
            if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)res, utlb))
                return LogicFlow::RetryMemoryOp;
        } else {
            if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, res, utlb)) return LogicFlow::RetryMemoryOp;
        }
    }
    return LogicFlow::Continue;
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpGroup2_EvIb_Internal(LogicFuncParams) {
    // C0: r/m8, imm8
    // C1: r/m32, imm8
    uint32_t dest;
    if constexpr (S == Specialized::ModReg) {
        // Optimization: Skip ReadModRM call for Reg
        uint8_t rm = op->modrm & 7;
        if constexpr (IsByte) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            if (rm < 4)
                dest = (*rptr) & 0xFF;
            else
                dest = ((*rptr) >> 8) & 0xFF;
        } else {
            if (op->prefixes.flags.opsize) {
                dest = GetReg(state, rm) & 0xFFFF;
            } else {
                dest = GetReg(state, rm);
            }
        }
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!res) return LogicFlow::RestartMemoryOp;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            } else {
                auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            }
        }
    }

    uint8_t count = (uint8_t)imm;
    return Helper_Group2_Internal<FixedSubOp>(state, op, dest, count, IsByte, utlb, flags_cache);
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpGroup2_Ev1_Internal(LogicFuncParams) {
    // D0: Shift r/m8, 1
    // D1: Shift r/m16/32, 1
    uint32_t dest;
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        if constexpr (IsByte)
            dest = GetReg8(state, op->modrm & 7);
        else
            dest = GetReg(state, op->modrm & 7);
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!res) return LogicFlow::RestartMemoryOp;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            } else {
                auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            }
        }
    }
    return Helper_Group2_Internal<FixedSubOp>(state, op, dest, 1, IsByte, utlb, flags_cache);
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
FORCE_INLINE LogicFlow OpGroup2_EvCl_Internal(LogicFuncParams) {
    // D2: r/m8, CL
    // D3: r/m32, CL
    uint32_t dest;
    if constexpr (S == Specialized::ModReg) {
        uint8_t rm = op->modrm & 7;
        if constexpr (IsByte) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            if (rm < 4)
                dest = (*rptr) & 0xFF;
            else
                dest = ((*rptr) >> 8) & 0xFF;
        } else {
            if (op->prefixes.flags.opsize) {
                dest = GetReg(state, rm) & 0xFFFF;
            } else {
                dest = GetReg(state, rm);
            }
        }
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
            if (!res) return LogicFlow::RestartMemoryOp;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            } else {
                auto res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
                if (!res) return LogicFlow::RestartMemoryOp;
                dest = *res;
            }
        }
    }

    uint8_t count = GetReg(state, ECX) & 0xFF;
    return Helper_Group2_Internal<FixedSubOp>(state, op, dest, count, IsByte, utlb, flags_cache);
}

namespace op {

FORCE_INLINE LogicFlow OpGroup2_EbIb(LogicFuncParams) { return OpGroup2_EvIb_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvIb(LogicFuncParams) { return OpGroup2_EvIb_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_Eb1(LogicFuncParams) { return OpGroup2_Ev1_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_Ev1(LogicFuncParams) { return OpGroup2_Ev1_Internal<false>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EbCl(LogicFuncParams) { return OpGroup2_EvCl_Internal<true>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvCl(LogicFuncParams) { return OpGroup2_EvCl_Internal<false>(LogicPassParams); }

// SHL Wrappers
FORCE_INLINE LogicFlow OpGroup2_EvIb_Shl(LogicFuncParams) { return OpGroup2_EvIb_Internal<false, 4>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_Ev1_Shl(LogicFuncParams) { return OpGroup2_Ev1_Internal<false, 4>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvCl_Shl(LogicFuncParams) { return OpGroup2_EvCl_Internal<false, 4>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvIb_Shl_ModReg(LogicFuncParams) {
    return OpGroup2_EvIb_Internal<false, 4, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_Ev1_Shl_ModReg(LogicFuncParams) {
    return OpGroup2_Ev1_Internal<false, 4, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_EvCl_Shl_ModReg(LogicFuncParams) {
    return OpGroup2_EvCl_Internal<false, 4, Specialized::ModReg>(LogicPassParams);
}

// SHR Wrappers
FORCE_INLINE LogicFlow OpGroup2_EvIb_Shr(LogicFuncParams) { return OpGroup2_EvIb_Internal<false, 5>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_Ev1_Shr(LogicFuncParams) { return OpGroup2_Ev1_Internal<false, 5>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvCl_Shr(LogicFuncParams) { return OpGroup2_EvCl_Internal<false, 5>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvIb_Shr_ModReg(LogicFuncParams) {
    return OpGroup2_EvIb_Internal<false, 5, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_Ev1_Shr_ModReg(LogicFuncParams) {
    return OpGroup2_Ev1_Internal<false, 5, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_EvCl_Shr_ModReg(LogicFuncParams) {
    return OpGroup2_EvCl_Internal<false, 5, Specialized::ModReg>(LogicPassParams);
}

// SAR Wrappers
FORCE_INLINE LogicFlow OpGroup2_EvIb_Sar(LogicFuncParams) { return OpGroup2_EvIb_Internal<false, 7>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_Ev1_Sar(LogicFuncParams) { return OpGroup2_Ev1_Internal<false, 7>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvCl_Sar(LogicFuncParams) { return OpGroup2_EvCl_Internal<false, 7>(LogicPassParams); }
FORCE_INLINE LogicFlow OpGroup2_EvIb_Sar_ModReg(LogicFuncParams) {
    return OpGroup2_EvIb_Internal<false, 7, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_Ev1_Sar_ModReg(LogicFuncParams) {
    return OpGroup2_Ev1_Internal<false, 7, Specialized::ModReg>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpGroup2_EvCl_Sar_ModReg(LogicFuncParams) {
    return OpGroup2_EvCl_Internal<false, 7, Specialized::ModReg>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpBt_Reg(LogicFuncParams) {
    // 0F A3: BT r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint32_t bit_val = 0;

    if (mod == 3) {
        uint32_t base = GetReg(state, op->modrm & 7);
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        bit_val = (base >> (bit_idx & mask_val)) & 1;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;   // Bit index can offset the memory address
        uint8_t byte_idx = bit_idx & 7;  // Get bit within the byte
        // Restart on read fail
        auto val_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        bit_val = (*val_res >> byte_idx) & 1;
    }

    if (bit_val)
        SetFlagBits(flags_cache, CF_MASK);
    else
        ClearFlagBits(flags_cache, CF_MASK);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpGroup8_EvIb(LogicFuncParams) {
    // 0F BA /4: BT  r/m16/32, imm8
    // 0F BA /5: BTS r/m16/32, imm8
    // 0F BA /6: BTR r/m16/32, imm8
    // 0F BA /7: BTC r/m16/32, imm8

    uint8_t subop = (op->modrm >> 3) & 7;
    // Safety check: Decoder should route handling here only for Group8 (BA)

    bool opsize = op->prefixes.flags.opsize;
    uint8_t offset = imm & (opsize ? 15 : 31);

    bool is_mem = ((op->modrm >> 6) & 3) != 3;
    uint32_t base = 0;
    uint32_t addr = 0;

    if (is_mem) {
        addr = ComputeLinearAddress(state, op);
        // Restart on read fail
        if (opsize) {
            auto read_res = ReadMem<uint16_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!read_res) return LogicFlow::RestartMemoryOp;
            base = *read_res;
        } else {
            auto read_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!read_res) return LogicFlow::RestartMemoryOp;
            base = *read_res;
        }
    } else {
        if (opsize) {
            base = GetReg(state, op->modrm & 7) & 0xFFFF;
        } else {
            base = GetReg(state, op->modrm & 7);
        }
    }

    uint8_t bit_val = (base >> offset) & 1;

    // Update CF
    if (bit_val)
        SetFlagBits(flags_cache, CF_MASK);
    else
        ClearFlagBits(flags_cache, CF_MASK);

    // Write Back if needed
    if (subop >= 5 && subop <= 7) {
        uint32_t res = base;
        uint32_t mask = (1 << offset);

        if (subop == 5)
            res |= mask;  // BTS
        else if (subop == 6)
            res &= ~mask;  // BTR
        else if (subop == 7)
            res ^= mask;  // BTC

        if (is_mem) {
            // Retry on write fail
            if (opsize) {
                if (!WriteMem<uint16_t, OpOnTLBMiss::Retry>(state, addr, (uint16_t)res, utlb, op))
                    return LogicFlow::RetryMemoryOp;
            } else {
                if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, res, utlb, op))
                    return LogicFlow::RetryMemoryOp;
            }
        } else {
            if (opsize)
                SetReg(state, op->modrm & 7, (GetReg(state, op->modrm & 7) & 0xFFFF0000) | (uint16_t)res);
            else
                SetReg(state, op->modrm & 7, res);
        }
    }
    // Subop 4 (BT) falls through here (no writeback), which is correct.
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBtr_Reg(LogicFuncParams) {
    // 0F B3: BTR r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);
        *rptr = base & ~(1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        // Restart on read fail
        auto byte_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!byte_res) return LogicFlow::RestartMemoryOp;
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);

        // Retry on write fail
        if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, addr, byte & ~(1 << byte_idx), utlb, op))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBts_Reg(LogicFuncParams) {
    // 0F AB: BTS r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);
        *rptr = base | (1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        // Restart on read fail
        auto byte_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!byte_res) return LogicFlow::RestartMemoryOp;
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);

        // Retry on write fail
        if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, addr, byte | (1 << byte_idx), utlb, op))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBtc_Reg(LogicFuncParams) {
    // 0F BB: BTC r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);
        *rptr = base ^ (1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        // Restart on read fail
        auto byte_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!byte_res) return LogicFlow::RestartMemoryOp;
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);

        // Retry on write fail
        if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, addr, byte ^ (1 << byte_idx), utlb, op))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBsr_GvEv(LogicFuncParams) {
    // 0F BD: BSR r16/32, r/m16/32
    // Read only, Restart logic
    uint32_t val;
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        val = *val_res;
    } else {
        auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        val = *val_res;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (val == 0) {
        SetFlagBits(flags_cache, ZF_MASK);
    } else {
        ClearFlagBits(flags_cache, ZF_MASK);
        int count = 31;
        while (((val >> count) & 1) == 0) count--;
        SetReg(state, reg, count);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBsf_Tzcnt_GvEv(LogicFuncParams) {
    // 0F BC: BSF r32, r/m32
    // F3 0F BC: TZCNT r32, r/m32

    uint32_t src;
    if (op->prefixes.flags.opsize) {
        auto src_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src = *src_res;
    } else {
        auto src_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src = *src_res;
    }

    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.rep) {
        // TZCNT (F3 Prefix)
        if (src == 0) {
            SetFlagBits(flags_cache, CF_MASK);
            ClearFlagBits(flags_cache, ZF_MASK);
            SetReg(state, reg, op->prefixes.flags.opsize ? 16 : 32);  // Operand Size
        } else {
            state->ctx.eflags &= ~(CF_MASK | ZF_MASK);  // CF cleared
            // __builtin_ctz is undefined for 0, but we checked src==0
            int count = __builtin_ctz(src);
            SetReg(state, reg, count);
            if (count == 0) SetFlagBits(flags_cache, ZF_MASK);
        }
    } else {
        // BSF (No F3 Prefix)
        if (src == 0) {
            SetFlagBits(flags_cache, ZF_MASK);
            // Dest undefined.
        } else {
            ClearFlagBits(flags_cache, ZF_MASK);
            int count = __builtin_ctz(src);
            SetReg(state, reg, count);
            // Flags: ZF cleared (done above).
            // CF, OF, SF, AF, PF undefined.
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpBswap_Reg(LogicFuncParams) {
    // 0F C8+rd: BSWAP r32
    // DecodeInstruction puts rd in op->modrm for non-ModRM ops
    uint8_t reg = op->modrm & 7;
    uint32_t val = GetReg(state, reg);
    uint32_t res = __builtin_bswap32(val);
    SetReg(state, reg, res);
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
