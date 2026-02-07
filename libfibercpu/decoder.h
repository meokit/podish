#pragma once
#include <cstdint>
#include <vector>
#include "common.h"
#include "mem/tlb.h"

#ifdef __clang__
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#define ATTR_MUSTTAIL [[clang::musttail]]
#define FORCE_INLINE __attribute__((always_inline)) inline
#else
#define ATTR_PRESERVE_NONE
#define ATTR_MUSTTAIL
#define FORCE_INLINE inline
#endif

namespace fiberish {

struct EmuState;
struct DecodedOp;

// Logic Function (Standard ABI, implementation)
using LogicFunc = void (*)(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* state, DecodedOp* op, int64_t instr_limit,
                                                 mem::MicroTLB utlb);

struct BasicBlock;

struct DecodedOp {
    // Immediate and Displacement

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wgnu-anonymous-struct"
#pragma clang diagnostic ignored "-Wnested-anon-types"
    union {
        struct {
            uint32_t imm;
            uint32_t disp;
        };
        BasicBlock* next_block;
    };
#pragma clang diagnostic pop

    // Prefixes
    union {
        uint16_t all;
        struct {
            uint16_t lock : 1;
            uint16_t rep : 1;
            uint16_t repne : 1;
            uint16_t segment : 3;
            uint16_t opsize : 1;
            uint16_t addrsize : 1;
            uint16_t ea_base : 4;   // 0-7: Reg, 8: None
            uint16_t ea_index : 4;  // 0-7: Reg, 8: None
        } flags;
    } prefixes;

    // Internal Flags
    union {
        uint8_t all;
        struct {
            uint8_t has_modrm : 1;
            uint8_t has_sib : 1;
            uint8_t has_disp : 1;
            uint8_t has_imm : 1;
            uint8_t is_control_flow : 1;
            uint8_t ea_shift : 2;
        } flags;
    } meta;

    // ModR/M
    uint8_t modrm;

    // Opcode for profiling
    uint16_t opcode;

    // Handler Information
    // We use a bitfield to pack:
    // - handler_offset (32 bits signed): +/- 8MB range for handler functions
    // - length (4 bits): Max instruction length is 15 bytes
    // - extra (4 bits): Opcode-specific data (Condition Code, etc.)
    int32_t handler_offset;
    uint32_t length : 8;
    uint32_t extra : 4;
    uint32_t reserved : 4;
    uint32_t eip_offset : 16;
};

// Size check
// static_assert(sizeof(DecodedOp) == 24, "DecodedOp size mismatch");

struct BasicBlock {
    uint32_t start_eip;
    uint32_t end_eip;
    uint32_t inst_count;  // Number of instructions in block (excluding sentinel)
    bool is_valid = true;

    // Flexible Array Member - Must be last
    // We expect max 64 instructions per block + 1 sentinel + 1 fault handling = ~66
    // Allocation size will be sizeof(BasicBlock) + sizeof(DecodedOp) * (count - 1)
    DecodedOp ops[1];

    // Helper to calculate allocation size
    static size_t CalculateSize(size_t op_count) {
        if (op_count == 0) return sizeof(BasicBlock);
        return sizeof(BasicBlock) + sizeof(DecodedOp) * (op_count - 1);
    }

    // Mark block as invalid
    void Invalidate();
};

// Decoder Logic
bool DecodeInstruction(const uint8_t* code, DecodedOp* op);

// Start EIP, Limit EIP, Max Instructions -> Returns Pointer to allocated block or nullptr
BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts);

}  // namespace fiberish
