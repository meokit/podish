#pragma once
// MMX Instruction Operations Implementation
// Uses SIMDe for cross-platform MMX/SSE emulation

#include "ops_mmx.h"

#include <simde/x86/mmx.h>

#include <cstring>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

// =========================================================================================
// MMX Register Access Helpers
// MMX registers alias FPU ST0-ST7 via the signif (low 64 bits) field of float80
// =========================================================================================

// Get raw 64-bit value from MMX register
FORCE_INLINE uint64_t& GetMmxRegRaw(EmuState* state, int idx) { return state->ctx.fpu_regs[idx].signif; }

// Get MMX register as simde__m64 for SIMD operations
FORCE_INLINE simde__m64 GetMmxReg(EmuState* state, int idx) {
    uint64_t val = state->ctx.fpu_regs[idx].signif;
    return simde_x_mm_set_pi64(static_cast<int64_t>(val));
}

// Set MMX register from simde__m64
FORCE_INLINE void SetMmxReg(EmuState* state, int idx, simde__m64 val) {
    simde__m64_private p = simde__m64_to_private(val);
    state->ctx.fpu_regs[idx].signif = static_cast<uint64_t>(p.i64[0]);

    // MMX execution sets TOS to 0
    state->ctx.fpu_top = 0;

    // Set Tag for this register to Valid (00)
    // fpu_tw is 16-bit, 2 bits per register.
    // Need to clear the 2 bits at position idx * 2.
    state->ctx.fpu_tw &= ~(3 << (idx * 2));
}

// =========================================================================================
// MMX Memory Access Helpers
// =========================================================================================

// Read 64-bit value from MMX register or memory
template <OpOnTLBMiss Strategy>
FORCE_INLINE mem::MemResult<simde__m64> ReadMmxModRM(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register operand - read from MMX register
        return GetMmxReg(state, rm);
    } else {
        // Memory operand - read 64 bits from memory
        uint32_t addr = ComputeLinearAddress(state, op);
        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            auto result = state->mmu.read<uint64_t, false>(addr, utlb, op);
            if (!result) return std::unexpected(result.error());
            return simde_x_mm_set_pi64(static_cast<int64_t>(*result));
        } else {
            auto value = state->mmu.read<uint64_t, true>(addr, utlb, op);
            if (!value) {
                value = state->request_read_and_check_pending<uint64_t>(addr, op->next_eip);
                if (!value) return std::unexpected(value.error());
            }
            return simde_x_mm_set_pi64(static_cast<int64_t>(*value));
        }
    }
}

// Write 64-bit value to MMX register or memory
template <OpOnTLBMiss Strategy>
FORCE_INLINE mem::MemResult<void> WriteMmxModRM(EmuState* state, DecodedOp* op, simde__m64 val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register operand - write to MMX register
        SetMmxReg(state, rm, val);
        return {};
    } else {
        // Memory operand - write 64 bits to memory
        uint32_t addr = ComputeLinearAddress(state, op);
        simde__m64_private p = simde__m64_to_private(val);
        uint64_t raw_val = static_cast<uint64_t>(p.i64[0]);

        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.write<uint64_t, false>(addr, raw_val, utlb, op);
        } else {
            auto result = state->mmu.write<uint64_t, true>(addr, raw_val, utlb, op);
            if (!result) {
                result = state->request_write_and_check_pending<uint64_t>(addr, raw_val, op->next_eip);
            }
            return result;
        }
    }
}

// Read 32-bit value for MOVD (from register or memory)
template <OpOnTLBMiss Strategy>
FORCE_INLINE mem::MemResult<uint32_t> ReadDwordModRM(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        return GetReg(state, rm);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.read<uint32_t, false>(addr, utlb, op);
        } else {
            auto value = state->mmu.read<uint32_t, true>(addr, utlb, op);
            if (!value) {
                value = state->request_read_and_check_pending<uint32_t>(addr, op->next_eip);
            }
            return value;
        }
    }
}

// Read 16-bit value (from register or memory)
template <OpOnTLBMiss Strategy>
FORCE_INLINE mem::MemResult<uint16_t> ReadWordModRM(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        return static_cast<uint16_t>(GetReg(state, rm) & 0xFFFF);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.read<uint16_t, false>(addr, utlb, op);
        } else {
            auto value = state->mmu.read<uint16_t, true>(addr, utlb, op);
            if (!value) {
                value = state->request_read_and_check_pending<uint16_t>(addr, op->next_eip);
            }
            return value;
        }
    }
}

// Write 32-bit value for MOVD (to register or memory)
template <OpOnTLBMiss Strategy>
FORCE_INLINE mem::MemResult<void> WriteDwordModRM(EmuState* state, DecodedOp* op, uint32_t val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        SetReg(state, rm, val);
        return {};
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.write<uint32_t, false>(addr, val, utlb, op);
        } else {
            auto result = state->mmu.write<uint32_t, true>(addr, val, utlb, op);
            if (!result) {
                result = state->request_write_and_check_pending<uint32_t>(addr, val, op->next_eip);
            }
            return result;
        }
    }
}

namespace op {

// =========================================================================================
// EMMS - Empty MMX State
// =========================================================================================

FORCE_INLINE LogicFlow OpEmms(LogicFuncParams) {
    // Reset FPU tag word to all empty (0xFFFF)
    state->ctx.fpu_tw = 0xFFFF;
    state->ctx.fpu_top = 0;
    return LogicFlow::Continue;
}

// =========================================================================================
// Data Movement
// =========================================================================================

// MOVD - Move Doubleword (to MMX)
FORCE_INLINE LogicFlow OpMovd_ToMmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadDwordModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    // Zero-extend 32-bit value to 64-bit MMX register
    simde__m64 result = simde_x_mm_set_pi64(static_cast<int64_t>(static_cast<uint64_t>(*src_res)));
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// MOVD - Move Doubleword (from MMX)
FORCE_INLINE LogicFlow OpMovd_FromMmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    // Get low 32 bits of MMX register
    simde__m64 val = GetMmxReg(state, reg);
    simde__m64_private p = simde__m64_to_private(val);
    uint32_t low_dword = static_cast<uint32_t>(p.u64[0] & 0xFFFFFFFF);

    auto res = WriteDwordModRM<OpOnTLBMiss::Restart>(state, op, low_dword, utlb);
    if (!res) return LogicFlow::RestartMemoryOp;

    return LogicFlow::Continue;
}

// MOVQ - Move Quadword (to MMX)
FORCE_INLINE LogicFlow OpMovq_ToMmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    SetMmxReg(state, reg, *src_res);
    return LogicFlow::Continue;
}

// MOVQ - Move Quadword (from MMX)
FORCE_INLINE LogicFlow OpMovq_FromMmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    simde__m64 val = GetMmxReg(state, reg);

    auto res = WriteMmxModRM<OpOnTLBMiss::Restart>(state, op, val, utlb);
    if (!res) return LogicFlow::RestartMemoryOp;

    return LogicFlow::Continue;
}

// =========================================================================================
// Arithmetic Operations
// =========================================================================================

// PADDB - Add Packed Bytes
FORCE_INLINE LogicFlow OpPaddb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_add_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDW - Add Packed Words
FORCE_INLINE LogicFlow OpPaddw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_add_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDD - Add Packed Dwords
FORCE_INLINE LogicFlow OpPaddd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_add_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBB - Subtract Packed Bytes
FORCE_INLINE LogicFlow OpPsubb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sub_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBW - Subtract Packed Words
FORCE_INLINE LogicFlow OpPsubw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sub_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBD - Subtract Packed Dwords
FORCE_INLINE LogicFlow OpPsubd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sub_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDQ - Add Packed Quadword
FORCE_INLINE LogicFlow OpPaddq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_add_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBQ - Subtract Packed Quadword
FORCE_INLINE LogicFlow OpPsubq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sub_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDUSB - Add Packed Unsigned Bytes with Saturation
FORCE_INLINE LogicFlow OpPaddusb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_adds_pu8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDUSW - Add Packed Unsigned Words with Saturation
FORCE_INLINE LogicFlow OpPaddusw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_adds_pu16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDSB - Add Packed Signed Bytes with Saturation
FORCE_INLINE LogicFlow OpPaddsb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_adds_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PADDSW - Add Packed Signed Words with Saturation
FORCE_INLINE LogicFlow OpPaddsw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_adds_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBUSB - Subtract Packed Unsigned Bytes with Saturation
FORCE_INLINE LogicFlow OpPsubusb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_subs_pu8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBUSW - Subtract Packed Unsigned Words with Saturation
FORCE_INLINE LogicFlow OpPsubusw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_subs_pu16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBSB - Subtract Packed Signed Bytes with Saturation
FORCE_INLINE LogicFlow OpPsubsb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_subs_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSUBSW - Subtract Packed Signed Words with Saturation
FORCE_INLINE LogicFlow OpPsubsw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_subs_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PMULLW - Multiply Packed Words (Low)
FORCE_INLINE LogicFlow OpPmullw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_mullo_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PMULHW - Multiply Packed Words (High)
FORCE_INLINE LogicFlow OpPmulhw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_mulhi_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PMADDWD - Multiply and Add Packed Words
FORCE_INLINE LogicFlow OpPmaddwd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_madd_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PMULUDQ - Multiply Packed Unsigned Doubleword
FORCE_INLINE LogicFlow OpPmuludq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);

    // MMX PMULUDQ multiplies low 32-bit unsigned integers and stores 64-bit product.
    uint64_t prod = static_cast<uint64_t>(dst.u32[0]) * static_cast<uint64_t>(src.u32[0]);
    simde__m64_private out{};
    out.u64[0] = prod;
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PMULHUW - Multiply Packed Unsigned Words (High)
FORCE_INLINE LogicFlow OpPmulhuw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 4; ++i) {
        uint32_t prod = static_cast<uint32_t>(dst.u16[i]) * static_cast<uint32_t>(src.u16[i]);
        out.u16[i] = static_cast<uint16_t>((prod >> 16) & 0xFFFF);
    }
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// =========================================================================================
// Logical Operations
// =========================================================================================

// PAND - Bitwise AND
FORCE_INLINE LogicFlow OpPand_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_and_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PANDN - Bitwise AND NOT
FORCE_INLINE LogicFlow OpPandn_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_andnot_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// POR - Bitwise OR
FORCE_INLINE LogicFlow OpPor_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_or_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PXOR - Bitwise XOR
FORCE_INLINE LogicFlow OpPxor_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_xor_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// =========================================================================================
// Compare Operations
// =========================================================================================

// PCMPEQB - Compare Packed Bytes for Equality
FORCE_INLINE LogicFlow OpPcmpeqb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpeq_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PCMPEQW - Compare Packed Words for Equality
FORCE_INLINE LogicFlow OpPcmpeqw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpeq_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PCMPEQD - Compare Packed Dwords for Equality
FORCE_INLINE LogicFlow OpPcmpeqd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpeq_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PCMPGTB - Compare Packed Signed Bytes for Greater Than
FORCE_INLINE LogicFlow OpPcmpgtb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpgt_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PCMPGTW - Compare Packed Signed Words for Greater Than
FORCE_INLINE LogicFlow OpPcmpgtw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpgt_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PCMPGTD - Compare Packed Signed Dwords for Greater Than
FORCE_INLINE LogicFlow OpPcmpgtd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_cmpgt_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PMINUB - Packed Minimum Unsigned Byte
FORCE_INLINE LogicFlow OpPminub_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 8; ++i) out.u8[i] = dst.u8[i] < src.u8[i] ? dst.u8[i] : src.u8[i];
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PMAXUB - Packed Maximum Unsigned Byte
FORCE_INLINE LogicFlow OpPmaxub_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 8; ++i) out.u8[i] = dst.u8[i] > src.u8[i] ? dst.u8[i] : src.u8[i];
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PMINSW - Packed Minimum Signed Word
FORCE_INLINE LogicFlow OpPminsw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 4; ++i) out.i16[i] = dst.i16[i] < src.i16[i] ? dst.i16[i] : src.i16[i];
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PMAXSW - Packed Maximum Signed Word
FORCE_INLINE LogicFlow OpPmaxsw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 4; ++i) out.i16[i] = dst.i16[i] > src.i16[i] ? dst.i16[i] : src.i16[i];
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PAVGB - Average Packed Unsigned Bytes
FORCE_INLINE LogicFlow OpPavgb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 8; ++i) {
        uint16_t sum = static_cast<uint16_t>(dst.u8[i]) + static_cast<uint16_t>(src.u8[i]) + 1;
        out.u8[i] = static_cast<uint8_t>(sum >> 1);
    }
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PAVGW - Average Packed Unsigned Words
FORCE_INLINE LogicFlow OpPavgw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private out{};
    for (int i = 0; i < 4; ++i) {
        uint32_t sum = static_cast<uint32_t>(dst.u16[i]) + static_cast<uint32_t>(src.u16[i]) + 1;
        out.u16[i] = static_cast<uint16_t>(sum >> 1);
    }
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PSADBW - Sum of Absolute Differences (Packed Unsigned Bytes)
FORCE_INLINE LogicFlow OpPsadbw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, reg));
    simde__m64_private src = simde__m64_to_private(*src_res);
    uint32_t sum = 0;
    for (int i = 0; i < 8; ++i) {
        int diff = static_cast<int>(dst.u8[i]) - static_cast<int>(src.u8[i]);
        sum += static_cast<uint32_t>(diff < 0 ? -diff : diff);
    }
    simde__m64_private out{};
    out.u64[0] = static_cast<uint64_t>(sum);
    SetMmxReg(state, reg, simde__m64_from_private(out));

    return LogicFlow::Continue;
}

// PINSRW - Insert Word
FORCE_INLINE LogicFlow OpPinsrw_Mmx(LogicFuncParams) {
    uint8_t mm_reg = (op->modrm >> 3) & 7;
    uint8_t lane = static_cast<uint8_t>(imm) & 0x3;

    auto src_res = ReadWordModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private dst = simde__m64_to_private(GetMmxReg(state, mm_reg));
    dst.u16[lane] = *src_res;
    SetMmxReg(state, mm_reg, simde__m64_from_private(dst));

    return LogicFlow::Continue;
}

// PEXTRW - Extract Word
FORCE_INLINE LogicFlow OpPextrw_Mmx(LogicFuncParams) {
    uint8_t gpr = (op->modrm >> 3) & 7;
    uint8_t lane = static_cast<uint8_t>(imm) & 0x3;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private src = simde__m64_to_private(*src_res);
    SetReg(state, gpr, static_cast<uint32_t>(src.u16[lane]));

    return LogicFlow::Continue;
}

// PMOVMSKB - Move Byte Mask
FORCE_INLINE LogicFlow OpPmovmskb_Mmx(LogicFuncParams) {
    uint8_t gpr = (op->modrm >> 3) & 7;
    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private src = simde__m64_to_private(*src_res);
    uint32_t mask = 0;
    for (int i = 0; i < 8; ++i) {
        mask |= ((src.u8[i] >> 7) & 1u) << i;
    }
    SetReg(state, gpr, mask);
    return LogicFlow::Continue;
}

// MOVNTQ - Non-Temporal Store Quadword (hint ignored)
FORCE_INLINE LogicFlow OpMovntq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m64 val = GetMmxReg(state, reg);
    auto res = WriteMmxModRM<OpOnTLBMiss::Retry>(state, op, val, utlb);
    if (!res) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

// MASKMOVQ - Masked Store Byte
FORCE_INLINE LogicFlow OpMaskmovq_Mmx(LogicFuncParams) {
    simde__m64 val = GetMmxReg(state, (op->modrm >> 3) & 7);
    simde__m64 mask = GetMmxReg(state, op->modrm & 7);
    uint32_t addr = GetReg(state, fiberish::EDI);

    simde__m64_private vp = simde__m64_to_private(val);
    simde__m64_private mp = simde__m64_to_private(mask);

    for (int i = 0; i < 8; ++i) {
        if (mp.u8[i] & 0x80) {
            if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + i, vp.u8[i], utlb, op)) {
                return LogicFlow::ExitOnCurrentEIP;
            }
        }
    }
    return LogicFlow::Continue;
}

// =========================================================================================
// Shift Operations (Register)
// =========================================================================================

// PSLLW - Shift Left Logical Words (by register)
FORCE_INLINE LogicFlow OpPsllw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSLLD - Shift Left Logical Dwords (by register)
FORCE_INLINE LogicFlow OpPslld_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSLLQ - Shift Left Logical Quadword (by register)
FORCE_INLINE LogicFlow OpPsllq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSRLW - Shift Right Logical Words (by register)
FORCE_INLINE LogicFlow OpPsrlw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSRLD - Shift Right Logical Dwords (by register)
FORCE_INLINE LogicFlow OpPsrld_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSRLQ - Shift Right Logical Quadword (by register)
FORCE_INLINE LogicFlow OpPsrlq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_si64(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSRAW - Shift Right Arithmetic Words (by register)
FORCE_INLINE LogicFlow OpPsraw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sra_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PSRAD - Shift Right Arithmetic Dwords (by register)
FORCE_INLINE LogicFlow OpPsrad_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sra_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// =========================================================================================
// Shift Operations (Immediate) - Group 12/13/14
// =========================================================================================

// PSLLW - Shift Left Logical Words (by immediate)
FORCE_INLINE LogicFlow OpPsllw_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_pi16(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSLLD - Shift Left Logical Dwords (by immediate)
FORCE_INLINE LogicFlow OpPslld_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_pi32(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSLLQ - Shift Left Logical Quadword (by immediate)
FORCE_INLINE LogicFlow OpPsllq_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sll_si64(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSRLW - Shift Right Logical Words (by immediate)
FORCE_INLINE LogicFlow OpPsrlw_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_pi16(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSRLD - Shift Right Logical Dwords (by immediate)
FORCE_INLINE LogicFlow OpPsrld_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_pi32(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSRLQ - Shift Right Logical Quadword (by immediate)
FORCE_INLINE LogicFlow OpPsrlq_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_srl_si64(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSRAW - Shift Right Arithmetic Words (by immediate)
FORCE_INLINE LogicFlow OpPsraw_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sra_pi16(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// PSRAD - Shift Right Arithmetic Dwords (by immediate)
FORCE_INLINE LogicFlow OpPsrad_Mmx_Imm(LogicFuncParams) {
    uint8_t reg = op->modrm & 7;
    simde__m64 count = simde_x_mm_set_pi64(imm);
    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_sra_pi32(dst, count);
    SetMmxReg(state, reg, result);
    return LogicFlow::Continue;
}

// =========================================================================================
// Group Handlers for Shift Immediate
// =========================================================================================

// Group 12: 0F 71 - PSLLW/PSRLW/PSRAW with immediate
FORCE_INLINE LogicFlow OpGroup_Mmx_Shift_Imm_W(LogicFuncParams) {
    uint8_t subop = (op->modrm >> 3) & 7;
    switch (subop) {
        case 2:  // PSRLW imm8
            return OpPsrlw_Mmx_Imm(LogicPassParams);
        case 4:  // PSRAW imm8
            return OpPsraw_Mmx_Imm(LogicPassParams);
        case 6:  // PSLLW imm8
            return OpPsllw_Mmx_Imm(LogicPassParams);
        default:
            // Undefined opcode
            return LogicFlow::ExitOnCurrentEIP;
    }
}

// Group 13: 0F 72 - PSLLD/PSRLD/PSRAD with immediate
FORCE_INLINE LogicFlow OpGroup_Mmx_Shift_Imm_D(LogicFuncParams) {
    uint8_t subop = (op->modrm >> 3) & 7;
    switch (subop) {
        case 2:  // PSRLD imm8
            return OpPsrld_Mmx_Imm(LogicPassParams);
        case 4:  // PSRAD imm8
            return OpPsrad_Mmx_Imm(LogicPassParams);
        case 6:  // PSLLD imm8
            return OpPslld_Mmx_Imm(LogicPassParams);
        default:
            return LogicFlow::ExitOnCurrentEIP;
    }
}

// Group 14: 0F 73 - PSLLQ/PSRLQ with immediate
FORCE_INLINE LogicFlow OpGroup_Mmx_Shift_Imm_Q(LogicFuncParams) {
    uint8_t subop = (op->modrm >> 3) & 7;
    switch (subop) {
        case 2:  // PSRLQ imm8
            return OpPsrlq_Mmx_Imm(LogicPassParams);
        case 6:  // PSLLQ imm8
            return OpPsllq_Mmx_Imm(LogicPassParams);
        default:
            return LogicFlow::ExitOnCurrentEIP;
    }
}

// =========================================================================================
// Pack/Unpack Operations
// =========================================================================================

// PSHUFW - Shuffle Packed Words
FORCE_INLINE LogicFlow OpPshufw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t imm8 = static_cast<uint8_t>(imm);

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64_private src = simde__m64_to_private(*src_res);
    simde__m64_private dst{};

    // dst.word[i] = src.word[imm8[2*i+1:2*i]]
    dst.u16[0] = src.u16[(imm8 >> 0) & 0x3];
    dst.u16[1] = src.u16[(imm8 >> 2) & 0x3];
    dst.u16[2] = src.u16[(imm8 >> 4) & 0x3];
    dst.u16[3] = src.u16[(imm8 >> 6) & 0x3];

    SetMmxReg(state, reg, simde__m64_from_private(dst));
    return LogicFlow::Continue;
}

// PACKSSWB - Pack Signed Words to Signed Bytes
FORCE_INLINE LogicFlow OpPacksswb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_packs_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PACKUSWB - Pack Signed Words to Unsigned Bytes
FORCE_INLINE LogicFlow OpPackuswb_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_packs_pu16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PACKSSDW - Pack Signed Dwords to Signed Words
FORCE_INLINE LogicFlow OpPackssdw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_packs_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKHBW - Unpack High Bytes
FORCE_INLINE LogicFlow OpPunpckhbw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpackhi_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKHWD - Unpack High Words
FORCE_INLINE LogicFlow OpPunpckhwd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpackhi_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKHDQ - Unpack High Dwords
FORCE_INLINE LogicFlow OpPunpckhdq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpackhi_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKLBW - Unpack Low Bytes
FORCE_INLINE LogicFlow OpPunpcklbw_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpacklo_pi8(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKLWD - Unpack Low Words
FORCE_INLINE LogicFlow OpPunpcklwd_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpacklo_pi16(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

// PUNPCKLDQ - Unpack Low Dwords
FORCE_INLINE LogicFlow OpPunpckldq_Mmx(LogicFuncParams) {
    uint8_t reg = (op->modrm >> 3) & 7;

    auto src_res = ReadMmxModRM<OpOnTLBMiss::Restart>(state, op, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;

    simde__m64 dst = GetMmxReg(state, reg);
    simde__m64 result = simde_mm_unpacklo_pi32(dst, *src_res);
    SetMmxReg(state, reg, result);

    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
