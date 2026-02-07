#pragma once

#include <cstdint>
#include "common.h"

namespace x86emu {

// Specialization Types
// This enum is used as a template parameter for instruction handlers.
enum class Specialized : uint8_t {
    None = 0,
    
    // Naming convention: Source_Dest_Type
    // e.g. Imm_RegEax
    
    // General
    // Registers (ModRM.Reg field)
    RegEax = 100, Reg0 = 100,
    RegEcx = 101, Reg1 = 101,
    RegEdx = 102, Reg2 = 102,
    RegEbx = 103, Reg3 = 103,
    RegEsp = 104, Reg4 = 104,
    RegEbp = 105, Reg5 = 105,
    RegEsi = 106, Reg6 = 106,
    RegEdi = 107, Reg7 = 107,
    
    // Addressing Modes
    ModReg,     // Mod=3 (Register)
    MemDisp0,   // [reg]
    MemDisp8,   // [reg + disp8]
    MemDisp32,  // [reg + disp32]
    
    // Operands
    Imm8,
    Imm32,
};

// Criteria for specialization matching
struct SpecCriteria {
    // ModRM Constraints
    // Set mask to 0 to ignore
    uint8_t mod_mask = 0;
    uint8_t mod_val = 0;
    
    uint8_t reg_mask = 0;
    uint8_t reg_val = 0;
    
    uint8_t rm_mask = 0;
    uint8_t rm_val = 0;
    
    // Prefix Constraints (e.g. Lock, Opsize)
    // Access op->prefixes.all directly.
    uint16_t prefix_mask = 0;
    uint16_t prefix_val = 0;
    
    bool Matches(uint8_t modrm, uint16_t prefixes) const {
        if (mod_mask) {
            uint8_t mod = (modrm >> 6) & 3;
            if ((mod & mod_mask) != mod_val) return false;
        }
        if (reg_mask) {
            uint8_t reg = (modrm >> 3) & 7;
            if ((reg & reg_mask) != reg_val) return false;
        }
        if (rm_mask) {
            uint8_t rm = modrm & 7;
            if ((rm & rm_mask) != rm_val) return false;
        }
        if (prefix_mask) {
            if ((prefixes & prefix_mask) != prefix_val) return false;
        }
        return true;
    }
};

#include "decoder.h" // For HandlerFunc

struct SpecializedEntry {
    uint16_t opcode;
    SpecCriteria criteria;
    HandlerFunc handler; // Wrapper pointer
};

// Global Registration
void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler);

// Lookup
HandlerFunc FindSpecializedHandler(uint16_t opcode, DecodedOp* op);

} // namespace x86emu
