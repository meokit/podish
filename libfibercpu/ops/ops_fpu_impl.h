#pragma once
// FPU Instructions
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include <cstring>
#include <optional>

#include <cmath>
#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_fpu.h"

namespace fiberish {

inline void UpdateFSW(EmuState* state) {
    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x3800) | ((state->ctx.fpu_top & 7) << 11);
}

// FPU Stack Helpers
inline void FpuPush(EmuState* state, const float80* val) {
    state->ctx.fpu_top = (state->ctx.fpu_top - 1) & 7;
    std::memcpy(&state->ctx.fpu_regs[state->ctx.fpu_top], val, sizeof(float80));
    state->ctx.fpu_tw &= ~(3 << (state->ctx.fpu_top * 2));  // Mark valid (00)
    UpdateFSW(state);
}

inline float80 FpuPop(EmuState* state) {
    float80 val;
    std::memcpy(&val, &state->ctx.fpu_regs[state->ctx.fpu_top], sizeof(float80));
    state->ctx.fpu_tw |= (3 << (state->ctx.fpu_top * 2));  // Mark empty
    state->ctx.fpu_top = (state->ctx.fpu_top + 1) & 7;
    UpdateFSW(state);
    return val;
}

inline float80& FpuTop(EmuState* state, int index) { return state->ctx.fpu_regs[(state->ctx.fpu_top + index) & 7]; }

inline void UpdateFpuRoundingMode(EmuState* state) { f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw); }

inline void SetFpuCompareFlags(EmuState* state, float80 a, float80 b) {
    state->ctx.fpu_sw &= ~0x4500;  // Clear C3/C2/C0
    if (f80_uncomparable(a, b)) {
        state->ctx.fpu_sw |= 0x4500;  // C3=1, C2=1, C0=1
    } else if (f80_eq(a, b)) {
        state->ctx.fpu_sw |= 0x4000;  // C3=1
    } else if (f80_lt(a, b)) {
        state->ctx.fpu_sw |= 0x0100;  // C0=1
    }
}

inline mem::MemResult<float80> ReadPackedBcd80(EmuState* state, uint32_t addr, mem::MicroTLB* utlb, DecodedOp* op) {
    double value = 0.0;
    double place = 1.0;
    for (int i = 0; i < 9; ++i) {
        auto b_res = ReadMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + (uint32_t)i, utlb, op);
        if (!b_res) return std::unexpected(b_res.error());
        uint8_t b = *b_res;
        uint8_t lo = b & 0x0F;
        uint8_t hi = (b >> 4) & 0x0F;
        if (lo <= 9) {
            value += (double)lo * place;
        }
        place *= 10.0;
        if (hi <= 9) {
            value += (double)hi * place;
        }
        place *= 10.0;
    }

    auto sign_res = ReadMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + 9, utlb, op);
    if (!sign_res) return std::unexpected(sign_res.error());
    if ((*sign_res & 0x80) != 0) {
        value = -value;
    }

    return f80_from_double(value);
}

inline bool WritePackedBcd80(EmuState* state, uint32_t addr, float80 value, mem::MicroTLB* utlb, DecodedOp* op) {
    double d = std::trunc(f80_to_double(value));
    bool negative = std::signbit(d);
    double abs_val = std::fabs(d);

    for (int i = 0; i < 9; ++i) {
        uint8_t lo = (uint8_t)std::fmod(abs_val, 10.0);
        abs_val = std::floor(abs_val / 10.0);
        uint8_t hi = (uint8_t)std::fmod(abs_val, 10.0);
        abs_val = std::floor(abs_val / 10.0);
        uint8_t packed = (uint8_t)((hi << 4) | lo);
        if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + (uint32_t)i, packed, utlb, op)) {
            return false;
        }
    }

    uint8_t sign = negative ? 0x80 : 0x00;
    if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + 9, sign, utlb, op)) {
        return false;
    }
    return true;
}

// Helper to read float32 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
inline mem::MemResult<float80> ReadF32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, ComputeLinearAddress(state, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint32_t val = *res;
    float f = *(float*)&val;
    return f80_from_double((double)f);
}

// Helper to read float64 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
inline mem::MemResult<float80> ReadF64(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res = ReadMem<uint64_t, OpOnTLBMiss::Blocking>(state, ComputeLinearAddress(state, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint64_t val = *res;
    return f80_from_double(*(double*)&val);
}

namespace op {

FORCE_INLINE LogicFlow OpFpu_D8(LogicFuncParams) {
    // D8: FPU arithmetic
    uint8_t subop = (op->modrm >> 3) & 7;
    uint8_t mod = op->modrm >> 6;
    float80& st0 = FpuTop(state, 0);

    // Register form: D8 C0-FF (operate with ST(i))
    if (mod == 3) {
        uint8_t sti_idx = op->modrm & 7;
        float80& sti = FpuTop(state, sti_idx);

        switch (subop) {
            case 0:  // FADD ST(0), ST(i)
                st0 = f80_add(st0, sti);
                break;
            case 1:  // FMUL ST(0), ST(i)
                st0 = f80_mul(st0, sti);
                break;
            case 2:  // FCOM ST(i)
                if (f80_lt(st0, sti))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, sti))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                break;
            case 3:  // FCOMP ST(i)
                if (f80_lt(st0, sti))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, sti))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                FpuPop(state);
                break;
            case 4:  // FSUB ST(0), ST(i)
                st0 = f80_sub(st0, sti);
                break;
            case 5:  // FSUBR ST(0), ST(i)
                st0 = f80_sub(sti, st0);
                break;
            case 6:  // FDIV ST(0), ST(i)
                st0 = f80_div(st0, sti);
                break;
            case 7:  // FDIVR ST(0), ST(i)
                st0 = f80_div(sti, st0);
                break;
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
        return LogicFlow::Continue;
    }

    // Memory form: D8 /r m32fp
    auto val_res = ReadF32(state, op, utlb);
    if (!val_res) return LogicFlow::ExitOnCurrentEIP;
    float80 val = *val_res;

    switch (subop) {
        case 0:
            st0 = f80_add(st0, val);
            break;  // FADD
        case 1:
            st0 = f80_mul(st0, val);
            break;  // FMUL
        case 2:     // FCOM
            // Update FPU Status Word (C0, C2, C3)
            // Unordered/LT/EQ?
            // Simplified:
            if (f80_lt(st0, val))
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;  // C0=1
            else if (f80_eq(st0, val))
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;  // C3=1
            else
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);  // Greater
            break;
        case 3:  // FCOMP (Compare and Pop)
            if (f80_lt(st0, val))
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
            else if (f80_eq(st0, val))
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
            else
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
            FpuPop(state);
            break;
        case 4:
            st0 = f80_sub(st0, val);
            break;  // FSUB
        case 5:
            st0 = f80_sub(val, st0);
            break;  // FSUBR
        case 6:
            st0 = f80_div(st0, val);
            break;  // FDIV
        case 7:
            st0 = f80_div(val, st0);
            break;  // FDIVR
        default:
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_D9(LogicFuncParams) {
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // D9 C0-FF: FPU Instructions with Regs
        // Map 0xD9C0 -> index
        uint8_t op_byte = op->modrm;

        if ((op_byte & 0xF8) == 0xC0) {  // FLD ST(i) (DUP) -> D9 C0
            float80 t = FpuTop(state, op_byte & 7);
            FpuPush(state, &t);
        } else if (op_byte == 0xC9) {  // FXCH ST(1)
            float80 t = FpuTop(state, 0);
            FpuTop(state, 0) = FpuTop(state, 1);
            FpuTop(state, 1) = t;
        } else if ((op_byte & 0xF8) == 0xC8) {  // FXCH ST(i)
            int idx = op_byte & 7;
            float80 t = FpuTop(state, 0);
            FpuTop(state, 0) = FpuTop(state, idx);
            FpuTop(state, idx) = t;
        } else if (op_byte == 0xD0) {  // FNOP
        } else if (op_byte == 0xE4) {  // FTST
            float80 st0 = FpuTop(state, 0);
            float80 zero = ConstF80_Zero();
            state->ctx.fpu_sw &= ~0x4500;
            if (f80_isnan(st0)) {
                state->ctx.fpu_sw |= 0x4500;  // C3=1, C2=1, C0=1
            } else if (f80_lt(st0, zero)) {
                state->ctx.fpu_sw |= 0x0100;  // C0=1
            } else if (f80_eq(st0, zero)) {
                state->ctx.fpu_sw |= 0x4000;  // C3=1
            }
        } else if (op_byte == 0xE5) {  // FXAM
            float80 st0 = FpuTop(state, 0);
            state->ctx.fpu_sw &= ~0x4700;                           // Clear C3, C2, C1, C0
            if (st0.signExp & 0x8000) state->ctx.fpu_sw |= 0x0200;  // C1=1 (Sign)
            if (f80_isnan(st0))
                state->ctx.fpu_sw |= 0x0100;  // NaN (C0=1)
            else if (f80_isinf(st0))
                state->ctx.fpu_sw |= 0x0500;  // Inf (C0=1, C2=1)
            else if (f80_iszero(st0))
                state->ctx.fpu_sw |= 0x4000;  // Zero (C3=1)
            else if (f80_isdenormal(st0))
                state->ctx.fpu_sw |= 0x4400;  // Denormal (C3=1, C2=1)
            else
                state->ctx.fpu_sw |= 0x0400;  // Normal (C2=1)
        } else if (op_byte == 0xE0) {         // FCHS
            FpuTop(state, 0) = f80_neg(FpuTop(state, 0));
        } else if (op_byte == 0xE1) {  // FABS
            FpuTop(state, 0) = f80_abs(FpuTop(state, 0));
        } else if (op_byte == 0xE8) {  // FLD1
            float80 t = ConstF80_One();
            FpuPush(state, &t);
        } else if (op_byte == 0xE9) {  // FLDL2T
            float80 t = ConstF80_L2T();
            FpuPush(state, &t);
        } else if (op_byte == 0xEA) {  // FLDL2E
            float80 t = ConstF80_L2E();
            FpuPush(state, &t);
        } else if (op_byte == 0xEB) {  // FLDPI
            float80 t = ConstF80_Pi();
            FpuPush(state, &t);
        } else if (op_byte == 0xEC) {  // FLDLG2
            float80 t = ConstF80_LG2();
            FpuPush(state, &t);
        } else if (op_byte == 0xED) {  // FLDLN2
            float80 t = ConstF80_LN2();
            FpuPush(state, &t);
        } else if (op_byte == 0xEE) {  // FLDZ
            float80 t = ConstF80_Zero();
            FpuPush(state, &t);
        } else if (op_byte == 0xF0) {  // F2XM1
            float80& st0 = FpuTop(state, 0);
            st0 = f80_from_double(std::pow(2.0, f80_to_double(st0)) - 1.0);
        } else if (op_byte == 0xF1) {  // FYL2X: ST(1) = ST(1) * log2(ST(0)); Pop ST(0)
            float80 x = FpuPop(state);
            float80& y = FpuTop(state, 0);
            y = f80_mul(y, f80_log2(x));
        } else if (op_byte == 0xF2) {  // FPTAN
            float80& st0 = FpuTop(state, 0);
            st0 = f80_from_double(std::tan(f80_to_double(st0)));
            float80 t = ConstF80_One();
            FpuPush(state, &t);
        } else if (op_byte == 0xF3) {  // FPATAN
            double y = f80_to_double(FpuTop(state, 1));
            double x = f80_to_double(FpuPop(state));
            FpuTop(state, 0) = f80_from_double(std::atan2(y, x));
        } else if (op_byte == 0xF4) {  // FXTRACT
            double v = f80_to_double(FpuTop(state, 0));
            int exp;
            double sig = std::frexp(v, &exp);
            if (v != 0.0) {
                sig *= 2.0;
                exp -= 1;
            }
            FpuTop(state, 0) = f80_from_double(exp);
            float80 t = f80_from_double(sig);
            FpuPush(state, &t);
        } else if (op_byte == 0xF5) {  // FPREM1
            float80& st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            st0 = f80_from_double(std::remainder(f80_to_double(st0), f80_to_double(st1)));
        } else if (op_byte == 0xF6) {  // FDECSTP
            state->ctx.fpu_top = (state->ctx.fpu_top - 1) & 7;
            UpdateFSW(state);
        } else if (op_byte == 0xF7) {  // FINCSTP
            state->ctx.fpu_top = (state->ctx.fpu_top + 1) & 7;
            UpdateFSW(state);
        } else if (op_byte == 0xF8) {  // FPREM
            float80& st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            st0 = f80_rem(st0, st1);
        } else if (op_byte == 0xF9) {  // FYL2XP1
            double y = f80_to_double(FpuTop(state, 1));
            double x = f80_to_double(FpuPop(state));
            FpuTop(state, 0) = f80_from_double(y * std::log2(x + 1.0));
        } else if (op_byte == 0xFA) {  // FSQRT
            FpuTop(state, 0) = f80_sqrt(FpuTop(state, 0));
        } else if (op_byte == 0xFB) {  // FSINCOS
            float80 st0 = FpuTop(state, 0);
            FpuTop(state, 0) = f80_sin(st0);
            float80 c = f80_cos(st0);
            FpuPush(state, &c);
        } else if (op_byte == 0xFC) {  // FRNDINT
            FpuTop(state, 0) = f80_round(FpuTop(state, 0));
        } else if (op_byte == 0xFD) {  // FSCALE
            FpuTop(state, 0) = f80_scale(FpuTop(state, 0), (int)f80_to_int(FpuTop(state, 1)));
        } else if (op_byte == 0xFE) {  // FSIN
            FpuTop(state, 0) = f80_sin(FpuTop(state, 0));
        } else if (op_byte == 0xFF) {  // FCOS
            FpuTop(state, 0) = f80_cos(FpuTop(state, 0));
        } else {
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        // Memory Access (Blocking)
        uint32_t addr = ComputeLinearAddress(state, op);
        switch (subop) {
            case 0:  // FLD m32
            {
                auto t_res = ReadF32(state, op, utlb);
                if (!t_res) return LogicFlow::ExitOnCurrentEIP;
                FpuPush(state, &(*t_res));
                break;
            }
            case 2:  // FST m32fp
            {
                float80 val = FpuTop(state, 0);
                float f = (float)f80_to_double(val);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, *(uint32_t*)&f, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FSTP m32fp
            {
                float80 val = FpuTop(state, 0);
                float f = (float)f80_to_double(val);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, *(uint32_t*)&f, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 4:  // FLDENV m14/28byte
            {
                auto cw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!cw_res) return LogicFlow::ExitOnCurrentEIP;
                auto sw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 2, utlb, op);
                if (!sw_res) return LogicFlow::ExitOnCurrentEIP;
                auto tw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 4, utlb, op);
                if (!tw_res) return LogicFlow::ExitOnCurrentEIP;
                state->ctx.fpu_cw = *cw_res;
                state->ctx.fpu_sw = *sw_res;
                state->ctx.fpu_tw = *tw_res;
                state->ctx.fpu_top = (state->ctx.fpu_sw >> 11) & 7;
                UpdateFpuRoundingMode(state);
                UpdateFSW(state);
                break;
            }
            case 5:  // FLDCW m16
            {
                auto cw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!cw_res) return LogicFlow::ExitOnCurrentEIP;
                state->ctx.fpu_cw = *cw_res;
                UpdateFpuRoundingMode(state);
                break;
            }
            case 6:  // FNSTENV m14/28byte
            {
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, state->ctx.fpu_cw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                uint16_t saved_sw = (uint16_t)((state->ctx.fpu_sw & ~0x3800) | ((state->ctx.fpu_top & 7) << 11));
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 2, saved_sw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 4, state->ctx.fpu_tw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                // TODO: full env store (IP, CS, OP, DS)
                break;
            }
            case 7:  // FNSTCW m16
            {
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, state->ctx.fpu_cw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DA(LogicFuncParams) {
    // DA: Int Arith m32
    if ((op->modrm >> 6) == 3) {
        if (op->modrm == 0xE9) {  // FUCOMPP
            float80 st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            if (f80_uncomparable(st0, st1)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4500;  // C3=1, C2=1, C0=1
            } else if (f80_eq(st0, st1)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;  // C3=1
            } else if (f80_lt(st0, st1)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;  // C0=1
            } else {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
            }
            FpuPop(state);
            FpuPop(state);
        } else {
            // DA C0-C7: FCMOVB
            // DA C8-CF: FCMOVE
            // DA D0-D7: FCMOVBE
            // DA D8-DF: FCMOVU
            int idx = op->modrm & 7;
            bool pass = false;
            switch ((op->modrm >> 3) & 7) {
                case 0:
                    pass = (GetFlags32(flags_cache) & fiberish::CF_MASK);
                    break;  // FCMOVB
                case 1:
                    pass = (GetFlags32(flags_cache) & fiberish::ZF_MASK);
                    break;  // FCMOVE
                case 2:
                    pass = (GetFlags32(flags_cache) & (fiberish::CF_MASK | fiberish::ZF_MASK));
                    break;  // FCMOVBE
                case 3:
                    pass = ReadPF(flags_cache);
                    break;  // FCMOVU
            }
            if (pass) FpuTop(state, 0) = FpuTop(state, idx);
        }
    } else {
        uint8_t subop = (op->modrm >> 3) & 7;
        uint32_t addr = ComputeLinearAddress(state, op);
        auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
        if (!val_res) return LogicFlow::ExitOnCurrentEIP;
        int32_t val32 = (int32_t)*val_res;
        float80 val = f80_from_int(val32);
        float80& st0 = FpuTop(state, 0);

        switch (subop) {
            case 0:
                st0 = f80_add(st0, val);
                break;  // FIADD
            case 1:
                st0 = f80_mul(st0, val);
                break;  // FIMUL
            case 2:     // FICOM
                if (f80_lt(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                break;
            case 3:  // FICOMP
                if (f80_lt(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                FpuPop(state);
                break;
            case 4:
                st0 = f80_sub(st0, val);
                break;  // FISUB
            case 5:
                st0 = f80_sub(val, st0);
                break;  // FISUBR
            case 6:
                st0 = f80_div(st0, val);
                break;  // FIDIV
            case 7:
                st0 = f80_div(val, st0);
                break;  // FIDIVR
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DB(LogicFuncParams) {
    // DB: FILD/FIST
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // DB C0-C7: FCMOVNB
        // DB C8-CF: FCMOVNE
        // DB D0-D7: FCMOVNBE
        // DB D8-DF: FCMOVNU
        uint8_t mode = (op->modrm >> 3) & 7;
        if (mode < 4) {
            int idx = op->modrm & 7;
            bool pass = false;
            switch (mode) {
                case 0:
                    pass = !(GetFlags32(flags_cache) & fiberish::CF_MASK);
                    break;  // FCMOVNB
                case 1:
                    pass = !(GetFlags32(flags_cache) & fiberish::ZF_MASK);
                    break;  // FCMOVNE
                case 2:
                    pass = !(GetFlags32(flags_cache) & (fiberish::CF_MASK | fiberish::ZF_MASK));
                    break;  // FCMOVNBE
                case 3:
                    pass = !ReadPF(flags_cache);
                    break;  // FCMOVNU
            }
            if (pass) FpuTop(state, 0) = FpuTop(state, idx);
        } else if (op->modrm == 0xE2) {               // FCLEX
            state->ctx.fpu_sw &= ~(0x00FF | 0x8000);  // Clear exception flags and busy flag
        } else if (op->modrm == 0xE3) {               // FINIT
            state->ctx.fpu_cw = 0x037F;
            state->ctx.fpu_sw = 0;
            state->ctx.fpu_tw = 0xFFFF;
            state->ctx.fpu_top = 0;
            UpdateFpuRoundingMode(state);
        } else if ((op->modrm & 0xF8) == 0xE8) {  // FUCOMI
            // ... (already implemented)
            // Compare ST0 with ST(i) and set EFLAGS
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            uint32_t flags = GetFlags32(flags_cache) & ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK |
                                                         fiberish::OF_MASK | fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                flags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                flags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                flags |= fiberish::CF_MASK;
            }
            SetFlags32AndSyncParityState(flags_cache, flags);
        } else if ((op->modrm & 0xF8) == 0xF0) {  // FCOMI
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            uint32_t flags = GetFlags32(flags_cache) & ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK |
                                                         fiberish::OF_MASK | fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                flags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                flags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                flags |= fiberish::CF_MASK;
            }
            SetFlags32AndSyncParityState(flags_cache, flags);
        } else {
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        switch (subop) {
            case 0:  // FILD m32int
            {
                auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int32_t val = (int32_t)*val_res;
                float80 t = f80_from_int(val);
                FpuPush(state, &t);
                break;
            }
            case 1:  // FISTTP m32int
            {
                int32_t val = (int32_t)std::trunc(f80_to_double(FpuTop(state, 0)));
                if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, (uint32_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 2:  // FIST m32int
            {
                float80 st0 = FpuTop(state, 0);
                int32_t val = (int32_t)f80_to_int(st0);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, (uint32_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FISTP m32int
            {
                float80 st0 = FpuTop(state, 0);
                int32_t val = (int32_t)f80_to_int(st0);
                if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, addr, (uint32_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 5:  // FLD m80fp
            {
                auto low_res = ReadMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!low_res) return LogicFlow::ExitOnCurrentEIP;
                auto high_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 8, utlb, op);
                if (!high_res) return LogicFlow::ExitOnCurrentEIP;

                float80 f;
                f.signif = *low_res;
                f.signExp = *high_res;
                FpuPush(state, &f);
                break;
            }
            case 7:  // FSTP m80fp
            {
                float80 f = FpuTop(state, 0);
                if (!WriteMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, f.signif, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 8, f.signExp, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DC(LogicFuncParams) {
    // DC: FPU Arith m64 (double)
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // FADD ST(i), ST0 etc.
        // DC C0+i: FADD ST(i), ST0
        int idx = op->modrm & 7;
        float80& dest = FpuTop(state, idx);
        float80 src = FpuTop(state, 0);

        switch (subop) {
            case 0:
                dest = f80_add(dest, src);
                break;  // FADD
            case 1:
                dest = f80_mul(dest, src);
                break;  // FMUL
            case 4:
                dest = f80_sub(src, dest);
                break;  // FSUBR ST(i), ST0
            case 5:
                dest = f80_sub(dest, src);
                break;  // FSUB ST(i), ST0
            case 6:
                dest = f80_div(src, dest);
                break;  // FDIVR ST(i), ST0
            case 7:
                dest = f80_div(dest, src);
                break;  // FDIV ST(i), ST0
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        auto val_res = ReadF64(state, op, utlb);
        if (!val_res) return LogicFlow::ExitOnCurrentEIP;
        float80 val = *val_res;
        float80& st0 = FpuTop(state, 0);
        switch (subop) {
            case 0:
                st0 = f80_add(st0, val);
                break;  // FADD
            case 1:
                st0 = f80_mul(st0, val);
                break;  // FMUL
            case 2:     // FCOM m64
                if (f80_lt(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                break;
            case 3:  // FCOMP m64
                if (f80_lt(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
                else if (f80_eq(st0, val))
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
                else
                    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
                FpuPop(state);
                break;
            case 4:
                st0 = f80_sub(st0, val);
                break;  // FSUB
            case 5:
                st0 = f80_sub(val, st0);
                break;  // FSUBR
            case 6:
                st0 = f80_div(st0, val);
                break;  // FDIV
            case 7:
                st0 = f80_div(val, st0);
                break;  // FDIVR
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DD(LogicFuncParams) {
    // DD: Load/Store m64
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // DD C0+i: FFREE ST(i)
        // DD D0+i: FST ST(i) (Store ST0 to STi)
        // DD D8+i: FSTP ST(i)
        // DD E0+i: FUCOM ST(i)
        // DD E8+i: FUCOMP ST(i)
        if (subop == 0) {  // FFREE ST(i)
            // Just mark as empty (Tag word = 11 for this register)
            state->ctx.fpu_tw |= (3 << ((state->ctx.fpu_top + (op->modrm & 7)) & 7) * 2);
        } else if (subop == 2) {  // FST ST(i)
            FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
        } else if (subop == 3) {  // FSTP ST(i)
            FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
            FpuPop(state);
        } else if (subop == 4 || subop == 5) {  // FUCOM / FUCOMP ST(i)
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            if (f80_uncomparable(st0, sti)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4500;  // C3=1, C2=1, C0=1
            } else if (f80_eq(st0, sti)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;  // C3=1
            } else if (f80_lt(st0, sti)) {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;  // C0=1
            } else {
                state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
            }
            if (subop == 5) FpuPop(state);  // FUCOMP
        } else {
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        switch (subop) {
            case 0:  // FLD m64
            {
                // ReadF64 handles Blocking Read and returns on fault.
                auto t_res = ReadF64(state, op, utlb);
                if (!t_res) return LogicFlow::ExitOnCurrentEIP;
                float80 t = *t_res;
                FpuPush(state, &t);
                break;
            }
            case 2:  // FST m64fp
            {
                uint32_t addr = ComputeLinearAddress(state, op);
                double d = f80_to_double(FpuTop(state, 0));
                if (!WriteMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, *(uint64_t*)&d, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FSTP m64fp
            {
                uint32_t addr = ComputeLinearAddress(state, op);
                double d = f80_to_double(FpuTop(state, 0));
                if (!WriteMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, *(uint64_t*)&d, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 4:  // FRSTOR m94/108byte (partial env restore)
            {
                uint32_t addr = ComputeLinearAddress(state, op);
                auto cw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 0, utlb, op);
                if (!cw_res) return LogicFlow::ExitOnCurrentEIP;
                auto sw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 2, utlb, op);
                if (!sw_res) return LogicFlow::ExitOnCurrentEIP;
                auto tw_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 4, utlb, op);
                if (!tw_res) return LogicFlow::ExitOnCurrentEIP;
                state->ctx.fpu_cw = *cw_res;
                state->ctx.fpu_sw = *sw_res;
                state->ctx.fpu_tw = *tw_res;
                state->ctx.fpu_top = (state->ctx.fpu_sw >> 11) & 7;
                for (int i = 0; i < 8; ++i) {
                    uint32_t reg_addr = addr + 28u + (uint32_t)(i * 10);
                    auto low_res = ReadMem<uint64_t, OpOnTLBMiss::Blocking>(state, reg_addr, utlb, op);
                    if (!low_res) return LogicFlow::ExitOnCurrentEIP;
                    auto high_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, reg_addr + 8, utlb, op);
                    if (!high_res) return LogicFlow::ExitOnCurrentEIP;
                    state->ctx.fpu_regs[i].signif = *low_res;
                    state->ctx.fpu_regs[i].signExp = *high_res;
                }
                UpdateFpuRoundingMode(state);
                UpdateFSW(state);
                break;
            }
            case 6:  // FNSAVE m94/108byte (partial env store)
            {
                uint32_t addr = ComputeLinearAddress(state, op);
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 0, state->ctx.fpu_cw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 2, state->ctx.fpu_sw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 4, state->ctx.fpu_tw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                for (int i = 0; i < 8; ++i) {
                    uint32_t reg_addr = addr + 28u + (uint32_t)(i * 10);
                    float80 f = state->ctx.fpu_regs[i];
                    if (!WriteMem<uint64_t, OpOnTLBMiss::Blocking>(state, reg_addr, f.signif, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, reg_addr + 8, f.signExp, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                }
                // x87 save also resets FPU state
                state->ctx.fpu_cw = 0x037F;
                state->ctx.fpu_sw = 0;
                state->ctx.fpu_tw = 0xFFFF;
                state->ctx.fpu_top = 0;
                UpdateFpuRoundingMode(state);
                UpdateFSW(state);
                break;
            }
            case 7:  // FNSTSW m2byte
            {
                uint32_t addr = ComputeLinearAddress(state, op);
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, state->ctx.fpu_sw, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DE(LogicFuncParams) {
    // DE: Arith (Pop)
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        if (op->modrm == 0xD9) {  // FCOMPP
            float80 st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            SetFpuCompareFlags(state, st0, st1);
            FpuPop(state);
            FpuPop(state);
            return LogicFlow::Continue;
        }

        // DE C0-F7
        // DE C1: FADDP ST(1), ST0 (Add ST0 to ST1 and Pop)
        int idx = op->modrm & 7;
        float80& dest = FpuTop(state, idx);
        float80 src = FpuTop(state, 0);

        switch (subop) {
            case 0:
                dest = f80_add(dest, src);
                break;  // FADDP
            case 1:
                dest = f80_mul(dest, src);
                break;  // FMULP
            case 4:
                dest = f80_sub(src, dest);
                break;  // FSUBRP (dest = src - dest)
            case 5:
                dest = f80_sub(dest, src);
                break;  // FSUBP (dest = dest - src)
            case 6:
                dest = f80_div(src, dest);
                break;  // FDIVRP (dest = src / dest)
            case 7:
                dest = f80_div(dest, src);
                break;  // FDIVP (dest = dest / src)
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
        FpuPop(state);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        switch (subop) {
            case 0:  // FIADD m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_add(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 1:  // FIMUL m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_mul(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 2:  // FICOM m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                SetFpuCompareFlags(state, FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 3:  // FICOMP m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                SetFpuCompareFlags(state, FpuTop(state, 0), f80_from_int(val));
                FpuPop(state);
                break;
            }
            case 4:  // FISUB m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_sub(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 5:  // FISUBR m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_sub(f80_from_int(val), FpuTop(state, 0));
                break;
            }
            case 6:  // FIDIV m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_div(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 7:  // FIDIVR m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_div(f80_from_int(val), FpuTop(state, 0));
                break;
            }
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpFpu_DF(LogicFuncParams) {
    // DF: m16 Int / Misc
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        if ((op->modrm & 0xF8) == 0xC0) {  // FFREEP ST(i)
            int idx = op->modrm & 7;
            int phys = (state->ctx.fpu_top + idx) & 7;
            state->ctx.fpu_tw |= (3 << (phys * 2));  // mark ST(i) empty
            FpuPop(state);                           // pop ST(0)
        } else if (op->modrm == 0xE0) {              // FNSTSW AX
            state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFF0000) | state->ctx.fpu_sw;
        } else if ((op->modrm & 0xF8) == 0xE8) {  // FUCOMIP
            // ... (already implemented)
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            uint32_t flags = GetFlags32(flags_cache) & ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK |
                                                         fiberish::OF_MASK | fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                flags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                flags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                flags |= fiberish::CF_MASK;
            }
            SetFlags32AndSyncParityState(flags_cache, flags);
            FpuPop(state);
        } else if ((op->modrm & 0xF8) == 0xF0) {  // FCOMIP
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            uint32_t flags = GetFlags32(flags_cache) & ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK |
                                                         fiberish::OF_MASK | fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                flags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                flags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                flags |= fiberish::CF_MASK;
            }
            SetFlags32AndSyncParityState(flags_cache, flags);
            FpuPop(state);
        } else {
            state->fault_vector = 6;
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        switch (subop) {
            case 0:  // FILD m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                float80 f = f80_from_int(val);
                FpuPush(state, &f);
                break;
            }
            case 1:  // FISTTP m16int
            {
                int16_t val = (int16_t)std::trunc(f80_to_double(FpuTop(state, 0)));
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, (uint16_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 2:  // FIST m16int
            {
                float80 st0 = FpuTop(state, 0);
                int16_t val = (int16_t)f80_to_int(st0);
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, (uint16_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FISTP m16int
            {
                float80 st0 = FpuTop(state, 0);
                int16_t val = (int16_t)f80_to_int(st0);
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr, (uint16_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 5:  // FILD m64int
            {
                auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int64_t val = (int64_t)*val_res;
                float80 f = f80_from_int(val);
                FpuPush(state, &f);
                break;
            }
            case 4:  // FBLD m80bcd
            {
                auto bcd_res = ReadPackedBcd80(state, addr, utlb, op);
                if (!bcd_res) return LogicFlow::ExitOnCurrentEIP;
                FpuPush(state, &(*bcd_res));
                break;
            }
            case 6:  // FBSTP m80bcd
            {
                if (!WritePackedBcd80(state, addr, FpuTop(state, 0), utlb, op)) return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 7:  // FISTP m64int
            {
                float80 st0 = FpuTop(state, 0);
                int64_t val = (int64_t)f80_to_int(st0);
                if (!WriteMem<uint64_t, OpOnTLBMiss::Blocking>(state, addr, (uint64_t)val, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            default:
                state->fault_vector = 6;
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

}  // namespace op

}  // namespace fiberish
