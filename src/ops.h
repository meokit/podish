#pragma once

#include "common.h"
#include "decoder.h"
#include "state.h"

// Include all modular operation headers
#include "ops/ops_helpers.h"
#include "ops/ops_groups.h"
#include "ops/ops_data_mov.h" 
#include "ops/ops_alu.h"
#include "ops/ops_shift_bit.h"
#include "ops/ops_muldiv.h"
#include "ops/ops_control.h"
#include "ops/ops_compare.h"
#include "ops/ops_sse_fp.h"
#include "ops/ops_sse_cvt.h"
#include "ops/ops_sse_mov.h"
#include "ops/ops_fpu.h"

namespace x86emu {

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];

} // namespace x86emu
