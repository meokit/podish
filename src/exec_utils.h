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
    // If not ModRM, no EA (or specialized). 
    // Assuming this is called only if has_modrm.
    
    // Mod=3 -> Register (Not memory address). Caller should check mod!=3 before calling this for Mem Access.
    
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
        // Special Case: Mod=0, RM=5 -> Disp32 (Already handled by decoder setting Disp, RM=5 ignored as base??)
        // Decoder logic:
        // if mod==0 && rm==5: disp_size=4. Base is 0.
        // else: Base is GetReg(rm).
        
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

}
