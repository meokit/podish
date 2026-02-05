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
