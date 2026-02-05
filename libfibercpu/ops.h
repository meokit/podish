#pragma once

#include "common.h"
#include "decoder.h"
#include "state.h"

// Include all modular operation headers
#include "ops/ops_alu.h"
#include "ops/ops_compare.h"
#include "ops/ops_control.h"
#include "ops/ops_data_mov.h"
#include "ops/ops_double_shift.h"
#include "ops/ops_fpu.h"
#include "ops/ops_groups.h"
#include "ops/ops_helpers.h"
#include "ops/ops_muldiv.h"
#include "ops/ops_shift_bit.h"
#include "ops/ops_sse_cvt.h"
#include "ops/ops_sse_fp.h"
#include "ops/ops_sse_int.h"
#include "ops/ops_sse_mov.h"

namespace x86emu {

// Handler Base Anchor (Used for calculating relative offsets)
extern void* g_HandlerBase;

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];
extern HandlerFunc g_Handlers_NF[1024];
extern HandlerFunc g_ExitHandlers[16];

// Specialized and Fused Opcode Indices
enum SpecializedOp : uint16_t {
    OP_MOV_RR_STORE = 0x200,  // MOV r32, r32 (from 0x89)
    OP_MOV_RM_STORE = 0x201,  // MOV [mem], r32 (from 0x89)
    OP_MOV_RR_LOAD  = 0x202,  // MOV r32, r32 (from 0x8B)
    OP_MOV_MR_LOAD  = 0x203,  // MOV r32, [mem] (from 0x8B)

    OP_FUSED_CMP_RR_JCC = 0x210, // CMP r32, r32 + Jcc
    OP_FUSED_CMP_RI_JCC = 0x211, // CMP r32, imm + Jcc
    OP_FUSED_CMP_MR_JCC = 0x212, // CMP [mem], r32 + Jcc
    OP_FUSED_CMP_RM_JCC = 0x213, // CMP r32, [mem] + Jcc
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

}  // namespace x86emu
