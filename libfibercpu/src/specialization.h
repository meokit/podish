#pragma once

#include <cstdint>
#include "decoder.h"
#include "instruction_kernel_spec.h"

namespace fiberish {

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
    bool any_flags = false;
    bool memory_only = false;

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
        return (any_flags || no_flags == op->meta.flags.no_flags) && MatchesIndexedRemainder(op);
    }

    // ModRM and flag liveness are already selected by the immutable index.
    bool MatchesIndexedRemainder(const DecodedOp* op) const {
        if (prefix_mask && (op->prefixes.all & prefix_mask) != prefix_val) return false;
        if (memory_only && !op->meta.flags.has_mem) return false;

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

    constexpr int GetScore() const {
        // Strict liveness and memory-only predicates outrank their wildcard forms.
        int score = (any_flags ? 0 : 1) + (memory_only ? 1 : 0);
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
    int priority = 0;
};

// Runtime registrations update future decodes. The caller retains ownership of
// handler code for as long as decoded blocks can reference it.
void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler, int priority);

// Lookup
HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op);

}  // namespace fiberish
