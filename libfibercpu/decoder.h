#pragma once
#include <cstdint>
#include "common.h"
#include <vector>

#ifdef __clang__
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#define ATTR_MUSTTAIL [[clang::musttail]]
#define FORCE_INLINE __attribute__((always_inline)) inline
#else
#define ATTR_PRESERVE_NONE
#define ATTR_MUSTTAIL
#define FORCE_INLINE inline
#endif

namespace x86emu {

struct EmuState;
struct DecodedOp;
// Logic Function (Standard ABI, implementation)
using LogicFunc = void(*)(EmuState* state, DecodedOp* op);

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE *)(EmuState* state, DecodedOp* op, int64_t instr_limit);

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
    
    // Handler Information
    uint16_t handler_index;
    
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
        } flags;
    } prefixes;
    
    // ModR/M & SIB
    uint8_t modrm;
    uint8_t sib;
    
    // Meta
    uint8_t length;
    
    // Internal Flags
    union {
        uint8_t all;
        struct {
            uint8_t has_modrm : 1;
            uint8_t has_sib : 1;
            uint8_t has_disp : 1;
            uint8_t has_imm : 1;
            uint8_t is_control_flow : 1;
        } flags;
    } meta;
};

// Size check
// static_assert(sizeof(DecodedOp) == 24, "DecodedOp size mismatch");

struct BasicBlock {
    uint32_t start_eip;
    uint32_t end_eip;
    uint32_t inst_count; // Number of instructions in block (excluding sentinel)
    std::vector<DecodedOp> ops;
};

// Decoder Logic
bool DecodeInstruction(const uint8_t* code, DecodedOp* op);

// Decode Block
bool DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts, BasicBlock* block);

} // namespace x86emu
