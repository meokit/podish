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

inline void SyncFpuStatusFromSoft(EmuState* state) { f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw); }

inline LogicFlow ContinueWithSyncedFpuStatus(EmuState* state) {
    SyncFpuStatusFromSoft(state);
    return LogicFlow::Continue;
}

inline void RecordFpuInstruction(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t operand_modrm, uint8_t opcode) {
    state->ctx.fpu_ip = GetNextEip(state, op) - GetOpLength(state, op);
    state->ctx.fpu_opcode = (uint16_t)(((opcode & 7) << 8) | operand_modrm);
    // This emulator's segment model has bases but no architectural selectors.
    state->ctx.fpu_cs = 0;
    if ((operand_modrm >> 6) != 3) {
        state->ctx.fpu_dp = ComputeLinearAddress(state, gpr, op);
        state->ctx.fpu_ds = 0;
    }
}

inline void ResetFpuEnvironment(EmuState* state) {
    state->ctx.fpu_cw = 0x037F;
    state->ctx.fpu_sw = 0;
    state->ctx.fpu_tw = 0xFFFF;
    state->ctx.fpu_top = 0;
    state->ctx.fpu_ip = 0;
    state->ctx.fpu_dp = 0;
    state->ctx.fpu_opcode = 0;
    state->ctx.fpu_cs = 0;
    state->ctx.fpu_ds = 0;
    UpdateFpuRoundingMode(state);
}

struct FpuEnvironment {
    uint16_t cw, sw, tw, opcode, cs, ds;
    uint32_t ip, dp;
};

inline std::optional<FpuEnvironment> ReadFpuEnvironment(EmuState* state, GprCache& gpr, uint32_t addr, bool operand16,
                                                        mem::MicroTLB* utlb, DecodedOp* op) {
    uint16_t words[14]{};
    const int count = operand16 ? 7 : 14;
    for (int i = 0; i < count; ++i) {
        auto word = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + (uint32_t)(2 * i), utlb, op);
        if (!word) return std::nullopt;
        words[i] = *word;
    }
    if (operand16) return FpuEnvironment{words[0], words[1], words[2], 0, words[4], words[6], words[3], words[5]};
    return FpuEnvironment{words[0],
                          words[2],
                          words[4],
                          (uint16_t)(words[9] & 0x7FF),
                          words[8],
                          words[12],
                          (uint32_t)words[6] | ((uint32_t)words[7] << 16),
                          (uint32_t)words[10] | ((uint32_t)words[11] << 16)};
}

inline void RestoreFpuEnvironment(EmuState* state, const FpuEnvironment& env) {
    state->ctx.fpu_cw = env.cw;
    state->ctx.fpu_sw = env.sw;
    state->ctx.fpu_tw = env.tw;
    state->ctx.fpu_top = (env.sw >> 11) & 7;
    state->ctx.fpu_ip = env.ip;
    state->ctx.fpu_dp = env.dp;
    state->ctx.fpu_opcode = env.opcode;
    state->ctx.fpu_cs = env.cs;
    state->ctx.fpu_ds = env.ds;
    UpdateFpuRoundingMode(state);
}

inline bool WriteFpuEnvironment(EmuState* state, GprCache& gpr, uint32_t addr, bool operand16, mem::MicroTLB* utlb,
                                DecodedOp* op) {
    SyncFpuStatusFromSoft(state);
    UpdateFSW(state);
    uint16_t words[14];
    int count;
    if (operand16) {
        uint16_t small[7] = {
            state->ctx.fpu_cw, state->ctx.fpu_sw,           state->ctx.fpu_tw, (uint16_t)state->ctx.fpu_ip,
            state->ctx.fpu_cs, (uint16_t)state->ctx.fpu_dp, state->ctx.fpu_ds};
        std::memcpy(words, small, sizeof(small));
        count = 7;
    } else {
        uint16_t large[14] = {state->ctx.fpu_cw,           0xFFFF,
                              state->ctx.fpu_sw,           0xFFFF,
                              state->ctx.fpu_tw,           0xFFFF,
                              (uint16_t)state->ctx.fpu_ip, (uint16_t)(state->ctx.fpu_ip >> 16),
                              state->ctx.fpu_cs,           (uint16_t)(state->ctx.fpu_opcode & 0x7FF),
                              (uint16_t)state->ctx.fpu_dp, (uint16_t)(state->ctx.fpu_dp >> 16),
                              state->ctx.fpu_ds,           0xFFFF};
        std::memcpy(words, large, sizeof(large));
        count = 14;
    }
    for (int i = 0; i < count; ++i)
        if (!WriteMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + (uint32_t)(2 * i), words[i], utlb,
                                                                  op))
            return false;
    return true;
}

inline bool FpuTrigInRange(EmuState* state, float80 value) {
    // Trigonometric range reduction is only defined for finite |x| < 2^63.
    // An out-of-range finite operand leaves the stack unchanged (no TAN/COS push).
    if (f80_is_supported(value) && !f80_isnan(value) && !f80_isinf(value) && (value.signExp & 0x7FFF) >= 16383 + 63) {
        state->ctx.fpu_sw |= 0x0400;
        return false;
    }
    state->ctx.fpu_sw &= ~0x0400;
    return true;
}

inline float80 FpuScale(float80 value, float80 exponent) {
    if (!f80_is_supported(value) || !f80_is_supported(exponent)) return f80_mul(F80_INF, F80_ZERO);
    if (f80_isnan(value) || f80_isnan(exponent)) return f80_add(value, exponent);
    if (f80_isinf(exponent)) {
        const bool down = (exponent.signExp & 0x8000) != 0;
        if ((!down && f80_iszero(value)) || (down && f80_isinf(value))) return f80_mul(F80_INF, F80_ZERO);
        float80 result = down ? F80_ZERO : F80_INF;
        result.signExp |= value.signExp & 0x8000;
        return result;
    }
    // FSCALE always truncates ST(1), independently of RC. Clamp very large
    // exponents before integer conversion, avoiding indefinite/overflow casts.
    int scale;
    if ((exponent.signExp & 0x7FFF) >= 16383 + 16)
        scale = (exponent.signExp & 0x8000) ? -65536 : 65536;
    else
        scale = (int)f80_trunc_to_int(exponent);
    return f80_scale(value, scale);
}

inline void MergeFpuInvalidStatus(EmuState* state, bool invalid) {
    if (invalid) state->ctx.fpu_sw |= 0x0001;
}

inline void MergeFcomInvalidStatus(EmuState* state, float80 a, float80 b, bool quiet_on_qnan) {
    if (!f80_uncomparable(a, b)) return;
    bool invalid = !quiet_on_qnan || f80_is_signaling_nan(a) || f80_is_signaling_nan(b);
    MergeFpuInvalidStatus(state, invalid);
}

inline void SetFcomiFlags(uint64_t& flags_cache, float80 a, float80 b) {
    bool unordered = f80_uncomparable(a, b);
    uint32_t flags = GetFlags32(flags_cache) & ~(fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK |
                                                 fiberish::OF_MASK | fiberish::SF_MASK | fiberish::AF_MASK);
    if (unordered) {
        flags |= (fiberish::ZF_MASK | fiberish::PF_MASK | fiberish::CF_MASK);
    } else if (f80_eq(a, b)) {
        flags |= fiberish::ZF_MASK;
    } else if (f80_lt(a, b)) {
        flags |= fiberish::CF_MASK;
    }
    SetFlags32AndSyncParityState(flags_cache, flags);
}

inline void MergeFcomiInvalidStatus(EmuState* state, float80 a, float80 b, bool quiet_on_qnan) {
    MergeFcomInvalidStatus(state, a, b, quiet_on_qnan);
}

inline void SetFpuCompareFlags(EmuState* state, float80 a, float80 b) {
    bool lt = f80_lt(a, b);
    bool eq = f80_eq(a, b);
    bool unordered = f80_uncomparable(a, b);

    state->ctx.fpu_sw &= ~0x4500;  // Clear C3/C2/C0
    if (unordered) {
        state->ctx.fpu_sw |= 0x4500;  // C3=1, C2=1, C0=1
    } else if (eq) {
        state->ctx.fpu_sw |= 0x4000;  // C3=1
    } else if (lt) {
        state->ctx.fpu_sw |= 0x0100;  // C0=1
    }
}

inline bool IsPackedBcdIndefinite(const uint8_t bytes[10]) {
    static constexpr uint8_t kPackedBcdIndefinite[10] = {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0xFF, 0xFF};
    return std::memcmp(bytes, kPackedBcdIndefinite, sizeof(kPackedBcdIndefinite)) == 0;
}

inline mem::MemResult<float80> ReadPackedBcd80(EmuState* state, GprCache& gpr, uint32_t addr, mem::MicroTLB* utlb,
                                               DecodedOp* op) {
    uint8_t raw[10];
    double value = 0.0;
    double place = 1.0;
    for (int i = 0; i < 9; ++i) {
        auto b_res = ReadMem<uint8_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + (uint32_t)i, utlb, op);
        if (!b_res) return std::unexpected(b_res.error());
        uint8_t b = *b_res;
        raw[i] = b;
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

    auto sign_res = ReadMem<uint8_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + 9, utlb, op);
    if (!sign_res) return std::unexpected(sign_res.error());
    raw[9] = *sign_res;
    if (IsPackedBcdIndefinite(raw)) {
        // Intel documents FBLD of the packed-BCD indefinite encoding as
        // undefined. Preserve the matching ext80 indefinite bit-pattern so the
        // sentinel does not collapse into -0 during reload.
        return float80{.signif = UINT64_C(0xC000000000000000), .signExp = UINT16_C(0xFFFF)};
    }
    if ((*sign_res & 0x80) != 0) {
        value = -value;
    }

    return f80_from_double(value);
}

// Conversion can raise softfloat exceptions. Publish them only with the store.
template <typename T, typename Convert>
inline bool WriteConvertedFpuStore(EmuState* state, GprCache& gpr, uint32_t addr, mem::MicroTLB* utlb, DecodedOp* op,
                                   Convert&& convert) {
    if (const auto target = state->mmu.try_write_target_tlb<T>(addr, utlb)) {
        mem::StoreGuestValue(state, *target, static_cast<T>(convert()));
        return true;
    }

    const auto previous = f80_save_environment();
    const T value = convert();
    const auto converted = f80_save_environment();
    f80_restore_environment(previous);
    if (!AccessGuestMemSlow<true>(state, gpr, op, [&] { return state->mmu.write_slow<T>(addr, value); })) return false;
    f80_restore_environment(converted);
    return true;
}

template <typename T, auto Convert>
inline bool WriteFpuInteger(EmuState* state, GprCache& gpr, uint32_t addr, bool truncate, mem::MicroTLB* utlb,
                            DecodedOp* op) {
    bool invalid = false;
    if (!WriteConvertedFpuStore<T>(state, gpr, addr, utlb, op,
                                   [&] { return static_cast<T>(Convert(FpuTop(state, 0), truncate, &invalid)); }))
        return false;
    MergeFpuInvalidStatus(state, invalid);
    return true;
}

inline bool WritePackedBcd80(EmuState* state, GprCache& gpr, uint32_t addr, float80 value, mem::MicroTLB* utlb,
                             DecodedOp* op) {
    static constexpr int64_t kPackedBcdMax = INT64_C(999999999999999999);
    bool invalid = false;
    if (!WriteConvertedFpuStore<std::array<uint8_t, 10>>(state, gpr, addr, utlb, op, [&] {
            const int64_t truncated = f80_to_int64_checked(value, true, &invalid);
            invalid |= truncated < -kPackedBcdMax || truncated > kPackedBcdMax;
            if (invalid) return std::array<uint8_t, 10>{0, 0, 0, 0, 0, 0, 0, 0xC0, 0xFF, 0xFF};

            std::array<uint8_t, 10> packed{};
            const bool negative = truncated < 0;
            uint64_t digits = negative ? static_cast<uint64_t>(-truncated) : static_cast<uint64_t>(truncated);
            for (unsigned i = 0; i < 9; ++i) {
                const auto lo = static_cast<uint8_t>(digits % 10);
                digits /= 10;
                const auto hi = static_cast<uint8_t>(digits % 10);
                digits /= 10;
                packed[i] = (hi << 4) | lo;
            }
            packed[9] = negative ? 0x80 : 0;
            return packed;
        }))
        return false;
    MergeFpuInvalidStatus(state, invalid);
    return true;
}

// Helper to read float32 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
inline mem::MemResult<float80> ReadF32(EmuState* state, GprCache& gpr, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res =
        ReadMem<uint32_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, ComputeLinearAddress(state, gpr, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint32_t val = *res;
    float f;
    std::memcpy(&f, &val, sizeof(f));
    return f80_from_double((double)f);
}

// Helper to read float64 from memory and convert to float80
// Uses Blocking Read (fail_on_tlb_miss = false)
inline mem::MemResult<float80> ReadF64(EmuState* state, GprCache& gpr, DecodedOp* op, mem::MicroTLB* utlb) {
    auto res =
        ReadMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, ComputeLinearAddress(state, gpr, op), utlb, op);
    if (!res) return std::unexpected(res.error());
    uint64_t val = *res;
    double d;
    std::memcpy(&d, &val, sizeof(d));
    return f80_from_double(d);
}

namespace op {

// Keep the decoded r/m index dynamic: it selects ST(i) relative to the current TOP.
template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE uint8_t FpuModRM(uint8_t operand_modrm) {
    uint8_t modrm = operand_modrm;
    if constexpr (Form == AddrModeSpec::ModReg) modrm |= 0xc0;
    if constexpr (Extension != GroupOpSpec::Dynamic) {
        modrm = static_cast<uint8_t>((modrm & 0xc7) | (static_cast<uint8_t>(Extension) << 3));
    }
    return modrm;
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_D8_Impl(LogicFuncParams) {
    // D8: FPU arithmetic
    uint8_t subop = GroupOperation<Extension>(operand_modrm);
    float80& st0 = FpuTop(state, 0);

    // Register form: D8 C0-FF (operate with ST(i))
    if (OperandIsReg<Form>(operand_modrm)) {
        uint8_t sti_idx = operand_modrm & 7;
        float80& sti = FpuTop(state, sti_idx);

        switch (subop) {
            case 0:  // FADD ST(0), ST(i)
                st0 = f80_add(st0, sti);
                break;
            case 1:  // FMUL ST(0), ST(i)
                st0 = f80_mul(st0, sti);
                break;
            case 2:  // FCOM ST(i)
                SetFpuCompareFlags(state, st0, sti);
                MergeFcomInvalidStatus(state, st0, sti, false);
                break;
            case 3:  // FCOMP ST(i)
                SetFpuCompareFlags(state, st0, sti);
                MergeFcomInvalidStatus(state, st0, sti, false);
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
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
        RecordFpuInstruction(state, gpr, op, operand_modrm, 0xD8);
        return ContinueWithSyncedFpuStatus(state);
    }

    // Memory form: D8 /r m32fp
    auto val_res = ReadF32(state, gpr, op, utlb);
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
            SetFpuCompareFlags(state, st0, val);
            MergeFcomInvalidStatus(state, st0, val, false);
            break;
        case 3:  // FCOMP (Compare and Pop)
            SetFpuCompareFlags(state, st0, val);
            MergeFcomInvalidStatus(state, st0, val, false);
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
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
    }
    RecordFpuInstruction(state, gpr, op, operand_modrm, 0xD8);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_D9_Impl(LogicFuncParams) {
    uint8_t subop = GroupOperation<Extension>(operand_modrm);

    if (OperandIsReg<Form>(operand_modrm)) {
        // D9 C0-FF: FPU Instructions with Regs
        // Map 0xD9C0 -> index
        uint8_t op_byte = FpuModRM<Extension, Form>(operand_modrm);

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
            SetFpuCompareFlags(state, st0, zero);
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
            if (FpuTrigInRange(state, st0)) {
                st0 = f80_tan(st0);
                float80 t = ConstF80_One();
                FpuPush(state, &t);
            }
        } else if (op_byte == 0xF3) {  // FPATAN
            float80 y = FpuTop(state, 1);
            float80 x = FpuPop(state);
            FpuTop(state, 0) = f80_atan2(y, x);
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
        } else if (op_byte == 0xF5 || op_byte == 0xF8) {  // FPREM1 / FPREM
            float80& st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            st0 = op_byte == 0xF5 ? f80_rem(st0, st1) : f80_mod(st0, st1);
            // The remainder helpers complete the reduction in one step.
            state->ctx.fpu_sw &= ~0x0400;
        } else if (op_byte == 0xF6) {      // FDECSTP
            state->ctx.fpu_sw &= ~0x0200;  // C1 is cleared; tags and physical registers are unchanged.
            state->ctx.fpu_top = (state->ctx.fpu_top - 1) & 7;
            UpdateFSW(state);
        } else if (op_byte == 0xF7) {  // FINCSTP
            state->ctx.fpu_sw &= ~0x0200;
            state->ctx.fpu_top = (state->ctx.fpu_top + 1) & 7;
            UpdateFSW(state);
        } else if (op_byte == 0xF9) {  // FYL2XP1
            double y = f80_to_double(FpuTop(state, 1));
            double x = f80_to_double(FpuPop(state));
            FpuTop(state, 0) = f80_from_double(y * std::log2(x + 1.0));
        } else if (op_byte == 0xFA) {  // FSQRT
            FpuTop(state, 0) = f80_sqrt(FpuTop(state, 0));
        } else if (op_byte == 0xFB) {  // FSINCOS
            float80 st0 = FpuTop(state, 0);
            if (FpuTrigInRange(state, st0)) {
                FpuTop(state, 0) = f80_sin(st0);
                float80 c = f80_cos(st0);
                FpuPush(state, &c);
            }
        } else if (op_byte == 0xFC) {  // FRNDINT
            FpuTop(state, 0) = f80_round(FpuTop(state, 0));
        } else if (op_byte == 0xFD) {  // FSCALE
            FpuTop(state, 0) = FpuScale(FpuTop(state, 0), FpuTop(state, 1));
        } else if (op_byte == 0xFE) {  // FSIN
            if (FpuTrigInRange(state, FpuTop(state, 0))) FpuTop(state, 0) = f80_sin(FpuTop(state, 0));
        } else if (op_byte == 0xFF) {  // FCOS
            if (FpuTrigInRange(state, FpuTop(state, 0))) FpuTop(state, 0) = f80_cos(FpuTop(state, 0));
        } else {
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        // Memory Access (Blocking)
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        switch (subop) {
            case 0:  // FLD m32
            {
                auto t_res = ReadF32(state, gpr, op, utlb);
                if (!t_res) return LogicFlow::ExitOnCurrentEIP;
                FpuPush(state, &(*t_res));
                break;
            }
            case 2:  // FST m32fp
            {
                if (!WriteConvertedFpuStore<float>(state, gpr, addr, utlb, op,
                                                   [&] { return static_cast<float>(f80_to_double(FpuTop(state, 0))); }))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FSTP m32fp
            {
                if (!WriteConvertedFpuStore<float>(state, gpr, addr, utlb, op,
                                                   [&] { return static_cast<float>(f80_to_double(FpuTop(state, 0))); }))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 4:  // FLDENV m14/28byte
            {
                auto env = ReadFpuEnvironment(state, gpr, addr, GetPrefixes(op).flags.opsize, utlb, op);
                if (!env) return LogicFlow::ExitOnCurrentEIP;
                RestoreFpuEnvironment(state, *env);
                break;
            }
            case 5:  // FLDCW m16
            {
                auto cw_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!cw_res) return LogicFlow::ExitOnCurrentEIP;
                state->ctx.fpu_cw = *cw_res;
                UpdateFpuRoundingMode(state);
                break;
            }
            case 6:  // FNSTENV m14/28byte
            {
                if (!WriteFpuEnvironment(state, gpr, addr, GetPrefixes(op).flags.opsize, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                state->ctx.fpu_cw |= 0x003F;  // FNSTENV masks all exceptions after saving the original CW.
                UpdateFpuRoundingMode(state);
                break;
            }
            case 7:  // FNSTCW m16
            {
                if (!WriteMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, state->ctx.fpu_cw, utlb,
                                                                          op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            default:
                state->fault_vector = 6;
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    if (OperandIsReg<Form>(operand_modrm) || subop < 4) RecordFpuInstruction(state, gpr, op, operand_modrm, 0xD9);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DA_Impl(LogicFuncParams) {
    // DA: Int Arith m32
    if (OperandIsReg<Form>(operand_modrm)) {
        if (FpuModRM<Extension, Form>(operand_modrm) == 0xE9) {  // FUCOMPP
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
            int idx = operand_modrm & 7;
            bool pass = false;
            switch (GroupOperation<Extension>(operand_modrm)) {
                case 0:
                    pass = ReadCF(flags_cache);
                    break;  // FCMOVB
                case 1:
                    pass = ReadZF(flags_cache);
                    break;  // FCMOVE
                case 2:
                    pass = ReadCF(flags_cache) || ReadZF(flags_cache);
                    break;  // FCMOVBE
                case 3:
                    pass = EvaluatePF(flags_cache);
                    break;  // FCMOVU
            }
            if (pass) FpuTop(state, 0) = FpuTop(state, idx);
        }
    } else {
        uint8_t subop = GroupOperation<Extension>(operand_modrm);
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        auto val_res = ReadMem<uint32_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
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
                SetFpuCompareFlags(state, st0, val);
                break;
            case 3:  // FICOMP
                SetFpuCompareFlags(state, st0, val);
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
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDA);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DB_Impl(LogicFuncParams) {
    // DB: FILD/FIST
    const auto subop = GroupOperation<Extension>(operand_modrm);
    const auto modrm = FpuModRM<Extension, Form>(operand_modrm);
    const bool reg_form = OperandIsReg<Form>(operand_modrm);

    if (reg_form) {
        // DB C0-C7: FCMOVNB
        // DB C8-CF: FCMOVNE
        // DB D0-D7: FCMOVNBE
        // DB D8-DF: FCMOVNU
        if (subop < 4) {
            int idx = operand_modrm & 7;
            bool pass = false;
            switch (subop) {
                case 0:
                    pass = !ReadCF(flags_cache);
                    break;  // FCMOVNB
                case 1:
                    pass = !ReadZF(flags_cache);
                    break;  // FCMOVNE
                case 2:
                    pass = !ReadCF(flags_cache) && !ReadZF(flags_cache);
                    break;  // FCMOVNBE
                case 3:
                    pass = !EvaluatePF(flags_cache);
                    break;  // FCMOVNU
            }
            if (pass) FpuTop(state, 0) = FpuTop(state, idx);
        } else if (modrm == 0xE2) {                   // FCLEX
            state->ctx.fpu_sw &= ~(0x00FF | 0x8000);  // Clear exception flags and busy flag
            UpdateFpuRoundingMode(state);
        } else if (modrm == 0xE3) {  // FINIT
            ResetFpuEnvironment(state);
        } else if ((modrm & 0xF8) == 0xE8) {  // FUCOMI
            int idx = operand_modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            SetFcomiFlags(flags_cache, st0, sti);
            MergeFcomiInvalidStatus(state, st0, sti, true);
        } else if ((modrm & 0xF8) == 0xF0) {  // FCOMI
            int idx = operand_modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            SetFcomiFlags(flags_cache, st0, sti);
            MergeFcomiInvalidStatus(state, st0, sti, false);
        } else {
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        switch (subop) {
            case 0:  // FILD m32int
            {
                auto val_res = ReadMem<uint32_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int32_t val = (int32_t)*val_res;
                float80 t = f80_from_int(val);
                FpuPush(state, &t);
                break;
            }
            case 1:  // FISTTP m32int
            {
                if (!WriteFpuInteger<uint32_t, f80_to_int32_checked>(state, gpr, addr, true, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 2:  // FIST m32int
            {
                if (!WriteFpuInteger<uint32_t, f80_to_int32_checked>(state, gpr, addr, false, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FISTP m32int
            {
                if (!WriteFpuInteger<uint32_t, f80_to_int32_checked>(state, gpr, addr, false, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 5:  // FLD m80fp
            {
                auto low_res = ReadMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!low_res) return LogicFlow::ExitOnCurrentEIP;
                auto high_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + 8, utlb, op);
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
                if (!WriteMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, f.signif, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                if (!WriteMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr + 8, f.signExp, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            default:
                state->fault_vector = 6;
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    if (!reg_form || (modrm != 0xE2 && modrm != 0xE3)) RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDB);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DC_Impl(LogicFuncParams) {
    // DC: FPU Arith m64 (double)
    uint8_t subop = GroupOperation<Extension>(operand_modrm);

    if (OperandIsReg<Form>(operand_modrm)) {
        // FADD ST(i), ST0 etc.
        // DC C0+i: FADD ST(i), ST0
        int idx = operand_modrm & 7;
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
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        auto val_res = ReadF64(state, gpr, op, utlb);
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
                SetFpuCompareFlags(state, st0, val);
                MergeFcomInvalidStatus(state, st0, val, false);
                break;
            case 3:  // FCOMP m64
                SetFpuCompareFlags(state, st0, val);
                MergeFcomInvalidStatus(state, st0, val, false);
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
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDC);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DD_Impl(LogicFuncParams) {
    // DD: Load/Store m64
    uint8_t subop = GroupOperation<Extension>(operand_modrm);

    if (OperandIsReg<Form>(operand_modrm)) {
        // DD C0+i: FFREE ST(i)
        // DD D0+i: FST ST(i) (Store ST0 to STi)
        // DD D8+i: FSTP ST(i)
        // DD E0+i: FUCOM ST(i)
        // DD E8+i: FUCOMP ST(i)
        if (subop == 0) {  // FFREE ST(i)
            // Just mark as empty (Tag word = 11 for this register)
            state->ctx.fpu_tw |= (3 << ((state->ctx.fpu_top + (operand_modrm & 7)) & 7) * 2);
        } else if (subop == 2) {  // FST ST(i)
            FpuTop(state, operand_modrm & 7) = FpuTop(state, 0);
        } else if (subop == 3) {  // FSTP ST(i)
            FpuTop(state, operand_modrm & 7) = FpuTop(state, 0);
            FpuPop(state);
        } else if (subop == 4 || subop == 5) {  // FUCOM / FUCOMP ST(i)
            int idx = operand_modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            MergeFcomInvalidStatus(state, st0, sti, true);
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
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        switch (subop) {
            case 0:  // FLD m64
            {
                // ReadF64 handles Blocking Read and returns on fault.
                auto t_res = ReadF64(state, gpr, op, utlb);
                if (!t_res) return LogicFlow::ExitOnCurrentEIP;
                float80 t = *t_res;
                FpuPush(state, &t);
                break;
            }
            case 1:  // FISTTP m64int (SSE3, like the existing m16/m32 forms)
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                if (!WriteFpuInteger<uint64_t, f80_to_int64_checked>(state, gpr, addr, true, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 2:  // FST m64fp
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                if (!WriteConvertedFpuStore<double>(state, gpr, addr, utlb, op,
                                                    [&] { return f80_to_double(FpuTop(state, 0)); }))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FSTP m64fp
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                if (!WriteConvertedFpuStore<double>(state, gpr, addr, utlb, op,
                                                    [&] { return f80_to_double(FpuTop(state, 0)); }))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 4:  // FRSTOR m94/108byte
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                const bool operand16 = GetPrefixes(op).flags.opsize;
                auto env = ReadFpuEnvironment(state, gpr, addr, operand16, utlb, op);
                if (!env) return LogicFlow::ExitOnCurrentEIP;
                float80 registers[8]{};
                for (int i = 0; i < 8; ++i) {
                    uint32_t reg_addr = addr + (operand16 ? 14u : 28u) + (uint32_t)(i * 10);
                    auto low_res = ReadMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, reg_addr, utlb, op);
                    if (!low_res) return LogicFlow::ExitOnCurrentEIP;
                    auto high_res =
                        ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, reg_addr + 8, utlb, op);
                    if (!high_res) return LogicFlow::ExitOnCurrentEIP;
                    registers[i].signif = *low_res;
                    registers[i].signExp = *high_res;
                }
                RestoreFpuEnvironment(state, *env);
                for (int i = 0; i < 8; ++i) FpuTop(state, i) = registers[i];
                break;
            }
            case 6:  // FNSAVE m94/108byte
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                const bool operand16 = GetPrefixes(op).flags.opsize;
                if (!WriteFpuEnvironment(state, gpr, addr, operand16, utlb, op)) return LogicFlow::ExitOnCurrentEIP;
                for (int i = 0; i < 8; ++i) {
                    uint32_t reg_addr = addr + (operand16 ? 14u : 28u) + (uint32_t)(i * 10);
                    float80 f = FpuTop(state, i);  // Save image contains logical ST0..ST7, not physical R0..R7.
                    if (!WriteMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, reg_addr, f.signif, utlb, op))
                        return LogicFlow::ExitOnCurrentEIP;
                    if (!WriteMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, reg_addr + 8, f.signExp, utlb,
                                                                              op))
                        return LogicFlow::ExitOnCurrentEIP;
                }
                // x87 save also resets FPU state
                ResetFpuEnvironment(state);
                break;
            }
            case 7:  // FNSTSW m2byte
            {
                uint32_t addr = ComputeLinearAddress(state, gpr, op);
                SyncFpuStatusFromSoft(state);
                if (!WriteMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, state->ctx.fpu_sw, utlb,
                                                                          op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            default:
                state->fault_vector = 6;
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    if (OperandIsReg<Form>(operand_modrm) || subop < 4) RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDD);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DE_Impl(LogicFuncParams) {
    // DE: Arith (Pop)
    uint8_t subop = GroupOperation<Extension>(operand_modrm);

    if (OperandIsReg<Form>(operand_modrm)) {
        if (FpuModRM<Extension, Form>(operand_modrm) == 0xD9) {  // FCOMPP
            float80 st0 = FpuTop(state, 0);
            float80 st1 = FpuTop(state, 1);
            SetFpuCompareFlags(state, st0, st1);
            MergeFcomInvalidStatus(state, st0, st1, false);
            FpuPop(state);
            FpuPop(state);
            RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDE);
            return ContinueWithSyncedFpuStatus(state);
        }

        // DE C0-F7
        // DE C1: FADDP ST(1), ST0 (Add ST0 to ST1 and Pop)
        int idx = operand_modrm & 7;
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
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
        FpuPop(state);
    } else {
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        switch (subop) {
            case 0:  // FIADD m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_add(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 1:  // FIMUL m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_mul(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 2:  // FICOM m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                SetFpuCompareFlags(state, FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 3:  // FICOMP m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                SetFpuCompareFlags(state, FpuTop(state, 0), f80_from_int(val));
                FpuPop(state);
                break;
            }
            case 4:  // FISUB m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_sub(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 5:  // FISUBR m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_sub(f80_from_int(val), FpuTop(state, 0));
                break;
            }
            case 6:  // FIDIV m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_div(FpuTop(state, 0), f80_from_int(val));
                break;
            }
            case 7:  // FIDIVR m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                FpuTop(state, 0) = f80_div(f80_from_int(val), FpuTop(state, 0));
                break;
            }
            default:
                state->fault_vector = 6;
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDE);
    return ContinueWithSyncedFpuStatus(state);
}

template <GroupOpSpec Extension, AddrModeSpec Form>
FORCE_INLINE LogicFlow Fpu_DF_Impl(LogicFuncParams) {
    // DF: m16 Int / Misc
    const auto subop = GroupOperation<Extension>(operand_modrm);
    const auto modrm = FpuModRM<Extension, Form>(operand_modrm);
    const bool reg_form = OperandIsReg<Form>(operand_modrm);

    if (reg_form) {
        if ((modrm & 0xF8) == 0xC0) {  // FFREEP ST(i)
            int idx = operand_modrm & 7;
            int phys = (state->ctx.fpu_top + idx) & 7;
            state->ctx.fpu_tw |= (3 << (phys * 2));  // mark ST(i) empty
            FpuPop(state);                           // pop ST(0)
        } else if (modrm == 0xE0) {                  // FNSTSW AX
            SyncFpuStatusFromSoft(state);
            gpr.SetReg(state, EAX, (gpr.GetReg(state, EAX) & 0xFFFF0000) | state->ctx.fpu_sw);
        } else if ((modrm & 0xF8) == 0xE8) {  // FUCOMIP
            int idx = operand_modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            SetFcomiFlags(flags_cache, st0, sti);
            MergeFcomiInvalidStatus(state, st0, sti, true);
            FpuPop(state);
        } else if ((modrm & 0xF8) == 0xF0) {  // FCOMIP
            int idx = operand_modrm & 7;
            float80 st0 = FpuTop(state, 0);
            float80 sti = FpuTop(state, idx);
            SetFcomiFlags(flags_cache, st0, sti);
            MergeFcomiInvalidStatus(state, st0, sti, false);
            FpuPop(state);
        } else {
            state->fault_vector = 6;
            if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                state->status = EmuStatus::Fault;
            }
            return LogicFlow::ExitOnCurrentEIP;
        }
    } else {
        uint32_t addr = ComputeLinearAddress(state, gpr, op);
        switch (subop) {
            case 0:  // FILD m16int
            {
                auto val_res = ReadMem<uint16_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int16_t val = (int16_t)*val_res;
                float80 f = f80_from_int(val);
                FpuPush(state, &f);
                break;
            }
            case 1:  // FISTTP m16int
            {
                if (!WriteFpuInteger<uint16_t, f80_to_int16_checked>(state, gpr, addr, true, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 2:  // FIST m16int
            {
                if (!WriteFpuInteger<uint16_t, f80_to_int16_checked>(state, gpr, addr, false, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                break;
            }
            case 3:  // FISTP m16int
            {
                if (!WriteFpuInteger<uint16_t, f80_to_int16_checked>(state, gpr, addr, false, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 5:  // FILD m64int
            {
                auto val_res = ReadMem<uint64_t, OpOnTLBMiss::BlockingRestartable>(state, gpr, addr, utlb, op);
                if (!val_res) return LogicFlow::ExitOnCurrentEIP;
                int64_t val = (int64_t)*val_res;
                float80 f = f80_from_int(val);
                FpuPush(state, &f);
                break;
            }
            case 4:  // FBLD m80bcd
            {
                auto bcd_res = ReadPackedBcd80(state, gpr, addr, utlb, op);
                if (!bcd_res) return LogicFlow::ExitOnCurrentEIP;
                FpuPush(state, &(*bcd_res));
                break;
            }
            case 6:  // FBSTP m80bcd
            {
                if (!WritePackedBcd80(state, gpr, addr, FpuTop(state, 0), utlb, op)) return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            case 7:  // FISTP m64int
            {
                if (!WriteFpuInteger<uint64_t, f80_to_int64_checked>(state, gpr, addr, false, utlb, op))
                    return LogicFlow::ExitOnCurrentEIP;
                FpuPop(state);
                break;
            }
            default:
                state->fault_vector = 6;
                if (!gpr.WithCanonical(state, [&] { return state->hooks.on_invalid_opcode(state); })) {
                    state->status = EmuStatus::Fault;
                }
                return LogicFlow::ExitOnCurrentEIP;
        }
    }
    if (!reg_form || modrm != 0xE0) RecordFpuInstruction(state, gpr, op, operand_modrm, 0xDF);
    return ContinueWithSyncedFpuStatus(state);
}

#define FIBERCPU_BIND_VECTOR_FPU
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_VECTOR_FPU

}  // namespace op

}  // namespace fiberish
