#pragma once

#include <cstdint>
#include "decoder.h"

namespace fiberish {

// Specialization Types
// This enum is used as a template parameter for instruction handlers.
enum class Specialized : uint8_t {
    None = 0,

    // Naming convention: Source_Dest_Type
    // e.g. Imm_RegEax

    // General
    // Registers (ModRM.Reg field)
    RegEax = 100,
    Reg0 = 100,
    RegEcx = 101,
    Reg1 = 101,
    RegEdx = 102,
    Reg2 = 102,
    RegEbx = 103,
    Reg3 = 103,
    RegEsp = 104,
    Reg4 = 104,
    RegEbp = 105,
    Reg5 = 105,
    RegEsi = 106,
    Reg6 = 106,
    RegEdi = 107,
    Reg7 = 107,

    // Addressing Modes
    ModReg,     // Mod=3 (Register)
    MemDisp0,   // [reg]
    MemDisp8,   // [reg + disp8]
    MemDisp32,  // [reg + disp32]

    // Operands
    Imm8,
    Imm32,

    // Operand Size
    Opsize16,
    Opsize32,
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

    // Flags Constraints
    bool no_flags = false;

    // Decoded memory shape constraints.
    // Set mask to 0 to ignore.
    uint8_t base_offset_mask = 0;
    uint8_t base_offset_val = 0;

    uint8_t index_offset_mask = 0;
    uint8_t index_offset_val = 0;

    uint8_t scale_mask = 0;
    uint8_t scale_val = 0;

    uint8_t segment_mask = 0;
    uint8_t segment_val = 0;

    bool RequiresMemShape() const { return base_offset_mask || index_offset_mask || scale_mask || segment_mask; }

    bool Matches(const DecodedOp* op) const {
        const uint8_t modrm = op->modrm;
        const uint8_t prefixes = op->prefixes.all;
        const bool op_no_flags = op->meta.flags.no_flags;

        if (op->meta.flags.has_modrm) {
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
        } else if (mod_mask || reg_mask || rm_mask) {
            return false;
        }
        if (prefix_mask) {
            if ((prefixes & prefix_mask) != prefix_val) return false;
        }
        if (no_flags != op_no_flags) return false;

        if (RequiresMemShape()) {
            if (!op->meta.flags.has_mem) return false;

            const uint32_t ea_desc = GetExt(op)->data.ea_desc;
            if (base_offset_mask) {
                const uint8_t base_offset = memdesc::BaseOffset(ea_desc);
                if ((base_offset & base_offset_mask) != base_offset_val) return false;
            }
            if (index_offset_mask) {
                const uint8_t index_offset = memdesc::IndexOffset(ea_desc);
                if ((index_offset & index_offset_mask) != index_offset_val) return false;
            }
            if (scale_mask) {
                const uint8_t scale = memdesc::Scale(ea_desc);
                if ((scale & scale_mask) != scale_val) return false;
            }
            if (segment_mask) {
                const uint8_t segment = memdesc::Segment(ea_desc);
                if ((segment & segment_mask) != segment_val) return false;
            }
        }

        return true;
    }

    int GetScore() const {
        int score = 0;
        if (mod_mask) score += 2;  // Rough weight
        if (reg_mask) score += 3;
        if (rm_mask) score += 3;
        if (base_offset_mask) score += 4;
        if (index_offset_mask) score += 4;
        if (scale_mask) score += 2;
        if (segment_mask) score += 2;
        if (prefix_mask) {
            // Count bits
            uint16_t v = prefix_mask;
            while (v) {
                score++;
                v &= v - 1;
            }
        }
        return score;
    }
};

struct SpecializedEntry {
    uint16_t opcode;
    SpecCriteria criteria;
    HandlerFunc handler;  // Wrapper pointer
};

// Global Registration
void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler);

// Lookup
HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op);

}  // namespace fiberish
