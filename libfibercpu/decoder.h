#pragma once
#include <cstdint>
#include <vector>
#include "common.h"

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
using LogicFunc = void (*)(EmuState* state, DecodedOp* op);

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* state, DecodedOp* op, int64_t instr_limit);

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

    // Handler Information
    // We use a bitfield to pack:
    // - handler_offset (24 bits signed): +/- 8MB range for handler functions
    // - length (4 bits): Max instruction length is 15 bytes
    // - extra (4 bits): Opcode-specific data (Condition Code, etc.)
    int32_t handler_offset : 24;
    uint32_t length : 4;
    uint32_t extra : 4;
};

// Size check
// static_assert(sizeof(DecodedOp) == 24, "DecodedOp size mismatch");

struct BasicBlock {
    uint32_t start_eip;
    uint32_t end_eip;
    uint32_t inst_count;  // Number of instructions in block (excluding sentinel)
    std::vector<DecodedOp> ops;

    // Chaining & Lifecycle
    std::vector<BasicBlock*> incoming_jumps;
    mutable uint32_t ref_count = 0;

    BasicBlock();
    ~BasicBlock();

    void Retain() const {
        // Assuming single-threaded emulator context or external locking.
        // If MT execution of the *same* EmuState is needed, use atomic.
        // Given current architecture implies single-threaded step/run per state:
        const_cast<BasicBlock*>(this)->ref_count++;
    }

    void Release() const {
        auto* self = const_cast<BasicBlock*>(this);
        if (--self->ref_count == 0) {
            delete self;
        }
    }

    // Link logic: this block is the TARGET. source is jumping TO here.
    void LinkFrom(BasicBlock* source);
    void UnlinkAll();
};

// Decoder Logic
bool DecodeInstruction(const uint8_t* code, DecodedOp* op);

// Decode Block
bool DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts, BasicBlock* block);

}  // namespace x86emu
