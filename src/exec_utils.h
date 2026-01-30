#pragma once

#include "common.h"
#include "state.h"
#include "decoder.h"
#include <cstdio>

namespace x86emu {

// ------------------------------------------------------------------------------------------------
// Register Access
// ------------------------------------------------------------------------------------------------

inline uint32_t* GetRegPtr(EmuState* state, uint8_t reg_idx) {
    if (reg_idx < 8) {
        return &state->ctx.regs[reg_idx];
    }
    // Handle invalid?
    return &state->ctx.regs[0]; // Fallback to EAX safe?
}

inline uint32_t GetReg(EmuState* state, uint8_t reg_idx) {
    return *GetRegPtr(state, reg_idx);
}

inline void SetReg(EmuState* state, uint8_t reg_idx, uint32_t val) {
    *GetRegPtr(state, reg_idx) = val;
}

// ------------------------------------------------------------------------------------------------
// Effective Address Calculation
// ------------------------------------------------------------------------------------------------

inline uint32_t ComputeEAD(EmuState* state, const DecodedOp* op) {
    // Mod=3 (Register) should be handled by caller before calling ComputeEAD.
    
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    uint32_t base = 0;
    
    // SIB?
    if (op->meta.flags.has_sib) {
        uint8_t scale = (op->sib >> 6) & 3;
        uint8_t index = (op->sib >> 3) & 7; // Index Register
        uint8_t base_reg = op->sib & 7;     // Base Register
        
        uint32_t idx_val = 0;
        if (index != 4) { // ESP cannot be Index
            idx_val = GetReg(state, index);
        }
        
        uint32_t base_val = 0;
         // Special SIB Base: If Mod=0 and Base=5 -> No Base (Disp32 only)
        if (mod == 0 && base_reg == 5) {
            base_val = 0; 
        } else {
            base_val = GetReg(state, base_reg);
        }
        
        base = base_val + (idx_val << scale);
    } 
    else {
        // No SIB
        // Mod=0, RM=5 -> Disp32 (Base=0)
        
        if (mod == 0 && rm == 5) {
            base = 0; // Absolute Disp32
        } else {
            base = GetReg(state, rm); 
        }
    }
    
    // Add Displacement
    if (op->meta.flags.has_disp) {
        base += op->disp;
    }
    
    return base;
}

// ------------------------------------------------------------------------------------------------
// ModRM Read/Write (32-bit only for now)
// ------------------------------------------------------------------------------------------------

inline uint32_t ReadModRM32(EmuState* state, const DecodedOp* op) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    if (mod == 3) {
        // Register Operand
        return GetReg(state, rm);
    } else {
        // Memory Operand
        uint32_t addr = ComputeEAD(state, op);
        return state->mmu.read<uint32_t>(addr);
    }
}

inline void WriteModRM32(EmuState* state, const DecodedOp* op, uint32_t val) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    if (mod == 3) {
        // Register Operand
        SetReg(state, rm, val);
    } else {
        // Memory Operand
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint32_t>(addr, val);
    }

}

inline uint8_t ReadModRM8(EmuState* state, const DecodedOp* op) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    if (mod == 3) {
        // Register (AL, CL, DL, BL, AH, CH, DH, BH)
        uint32_t val = GetReg(state, rm & 3);
        if (rm < 4) return val & 0xFF;
        else return (val >> 8) & 0xFF;
    } else {
        uint32_t addr = ComputeEAD(state, op);
        return state->mmu.read<uint8_t>(addr);
    }
}

inline uint16_t ReadModRM16(EmuState* state, const DecodedOp* op) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    if (mod == 3) {
        // Register (AX, CX, DX, BX, SP, BP, SI, DI)
        return GetReg(state, rm) & 0xFFFF;
    } else {
        uint32_t addr = ComputeEAD(state, op);
        return state->mmu.read<uint16_t>(addr);
    }
}

// ------------------------------------------------------------------------------------------------
// Stack Operations
// ------------------------------------------------------------------------------------------------

inline void Push32(EmuState* state, uint32_t val) {
    uint32_t esp = GetReg(state, ESP);
    esp -= 4;
    SetReg(state, ESP, esp);
    state->mmu.write<uint32_t>(esp, val);
}

inline uint32_t Pop32(EmuState* state) {
    uint32_t esp = GetReg(state, ESP);
    uint32_t val = state->mmu.read<uint32_t>(esp);
    esp += 4;
    SetReg(state, ESP, esp);
    return val;
}

// ------------------------------------------------------------------------------------------------
// ALU Operations & Flags
// ------------------------------------------------------------------------------------------------

// EFLAGS Masks
constexpr uint32_t CF_MASK = 0x0001;
constexpr uint32_t PF_MASK = 0x0004;
constexpr uint32_t AF_MASK = 0x0010;
constexpr uint32_t ZF_MASK = 0x0040;
constexpr uint32_t SF_MASK = 0x0080;
constexpr uint32_t OF_MASK = 0x0800;

inline uint8_t Parity(uint8_t v) {
    // Basic parity: even number of 1s -> 1
    v ^= v >> 4;
    v &= 0xf;
    return (0x9669 >> v) & 1;
}

template<typename T>
inline T AluAdd(EmuState* state, T dest, T src) {
    T res = dest + src;
    
    // PF, ZF, SF
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    
    // CF: Unsigned Overflow
    // res < dest implies overflow for ADD
    // Or (res < src)
    if (res < dest) flags |= CF_MASK;
    
    // OF: Signed Overflow
    // (dest^src) >= 0 (same sign) AND (dest^res) < 0 (sign changed)
    T sign_mask = (T)1 << (sizeof(T)*8 - 1);
    bool s1 = (dest & sign_mask);
    bool s2 = (src & sign_mask);
    bool sr = (res & sign_mask);
    
    if (s1 == s2 && s1 != sr) flags |= OF_MASK;
    
    // AF: Carry from bit 3 to 4
    if (((dest & 0xF) + (src & 0xF)) > 0xF) flags |= AF_MASK;

    state->ctx.eflags = flags;
    return res;
}

template<typename T>
inline T AluSub(EmuState* state, T dest, T src) {
    T res = dest - src;
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    
    // CF: Borrow
    if (dest < src) flags |= CF_MASK;
    
    // OF: Signed Overflow
    // (dest^src) < 0 (diff sign) AND (dest^res) < 0 (sign flipped from dest)
    T sign_mask = (T)1 << (sizeof(T)*8 - 1);
    bool s1 = (dest & sign_mask);
    bool s2 = (src & sign_mask);
    bool sr = (res & sign_mask);
    
    if (s1 != s2 && s1 != sr) flags |= OF_MASK;
    
    // AF: Borrow from bit 3
    if ((dest & 0xF) < (src & 0xF)) flags |= AF_MASK;
    
    state->ctx.eflags = flags;
    return res;
}



template<typename T>
inline T AluAnd(EmuState* state, T dest, T src) {
    T res = dest & src;
    
    // CF=0, OF=0
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    
    // AF is undefined, let's clear it
    
    state->ctx.eflags = flags;
    return res;
}

template<typename T>
inline T AluOr(EmuState* state, T dest, T src) {
    T res = dest | src;
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    
    state->ctx.eflags = flags;
    return res;
}

template<typename T>
inline T AluXor(EmuState* state, T dest, T src) {
    T res = dest ^ src;
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    
    state->ctx.eflags = flags;
    return res;
}


// ------------------------------------------------------------------------------------------------
// Shift / Rotate
// ------------------------------------------------------------------------------------------------

template<typename T>
inline T AluShl(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;
    
    // CF: Last bit shifted out is bit (sizeof(T)*8 - count) of original
    uint32_t width = sizeof(T) * 8;
    bool cf = (dest >> (width - count)) & 1;
    
    T res = dest << count;
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (width - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    if (cf) flags |= CF_MASK;
    
    // OF: For 1-bit, OF = MSB(Res) ^ CF
    if (count == 1) {
        bool msb_res = (res >> (width - 1)) & 1;
        if (msb_res != cf) flags |= OF_MASK;
    }
    
    state->ctx.eflags = flags;
    return res;
}

template<typename T>
inline T AluShr(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;
    
    // CF: Last bit shifted out is bit (count-1)
    bool cf = (dest >> (count - 1)) & 1;
    
    T res = dest >> count;
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    if (cf) flags |= CF_MASK;
    
    // OF: For 1-bit, OF = MSB(Original)
    if (count == 1) {
        if ((dest >> (sizeof(T)*8 - 1)) & 1) flags |= OF_MASK;
    }
    
    state->ctx.eflags = flags;
    return res;
}

template<typename T>
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
    
    uint32_t flags = state->ctx.eflags & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
    if (res == 0) flags |= ZF_MASK;
    if ((res >> (sizeof(T)*8 - 1)) & 1) flags |= SF_MASK;
    if (Parity(res & 0xFF)) flags |= PF_MASK;
    if (cf) flags |= CF_MASK;
    
    // OF: For 1-bit, OF = 0
    if (count == 1) {
        // Clear OF (already cleared)
    }
    
    state->ctx.eflags = flags;
    return res;
}

template<typename T>
inline T AluRol(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;
    
    T res = (dest << count) | (dest >> (width - count));
    
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
    return res;
}

template<typename T>
inline T AluRor(EmuState* state, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;
    
    T res = (dest >> count) | (dest << (width - count));
    
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
    return res;
}

}

