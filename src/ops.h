#pragma once

#include "common.h"
#include "decoder.h"
#include "state.h"

namespace x86emu {

// Initial Handlers
void OpNop(EmuState* state, DecodedOp* op);
void OpUd2(EmuState* state, DecodedOp* op);

void OpDecodeFault(EmuState* state, DecodedOp* op);

// Arithmetic & Logic
void OpAdd_EvGv(EmuState* state, DecodedOp* op); // 01
void OpOr_EvGv(EmuState* state, DecodedOp* op); // 09
void OpAnd_EvGv(EmuState* state, DecodedOp* op); // 21
void OpSub_EvGv(EmuState* state, DecodedOp* op); // 29
void OpXor_EvGv(EmuState* state, DecodedOp* op); // 31
void OpCmp_EvGv(EmuState* state, DecodedOp* op); // 39
void OpTest_EvGv(EmuState* state, DecodedOp* op); // 85 (TEST r/m, r)
void OpCwde(EmuState* state, DecodedOp* op); // 98 (CBW/CWDE)
void OpCdq(EmuState* state, DecodedOp* op); // 99 (CWD/CDQ)

void OpInc_Reg(EmuState* state, DecodedOp* op); // 40-47
void OpDec_Reg(EmuState* state, DecodedOp* op); // 48-4F

void OpGroup1_EvIz(EmuState* state, DecodedOp* op); // 81, 83 (Add, Or, And, Sub, Xor, Cmp)
void OpGroup5_Ev(EmuState* state, DecodedOp* op); // FF (Inc, Dec, Call, Jmp, Push)

// Data Transfer
void OpMov_EvGv(EmuState* state, DecodedOp* op);
void OpMov_GvEv(EmuState* state, DecodedOp* op);
void OpMovzx_Byte(EmuState* state, DecodedOp* op); // 0F B6
void OpMovzx_Word(EmuState* state, DecodedOp* op); // 0F B7
void OpMovsx_Byte(EmuState* state, DecodedOp* op); // 0F BE
void OpMovsx_Word(EmuState* state, DecodedOp* op); // 0F BF
void OpMov_RegImm(EmuState* state, DecodedOp* op); // B8-BF
void OpMov_EvIz(EmuState* state, DecodedOp* op);   // C7 (Mov r/m32, imm32)
void OpLea(EmuState* state, DecodedOp* op);
void OpPush_Reg(EmuState* state, DecodedOp* op);
void OpPush_Imm(EmuState* state, DecodedOp* op);
void OpPop_Reg(EmuState* state, DecodedOp* op);

// Control
void OpHlt(EmuState* state, DecodedOp* op);
void OpInt(EmuState* state, DecodedOp* op); // CD
void OpInt3(EmuState* state, DecodedOp* op); // CC
void OpJmp_Rel(EmuState* state, DecodedOp* op); // E9
void OpJcc_Rel(EmuState* state, DecodedOp* op); // 0F 8x
void OpCall_Rel(EmuState* state, DecodedOp* op); // E8
void OpRet(EmuState* state, DecodedOp* op); // C3
// 0F C0/C1
void OpXadd_Rm_R(EmuState* state, DecodedOp* op);
// SSE/SSE2 Unified Handlers
void OpAdd_Sse(EmuState* state, DecodedOp* op); // 0F 58 (ADDPS, ADDPD, ADDSS, ADDSD)
void OpMul_Sse(EmuState* state, DecodedOp* op); // 0F 59 (MULPS, MULPD, MULSS, MULSD)
void OpSub_Sse(EmuState* state, DecodedOp* op); // 0F 5C (SUBPS, SUBPD, SUBSS, SUBSD)
void OpDiv_Sse(EmuState* state, DecodedOp* op); // 0F 5E (DIVPS, DIVPD, DIVSS, DIVSD)
void OpAnd_Sse(EmuState* state, DecodedOp* op); // 0F 54 (ANDPS, ANDPD)
void OpAndn_Sse(EmuState* state, DecodedOp* op); // 0F 55 (ANDNPS, ANDNPD)

// SSE Moves
void OpMov_Sse_Load(EmuState* state, DecodedOp* op); // 0F 10 (MOVUPS, MOVUPD, MOVSS, MOVSD)
void OpMov_Sse_Store(EmuState* state, DecodedOp* op); // 0F 11 (MOVUPS, MOVUPD, MOVSS, MOVSD)
void OpMovd_Load(EmuState* state, DecodedOp* op); // 0F 6E (MOVD xmm, r/m32)
void OpMovd_Store(EmuState* state, DecodedOp* op); // 0F 7E (MOVD r/m32, xmm)
void OpMovq_Load(EmuState* state, DecodedOp* op); // 0F 6F (MOVQ xmm, xmm/m64)
void OpMovq_Store(EmuState* state, DecodedOp* op); // 0F 7F (MOVQ xmm/m64, xmm)

// Global Dispatch Table
extern HandlerFunc g_Handlers[1024];

} // namespace x86emu
