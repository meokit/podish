// FPU Instructions
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include <cstring>
#include <optional>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

static inline void UpdateFSW(EmuState* state) {
    state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x3800) | ((state->ctx.fpu_top & 7) << 11);
}

// FPU Stack Helpers
static inline void FpuPush(EmuState* state, const float80* val) {
    state->ctx.fpu_top = (state->ctx.fpu_top - 1) & 7;
    std::memcpy(&state->ctx.fpu_regs[state->ctx.fpu_top], val, sizeof(float80));
    state->ctx.fpu_tw &= ~(3 << (state->ctx.fpu_top * 2));  // Mark valid (00)
    UpdateFSW(state);
}

static inline float80 FpuPop(EmuState* state) {
    float80 val;
    std::memcpy(&val, &state->ctx.fpu_regs[state->ctx.fpu_top], sizeof(float80));
    state->ctx.fpu_tw |= (3 << (state->ctx.fpu_top * 2));  // Mark empty
    state->ctx.fpu_top = (state->ctx.fpu_top + 1) & 7;
    UpdateFSW(state);
    return val;
}

static inline float80& FpuTop(EmuState* state, int index) {
    return state->ctx.fpu_regs[(state->ctx.fpu_top + index) & 7];
}

static inline void UpdateFpuRoundingMode(EmuState* state) { f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw); }

// Helper to read float32 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
static inline mem::MemResult<float80> ReadF32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, ComputeLinearAddress(state, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint32_t val = *res;
    float f = *(float*)&val;
    return f80_from_double((double)f);
}

// Helper to read float64 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
static inline mem::MemResult<float80> ReadF64(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res = ReadMem<uint64_t, OpOnTLBMiss::Blocking>(state, ComputeLinearAddress(state, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint64_t val = *res;
    return f80_from_double(*(double*)&val);
}

static FORCE_INLINE LogicFlow OpFpu_D8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // D8: FPU Arith m32
    uint8_t subop = (op->modrm >> 3) & 7;
    auto val_res = ReadF32(state, op, utlb);
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
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_D9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // D9 C0-FF: FPU Instructions with Regs
        // Map 0xD9C0 -> index
        uint8_t op_byte = op->modrm;

        if (op_byte == 0xC0) {  // FLD ST(0) (DUP) -> D9 C0
            float80 t = FpuTop(state, 0);
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
        } else if (op_byte == 0xF1) {  // FYL2X: ST(1) = ST(1) * log2(ST(0)); Pop ST(0)
            float80 x = FpuPop(state);
            float80& y = FpuTop(state, 0);
            y = f80_mul(y, f80_log2(x));
        } else if (op_byte == 0xF8) {  // FPREM
            float80& st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            st0 = f80_rem(st0, st1);
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
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
                state->ctx.fpu_cw = *cw_res;
                // TODO: full env load (SW, TW, IP, CS, OP, DS)
                UpdateFpuRoundingMode(state);
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
                if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, addr + 2, state->ctx.fpu_sw, utlb, op))
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DA(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // DA: Int Arith m32
    if ((op->modrm >> 6) == 3) {
        // DA C0-C7: FCMOVB
        // DA C8-CF: FCMOVE
        // DA D0-D7: FCMOVBE
        // DA D8-DF: FCMOVU
        int idx = op->modrm & 7;
        bool pass = false;
        switch ((op->modrm >> 3) & 7) {
            case 0:
                pass = (state->ctx.eflags & fiberish::CF_MASK);
                break;  // FCMOVB
            case 1:
                pass = (state->ctx.eflags & fiberish::ZF_MASK);
                break;  // FCMOVE
            case 2:
                pass = (state->ctx.eflags & (fiberish::CF_MASK | fiberish::ZF_MASK));
                break;  // FCMOVBE
            case 3:
                pass = (state->ctx.eflags & fiberish::PF_MASK);
                break;  // FCMOVU
        }
        if (pass) FpuTop(state, 0) = FpuTop(state, idx);
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DB(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
                    pass = !(state->ctx.eflags & fiberish::CF_MASK);
                    break;  // FCMOVNB
                case 1:
                    pass = !(state->ctx.eflags & fiberish::ZF_MASK);
                    break;  // FCMOVNE
                case 2:
                    pass = !(state->ctx.eflags & (fiberish::CF_MASK | fiberish::ZF_MASK));
                    break;  // FCMOVNBE
                case 3:
                    pass = !(state->ctx.eflags & fiberish::PF_MASK);
                    break;  // FCMOVNU
            }
            if (pass) FpuTop(state, 0) = FpuTop(state, idx);
        } else if (op->modrm == 0xE3) {  // FINIT
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

            state->ctx.eflags &= ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK | fiberish::OF_MASK |
                                   fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                state->ctx.eflags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                state->ctx.eflags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                state->ctx.eflags |= fiberish::CF_MASK;
            }
        } else if ((op->modrm & 0xF8) == 0xF0) {  // FCOMI
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            state->ctx.eflags &= ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK | fiberish::OF_MASK |
                                   fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                state->ctx.eflags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                state->ctx.eflags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                state->ctx.eflags |= fiberish::CF_MASK;
            }
        } else {
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DC(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
                dest = f80_sub(dest, src);
                break;  // FSUB (dest - src)
            case 5:
                dest = f80_sub(src, dest);
                break;  // FSUBR (src - dest)
            case 6:
                dest = f80_div(dest, src);
                break;  // FDIV
            case 7:
                dest = f80_div(src, dest);
                break;  // FDIVR
            default:
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DD(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // DD: Load/Store m64
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        // DD D0+i: FST ST(i) (Store ST0 to STi)
        // DD D8+i: FSTP ST(i)
        if (subop == 2) {  // FST ST(i)
            FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
        } else if (subop == 3) {  // FSTP ST(i)
            FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
            FpuPop(state);
        } else {
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
            default:
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DE(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // DE: Arith (Pop)
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

static FORCE_INLINE LogicFlow OpFpu_DF(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // DF: m16 Int / Misc
    uint8_t subop = (op->modrm >> 3) & 7;

    if ((op->modrm >> 6) == 3) {
        if (op->modrm == 0xE0) {  // FNSTSW AX
            state->ctx.regs[EAX] = (state->ctx.regs[EAX] & 0xFFFF0000) | state->ctx.fpu_sw;
        } else if ((op->modrm & 0xF8) == 0xE8) {  // FUCOMIP
            // ... (already implemented)
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            state->ctx.eflags &= ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK | fiberish::OF_MASK |
                                   fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                state->ctx.eflags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                state->ctx.eflags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                state->ctx.eflags |= fiberish::CF_MASK;
            }
            FpuPop(state);
        } else if ((op->modrm & 0xF8) == 0xF0) {  // FCOMIP
            int idx = op->modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);

            state->ctx.eflags &= ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK | fiberish::OF_MASK |
                                   fiberish::SF_MASK | fiberish::AF_MASK);
            if (f80_uncomparable(st0, sti)) {
                state->ctx.eflags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
            } else if (f80_eq(st0, sti)) {
                state->ctx.eflags |= fiberish::ZF_MASK;
            } else if (f80_lt(st0, sti)) {
                state->ctx.eflags |= fiberish::CF_MASK;
            }
            FpuPop(state);
        } else {
            if (!state->hooks.on_invalid_opcode(state)) {
                state->status = EmuStatus::Fault;
                state->fault_vector = 6;
            }
            if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
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
                if (!state->hooks.on_invalid_opcode(state)) {
                    state->status = EmuStatus::Fault;
                    state->fault_vector = 6;
                }
                if (state->status == EmuStatus::Fault) return LogicFlow::ExitOnCurrentEIP;
        }
    }
    return LogicFlow::Continue;
}

void RegisterFpuOps() {
    g_Handlers[0xD8] = DispatchWrapper<OpFpu_D8>;
    g_Handlers[0xD9] = DispatchWrapper<OpFpu_D9>;
    g_Handlers[0xDA] = DispatchWrapper<OpFpu_DA>;
    g_Handlers[0xDB] = DispatchWrapper<OpFpu_DB>;
    g_Handlers[0xDC] = DispatchWrapper<OpFpu_DC>;
    g_Handlers[0xDD] = DispatchWrapper<OpFpu_DD>;
    g_Handlers[0xDE] = DispatchWrapper<OpFpu_DE>;
    g_Handlers[0xDF] = DispatchWrapper<OpFpu_DF>;
}

}  // namespace fiberish