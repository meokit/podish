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
#define PREFETCH_WRITE(addr) _mm_prefetch((const char*)(addr), _MM_HINT_T0)
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
struct ShimOp;

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTLB utlb, uint32_t branch);

enum class LogicFlow : uint8_t {
    Continue = 0,
    ExitOnCurrentEIP = 1,
    ExitOnNextEIP = 2,
    ExitWithoutSyncEIP = 3,
    RestartMemoryOp = 4,
    RetryMemoryOp = 5,
};

#define LogicFuncParams \
    EmuState *RESTRICT state, ShimOp *RESTRICT op, mem::MicroTLB *utlb, uint32_t imm, uint32_t *branch
#define LogicPassParams state, op, utlb, imm, branch

// Logic Function (Standard ABI, implementation)
// It may modify op and next_handler to control flow!
using LogicFunc = LogicFlow (*)(LogicFuncParams);  // Always inlined, no restrict needed

struct BasicBlock;

#define COMMON_OP_FIELDS                    \
    union {                                 \
        struct {                            \
            uint32_t disp;        /* 0-3 */ \
            uint8_t base_offset;  /* 4   */ \
            uint8_t index_offset; /* 5   */ \
            uint8_t scale;        /* 6   */ \
            uint8_t mem_flags;    /* 7   */ \
        } mem;                              \
        BasicBlock* next_block;             \
        uint64_t mem_packed;                \
    };                                      \
    uint32_t next_eip; /* 8-11  */          \
    uint8_t len;       /* 12    */          \
    uint8_t modrm;     /* 13    */          \
    union {                                 \
        uint8_t all;                        \
        struct {                            \
            uint8_t lock : 1;               \
            uint8_t rep : 1;                \
            uint8_t repne : 1;              \
            uint8_t segment : 3;            \
            uint8_t opsize : 1;             \
            uint8_t addrsize : 1;           \
        } flags;                            \
    } prefixes; /* 14    */                 \
    union {                                 \
        uint8_t all;                        \
        struct {                            \
            uint8_t has_modrm : 1;          \
            uint8_t has_sib : 1;            \
            uint8_t has_disp : 1;           \
            uint8_t has_imm : 1;            \
            uint8_t is_control_flow : 1;    \
            uint8_t no_flags : 1;           \
            uint8_t is_first : 1;           \
            uint8_t is_last : 1;            \
        } flags;                            \
    } meta /* 15    */

struct alignas(16) ShimOp {
    COMMON_OP_FIELDS;
};

struct alignas(32) DecodedOp {
    COMMON_OP_FIELDS;

    uint32_t imm;
    uint32_t padding0;
    HandlerFunc handler;

    uint8_t GetLength() const { return len; }
    void SetLength(uint8_t l) { len = l; }
};

static_assert(sizeof(ShimOp) == 16, "ShimOp must be exactly 16 bytes");
static_assert(sizeof(DecodedOp) == 32, "DecodedOp must be exactly 32 bytes");

static_assert(offsetof(ShimOp, next_eip) == offsetof(DecodedOp, next_eip), "Layout mismatch: next_eip");
static_assert(offsetof(ShimOp, meta) == offsetof(DecodedOp, meta), "Layout mismatch: meta");
static_assert(offsetof(DecodedOp, imm) == 16, "DecodedOp: imm must start at offset 16");
static_assert(offsetof(DecodedOp, handler) == 24, "DecodedOp: handler must start at offset 24");

#undef COMMON_OP_FIELDS

// Size check
static_assert(sizeof(DecodedOp) == 32, "DecodedOp size mismatch");

// Size check
// static_assert(sizeof(DecodedOp) == 32, "DecodedOp size mismatch");
struct BasicBlock {
    uint32_t start_eip;
    uint32_t end_eip;
    uint32_t inst_count;  // Number of instructions in block (excluding sentinel)
    uint16_t padding0;    // Align to 8 bytes for exec_count
    uint8_t padding1;
    bool is_valid = true;
    uint64_t exec_count;  // Number of times block was executed
    HandlerFunc jit_func = nullptr;

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
