#pragma once

#include <cstdio>
#include "common.h"
#include "decoder.h"
#include "state.h"

namespace fiberish {

// ------------------------------------------------------------------------------------------------
// Register Access
// ------------------------------------------------------------------------------------------------

FORCE_INLINE uint32_t* GetRegPtr(EmuState* state, uint8_t reg_idx) { return &state->ctx.regs[reg_idx]; }

FORCE_INLINE uint32_t GetReg(EmuState* state, uint8_t reg_idx) { return *GetRegPtr(state, reg_idx); }

FORCE_INLINE void SetReg(EmuState* state, uint8_t reg_idx, uint32_t val) { *GetRegPtr(state, reg_idx) = val; }

FORCE_INLINE uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4)
        return val & 0xFF;
    else
        return (val >> 8) & 0xFF;
}

// ------------------------------------------------------------------------------------------------
// EFLAGS Masks
// ------------------------------------------------------------------------------------------------

constexpr uint32_t CF_MASK = 0x0001;
constexpr uint32_t PF_MASK = 0x0004;
constexpr uint32_t AF_MASK = 0x0010;
constexpr uint32_t ZF_MASK = 0x0040;
constexpr uint32_t SF_MASK = 0x0080;
constexpr uint32_t OF_MASK = 0x0800;

// ------------------------------------------------------------------------------------------------
// Condition Checking (for Jcc, CMOVcc)
// ------------------------------------------------------------------------------------------------

template <uint8_t Cond>
inline bool CheckConditionFixed(EmuState* state) {
    uint32_t f = state->ctx.eflags;
    if constexpr (Cond == 0)
        return (f & OF_MASK) != 0;  // JO
    else if constexpr (Cond == 1)
        return (f & OF_MASK) == 0;  // JNO
    else if constexpr (Cond == 2)
        return (f & CF_MASK) != 0;  // JB/JC
    else if constexpr (Cond == 3)
        return (f & CF_MASK) == 0;  // JNB/JNC
    else if constexpr (Cond == 4)
        return (f & ZF_MASK) != 0;  // JZ/JE
    else if constexpr (Cond == 5)
        return (f & ZF_MASK) == 0;  // JNZ/JNE
    else if constexpr (Cond == 6)
        return (f & (CF_MASK | ZF_MASK)) != 0;  // JBE
    else if constexpr (Cond == 7)
        return (f & (CF_MASK | ZF_MASK)) == 0;  // JA
    else if constexpr (Cond == 8)
        return (f & SF_MASK) != 0;  // JS
    else if constexpr (Cond == 9)
        return (f & SF_MASK) == 0;  // JNS
    else if constexpr (Cond == 10)
        return (f & PF_MASK) != 0;  // JP/JPE
    else if constexpr (Cond == 11)
        return (f & PF_MASK) == 0;  // JNP/JPO
    else if constexpr (Cond == 12)
        return ((f & SF_MASK) != 0) != ((f & OF_MASK) != 0);  // JL
    else if constexpr (Cond == 13)
        return ((f & SF_MASK) != 0) == ((f & OF_MASK) != 0);  // JGE
    else if constexpr (Cond == 14)
        return (f & ZF_MASK) || (((f & SF_MASK) != 0) != ((f & OF_MASK) != 0));  // JLE
    else if constexpr (Cond == 15)
        return !(f & ZF_MASK) && (((f & SF_MASK) != 0) == ((f & OF_MASK) != 0));  // JG
    else
        return false;
}

inline bool CheckCondition(EmuState* state, uint8_t cond) {
    static const uint32_t g_ConditionLUT[16] = {
        0xFFFF0000,  // cond 0: JO
        0x0000FFFF,  // cond 1: JNO
        0xAAAAAAAA,  // cond 2: JB
        0x55555555,  // cond 3: JAE
        0xF0F0F0F0,  // cond 4: JZ
        0x0F0F0F0F,  // cond 5: JNZ
        0xFAFAFAFA,  // cond 6: JBE
        0x05050505,  // cond 7: JA
        0xFF00FF00,  // cond 8: JS
        0x00FF00FF,  // cond 9: JNS
        0xCCCCCCCC,  // cond 10: JP
        0x33333333,  // cond 11: JNP
        0x00FFFF00,  // cond 12: JL
        0xFF0000FF,  // cond 13: JGE
        0xF0FFFFF0,  // cond 14: JLE
        0x0F00000F,  // cond 15: JG
    };

    uint32_t f = state->ctx.eflags;
    // Index bits: OF(bit 11) SF(bit 7) ZF(bit 6) PF(bit 2) CF(bit 0)
    // We map them to: OF:4, SF:3, ZF:2, PF:1, CF:0
    uint32_t index = (f & 0x1) | ((f >> 1) & 0x2) | ((f >> 4) & 0x4) | ((f >> 4) & 0x8) | ((f >> 7) & 0x10);

    return (g_ConditionLUT[cond & 0xF] >> index) & 1;
}

// ------------------------------------------------------------------------------------------------
// Effective Address Calculation
// ------------------------------------------------------------------------------------------------

inline uint32_t GetSegmentBase(EmuState* state, const DecodedOp* op) {
    uint8_t seg = op->prefixes.flags.segment;
    // 1=ES, 2=CS, 3=SS, 4=DS, 5=FS, 6=GS
    if (seg >= 5) {
        return state->ctx.seg_base[seg];
    }
    return 0;
}

inline uint32_t ComputeEA(EmuState* state, const DecodedOp* op) {
    // Computes Effective Address (no segment base)
    // If ea_base or ea_index is 8, will fetch from the `zero` register
    uint32_t base = state->ctx.regs[op->prefixes.flags.ea_base];
    base += (state->ctx.regs[op->prefixes.flags.ea_index] << op->meta.flags.ea_shift);
    base += op->disp;
    return base;
}

inline uint32_t ComputeLinearAddress(EmuState* state, const DecodedOp* op) {
    uint32_t ea = ComputeEA(state, op);
    return ea + GetSegmentBase(state, op);
}

// ------------------------------------------------------------------------------------------------
// ModRM Read/Write (32-bit only for now)
// ------------------------------------------------------------------------------------------------

inline uint32_t ReadModRM32(EmuState* state, const DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register Operand
        return GetReg(state, rm);
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, op);
        return state->mmu.read<uint32_t>(addr, utlb);
    }
}

inline void WriteModRM32(EmuState* state, const DecodedOp* op, uint32_t val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register Operand
        SetReg(state, rm, val);
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint32_t>(addr, val, utlb);
    }
}

inline void WriteModRM8(EmuState* state, const DecodedOp* op, uint8_t val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register (AL, CL, DL, BL, AH, CH, DH, BH)
        uint32_t* rptr = GetRegPtr(state, rm & 3);
        uint32_t curr = *rptr;
        if (rm < 4) {
            curr = (curr & 0xFFFFFF00) | val;
        } else {
            curr = (curr & 0xFFFF00FF) | (val << 8);
        }
        *rptr = curr;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint8_t>(addr, val, utlb);
    }
}

inline void WriteModRM16(EmuState* state, const DecodedOp* op, uint16_t val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register (AX, CX, DX, BX, SP, BP, SI, DI)
        uint32_t* rptr = GetRegPtr(state, rm);
        *rptr = (*rptr & 0xFFFF0000) | val;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint16_t>(addr, val, utlb);
    }
}

inline uint8_t ReadModRM8(EmuState* state, const DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register (AL, CL, DL, BL, AH, CH, DH, BH)
        uint32_t val = GetReg(state, rm & 3);
        if (rm < 4)
            return val & 0xFF;
        else
            return (val >> 8) & 0xFF;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        return state->mmu.read<uint8_t>(addr, utlb);
    }
}

inline uint16_t ReadModRM16(EmuState* state, const DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register (AX, CX, DX, BX, SP, BP, SI, DI)
        return GetReg(state, rm) & 0xFFFF;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        return state->mmu.read<uint16_t>(addr, utlb);
    }
}

inline simde__m128 ReadModRM128(EmuState* state, const DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        return state->ctx.xmm[rm];
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        uint64_t low = state->mmu.read<uint64_t>(addr, utlb);
        uint64_t high = state->mmu.read<uint64_t>(addr + 8, utlb);

        // Combine into simde__m128
        // Assuming little endian host and target
        simde__m128 res;
        uint64_t* ptr = (uint64_t*)&res;
        ptr[0] = low;
        ptr[1] = high;
        return res;
    }
}

inline void WriteModRM128(EmuState* state, const DecodedOp* op, simde__m128 val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        state->ctx.xmm[rm] = val;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        uint64_t* ptr = (uint64_t*)&val;
        state->mmu.write<uint64_t>(addr, ptr[0], utlb);
        state->mmu.write<uint64_t>(addr + 8, ptr[1], utlb);
    }
}

// ------------------------------------------------------------------------------------------------
// Stack Operations
// ------------------------------------------------------------------------------------------------

inline void Push16(EmuState* state, uint16_t val, mem::MicroTLB* utlb) {
    uint32_t esp = GetReg(state, ESP);
    esp -= 2;
    SetReg(state, ESP, esp);
    state->mmu.write<uint16_t>(esp, val, utlb);
}

inline uint16_t Pop16(EmuState* state, mem::MicroTLB* utlb) {
    uint32_t esp = GetReg(state, ESP);
    uint16_t val = state->mmu.read<uint16_t>(esp, utlb);
    esp += 2;
    SetReg(state, ESP, esp);
    return val;
}

inline void Push32(EmuState* state, uint32_t val, mem::MicroTLB* utlb) {
    uint32_t esp = GetReg(state, ESP);
    esp -= 4;
    SetReg(state, ESP, esp);

    // Optimization: Assume Flat Stack (ignore SS base)
    state->mmu.write<uint32_t>(esp, val, utlb);
}

inline uint32_t Pop32(EmuState* state, mem::MicroTLB* utlb) {
    uint32_t esp = GetReg(state, ESP);

    // Optimization: Assume Flat Stack (ignore SS base)
    uint32_t val = state->mmu.read<uint32_t>(esp, utlb);

    esp += 4;
    SetReg(state, ESP, esp);
    return val;
}

// ------------------------------------------------------------------------------------------------
// ALU Operations & Flags
// ------------------------------------------------------------------------------------------------

// Use compiler builtins where possible
#if defined(__GNUC__) || defined(__clang__)
#define FIBER_POPCOUNT32(x) __builtin_popcount(x)
#define FIBER_PARITY32(x) __builtin_parity(x)
#elif defined(_MSC_VER)
#include <intrin.h>
#define FIBER_POPCOUNT32(x) __popcnt(x)
// MSVC doesn't have a direct parity builtin, use popcount & 1
#define FIBER_PARITY32(x) (__popcnt(x) & 1)
#else
// Fallback
#define FIBER_POPCOUNT32(x) std::bitset<32>(x).count()
#define FIBER_PARITY32(x) (FIBER_POPCOUNT32(x) & 1)
#endif

// 256-entry Parity Lookup Table (1 = Odd Parity i.e. odd number of 1s)
static const uint8_t g_ParityLUT[256] = {
    1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1,
    0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0,
    0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0,
    1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1,
    0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1,
    0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1,
    1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1,
};

// Returns true if PF should be set (even number of 1s in low 8 bits)
inline bool CalcPflag(uint8_t res_byte) {
#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__) || defined(_M_IX86)
    // On x86, we can use builtins which map to efficient instructions or use a small sequence
    // __builtin_parity returns 1 for odd, 0 for even.
    // PF wants 1 for even, 0 for odd.
    return !FIBER_PARITY32(res_byte);
#else
    // On non-x86 (like ARM64), use LUT to avoid bit-twiddling overhead
    return g_ParityLUT[res_byte];
#endif
}

// Backward compatibility for other ops files
inline uint8_t Parity(uint8_t v) { return CalcPflag(v) ? 1 : 0; }

template <typename T, bool UpdateFlags = true>
inline T AluAdd(EmuState* state, T dest, T src) {
    T res = dest + src;

    if constexpr (UpdateFlags) {
        // PF, ZF, SF
        // Clear all relevant flags first
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        // ZF: Zero Flag
        if (res == 0) flags |= ZF_MASK;

        // SF: Sign Flag (MSB)
        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        // PF: Parity Flag (Low 8 bits have even number of 1s)
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        // CF: Unsigned Overflow (Carry)
        // res < dest implies overflow for ADD
        if (res < dest) flags |= CF_MASK;

        // OF: Signed Overflow
        // (dest^src) >= 0 (same sign) AND (dest^res) < 0 (sign changed)
        // Optimization: ((dest ^ src ^ -1) & (dest ^ res)) & sign_mask
        T sign_mask = (T)1 << msb_idx;
        if (!((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        // AF: Auxiliary Carry (Carry from bit 3 to 4)
        // Branchless: (dest ^ src ^ res) & 0x10 means there was a carry/borrow into bit 4
        if ((dest ^ src ^ res) & AF_MASK) flags |= AF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSub(EmuState* state, T dest, T src) {
    T res = dest - src;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        // CF: Borrow
        if (dest < src) flags |= CF_MASK;

        // OF: Signed Overflow
        // (dest^src) < 0 (diff sign) AND (dest^res) < 0 (sign flipped from dest)
        // equivalent: ((dest ^ src) & (dest ^ res)) & sign_mask
        T sign_mask = (T)1 << msb_idx;
        if (((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        // AF: Borrow from bit 3
        // For SUB, similar branchless formula: (dest ^ src ^ res) & 0x10
        if ((dest ^ src ^ res) & AF_MASK) flags |= AF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluAdc(EmuState* state, T dest, T src) {
    uint32_t cf_in = (state->ctx.eflags & CF_MASK);  // 0 or 1 (CF_MASK is 1)

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest + wsrc + cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        // CF
        if (wres >> (sizeof(T) * 8)) flags |= CF_MASK;

        // OF: Signed Overflow
        T sign_mask = (T)1 << msb_idx;
        if (!((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        // AF
        if ((dest ^ src ^ res) & AF_MASK) flags |= AF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSbb(EmuState* state, T dest, T src) {
    uint32_t cf_in = (state->ctx.eflags & CF_MASK);  // 0 or 1

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest - wsrc - cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        // CF: Borrow
        if (wdest < (wsrc + cf_in)) flags |= CF_MASK;

        // OF: Signed Overflow
        T sign_mask = (T)1 << msb_idx;
        if (((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        // AF
        if ((dest ^ src ^ res) & AF_MASK) flags |= AF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluAnd(EmuState* state, T dest, T src) {
    T res = dest & src;

    if constexpr (UpdateFlags) {
        // CF=0, OF=0, AF undefined (cleared)
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluOr(EmuState* state, T dest, T src) {
    T res = dest | src;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluXor(EmuState* state, T dest, T src) {
    T res = dest ^ src;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;

        state->ctx.eflags = flags;
    }
    return res;
}

// ------------------------------------------------------------------------------------------------
// Shift / Rotate
// ------------------------------------------------------------------------------------------------

template <typename T, bool UpdateFlags = true>
inline T AluShl(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // CF: Last bit shifted out is bit (sizeof(T)*8 - count) of original
    uint32_t width = sizeof(T) * 8;
    bool cf = (dest >> (width - count)) & 1;

    T res = dest << count;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (width - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ CF
        if (count == 1) {
            bool msb_res = (res >> (width - 1)) & 1;
            if (msb_res != cf) flags |= OF_MASK;
        }

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluShr(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // CF: Last bit shifted out is bit (count-1)
    bool cf = (dest >> (count - 1)) & 1;

    T res = dest >> count;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Original)
        if (count == 1) {
            if ((dest >> (sizeof(T) * 8 - 1)) & 1) flags |= OF_MASK;
        }

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSar(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // Arithmetic Shift
    using ST = std::make_signed_t<T>;
    ST sdest = (ST)dest;
    ST sres = sdest >> count;
    T res = (T)sres;

    // CF: Last bit shifted out
    bool cf = (dest >> (count - 1)) & 1;

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (CalcPflag(res & 0xFF)) flags |= PF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = 0
        if (count == 1) {
            // Clear OF (already cleared)
        }

        state->ctx.eflags = flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRol(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;

    T res = (dest << count) | (dest >> (width - count));

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | OF_MASK);
        // SF/ZF/PF/AF unaffected

        // CF: LSB of result
        bool cf = (res & 1);
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ CF
        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            if (msb != cf) flags |= OF_MASK;
        }

        state->ctx.eflags = (state->ctx.eflags & (PF_MASK | AF_MASK | ZF_MASK | SF_MASK)) | flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRor(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;

    T res = (dest >> count) | (dest << (width - count));

    if constexpr (UpdateFlags) {
        uint32_t flags = state->ctx.eflags & ~(CF_MASK | OF_MASK);

        // CF: MSB of result
        bool cf = (res >> (width - 1)) & 1;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ MSB-1(Res)
        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            if (msb != smsb) flags |= OF_MASK;
        }

        state->ctx.eflags = (state->ctx.eflags & (PF_MASK | AF_MASK | ZF_MASK | SF_MASK)) | flags;
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRcl(EmuState* state, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    count %= (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (state->ctx.eflags & CF_MASK) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val << count) | (val >> (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        if (new_cf)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;

        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            if (msb != new_cf)
                state->ctx.eflags |= OF_MASK;
            else
                state->ctx.eflags &= ~OF_MASK;
        }
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRcr(EmuState* state, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    count %= (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (state->ctx.eflags & CF_MASK) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val >> count) | (val << (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        if (new_cf)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;

        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            if (msb != smsb)
                state->ctx.eflags |= OF_MASK;
            else
                state->ctx.eflags &= ~OF_MASK;
        }
    }
    return res;
}

}  // namespace fiberish
