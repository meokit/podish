#pragma once

#include "common.h"
#include "decoder.h"
#include "specialization.h"
#include "state.h"

// Include all modular operation headers
#include "ops/ops_alu.h"
#include "ops/ops_compare.h"
#include "ops/ops_control.h"
#include "ops/ops_data_mov.h"
#include "ops/ops_double_shift.h"
#include "ops/ops_fpu.h"
#include "ops/ops_groups.h"
#include "ops/ops_muldiv.h"
#include "ops/ops_shift_bit.h"
#include "ops/ops_sse_cvt.h"
#include "ops/ops_sse_fp.h"
#include "ops/ops_sse_int.h"
#include "ops/ops_sse_mov.h"

#include <limits>

namespace fiberish {

// Handler Base Anchor (Used for calculating relative offsets)
extern void* g_HandlerBase;

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];
extern HandlerFunc g_ExitHandlers[32];

// Specialized Opcode Indices
enum SpecializedOp : uint16_t {
    OP_MOV_RR_STORE = 0x200,  // MOV r32, r32 (from 0x89)
    OP_MOV_RM_STORE = 0x201,  // MOV [mem], r32 (from 0x89)
    OP_MOV_RR_LOAD = 0x202,   // MOV r32, r32 (from 0x8B)
    OP_MOV_MR_LOAD = 0x203,   // MOV r32, [mem] (from 0x8B)
};

// Initialization
void RegisterAluOps();
void RegisterCompareOps();
void RegisterControlOps();
void RegisterDataMovOps();
void RegisterDoubleShiftOps();
void RegisterFpuOps();
void RegisterGroupOps();
void RegisterMulDivOps();
void RegisterShiftBitOps();
void RegisterSseCvtOps();
void RegisterSseFpOps();
void RegisterSseIntOps();
void RegisterSseMovOps();

// Sentinel Handler (Inline)
static inline ATTR_PRESERVE_NONE int64_t ExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                   int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch) {
    if (branch != std::numeric_limits<uint32_t>::max()) {
        state->ctx.eip = branch;
    } else {
        state->ctx.eip = op->next_eip;  // == (op - 1)->next_eip
    }

    // Clear mem_op
    state->mem_op.emplace<0>();

    // Basic Block Chaining
    // Optim: If next_block is dummy, is_valid is false, so we skip.
    // If next_block is real but invalidated, is_valid is false, so we skip.
    BasicBlock* next_block = GetNextBlock(op);
    if (next_block->is_valid && next_block->start_eip == state->ctx.eip) {
        // Check instruction limit before chaining
        if (instr_limit > 0) {
            // Subtract the NEXT block's size from the limit
            instr_limit -= next_block->inst_count;

            state->last_block = next_block;
            DecodedOp* next_head = next_block->FirstOp();

            next_block->exec_count++;

            if (next_block->entry != nullptr) {
                ATTR_MUSTTAIL return next_block->entry(state, next_head, instr_limit, utlb,
                                                       std::numeric_limits<uint32_t>::max());
            }
        }
    }
    // Returns to X86_Run loop.
    return instr_limit;
}

}  // namespace fiberish
