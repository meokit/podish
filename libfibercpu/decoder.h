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

#if defined(_MSC_VER)
#define RESTRICT __restrict
#elif defined(__GNUC__) || defined(__clang__)
#define RESTRICT __restrict__
#else
#define RESTRICT
#endif

#if defined(__GNUC__) || defined(__clang__)
#define PREFETCH(addr) __builtin_prefetch(addr, 0, 3)
#define PREFETCH_WRITE(addr) __builtin_prefetch(addr, 1, 3)
#elif defined(_MSC_VER)
#include <intrin.h>
#if defined(_M_X64) || defined(_M_IX86)
#define PREFETCH(addr) _mm_prefetch((const char*)(addr), _MM_HINT_T0)
#define PREFETCH_WRITE(addr) _mm_prefetch((const char*)(addr), _MM_HINT_T0)  // x86写预取指令支持有限
#elif defined(_M_ARM64) || defined(_M_ARM)
#define PREFETCH(addr) __prefetch((const void*)(addr))
#define PREFETCH_WRITE(addr) __prefetchw((const void*)(addr))
#else
#define PREFETCH(addr)
#define PREFETCH_WRITE(addr)
#endif
#else
#define PREFETCH(addr)
#define PREFETCH_WRITE(addr)
#endif

namespace fiberish {

struct EmuState;
struct DecodedOp;

// Logic Function (Standard ABI, implementation)
using LogicFunc = void (*)(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Always inlined, no restrict needed

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTLB utlb);

struct BasicBlock;

struct alignas(32) DecodedOp {
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

    // ------------ 8 BYTES ------------

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

    // ------------ 10 BYTES ------------

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

    // ------------ 12 BYTES ------------
    int8_t length;
    int8_t extra;
    int8_t padding0;
    int8_t padding1;
    // ------------ 16 BYTES ------------
    uint32_t next_eip;
    uint32_t padding2;

    // ------------ 24 BYTES ------------
    HandlerFunc handler;
    // ------------ 32 BYTES ------------
};

// Size check
// static_assert(sizeof(DecodedOp) == 32, "DecodedOp size mismatch");
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
bool DecodeInstruction(const uint8_t* code, DecodedOp* op, uint16_t* handler_index);

// Start EIP, Limit EIP, Max Instructions -> Returns Pointer to allocated block or nullptr
BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts);

}  // namespace fiberish
