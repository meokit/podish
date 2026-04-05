#include "../superopcodes.h"
#include "../ops.h"
#include "../ops/ops_alu_impl.h"
#include "../ops/ops_compare_impl.h"
#include "../ops/ops_control_impl.h"
#include "../ops/ops_data_mov_impl.h"
#include "../ops/ops_double_shift_impl.h"
#include "../ops/ops_fpu_impl.h"
#include "../ops/ops_groups_impl.h"
#include "../ops/ops_mmx_impl.h"
#include "../ops/ops_muldiv_impl.h"
#include "../ops/ops_shift_bit_impl.h"
#include "../ops/ops_sse_cvt_impl.h"
#include "../ops/ops_sse_fp_impl.h"
#include "../ops/ops_sse_int_impl.h"
#include "../ops/ops_sse_mov_impl.h"

namespace fiberish {

// weighted_exec_count=134718 occurrences=147862 relation=RAW anchor=Pop_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_000_OpPop_Reg32_Ebx__OpPop_Reg32_Esi(EmuState* RESTRICT state,
                                                                            DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                            mem::MicroTLB utlb, uint32_t branch,
                                                                            uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=116210 occurrences=134306 relation=RAW anchor=Test_EvGv_32_ModReg_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_001_OpTest_EvGv_32_ModReg_Eax__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=114924 occurrences=188944 relation=RAW anchor=Test_EvGv_32_ModReg_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_002_OpTest_EvGv_32_ModReg_Eax__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=109106 occurrences=129506 relation=RAW anchor=Pop_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_003_OpPop_Reg32_Esi__OpPop_Reg32_Edi(EmuState* RESTRICT state,
                                                                            DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                            mem::MicroTLB utlb, uint32_t branch,
                                                                            uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=106236 occurrences=138672 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_004_OpGroup5_Ev_Push_32_Flags__OpCall_Rel(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=104556 occurrences=122788 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_005_OpPush_Imm8__OpPush_Imm8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                    int64_t instr_limit, mem::MicroTLB utlb,
                                                                    uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=94194 occurrences=110338 relation=RAW anchor=Pop_Reg32_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_006_OpPop_Reg32_Edi__OpPop_Reg32_Ebp(EmuState* RESTRICT state,
                                                                            DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                            mem::MicroTLB utlb, uint32_t branch,
                                                                            uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebp, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=84208 occurrences=111808 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_007_OpPush_Reg32_Eax__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=82678 occurrences=95806 relation=RAW anchor=Pop_Reg32_Ebp direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_008_OpPop_Reg32_Ebp__OpRet(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTLB utlb,
                                                                  uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebp, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpRet, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=77320 occurrences=17346 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_009_OpCmp_EvGv_32_ModReg__OpJcc_B_Rel8(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_B_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=77068 occurrences=108676 relation=RAW anchor=Push_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_010_OpPush_Reg32_Esi__OpPush_Reg32_Ebx(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=74880 occurrences=93414 relation=RAW anchor=Group1_EvIb_Add_32_Flags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_011_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPop_Reg32_Ebx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=72376 occurrences=4346 relation=RAW anchor=Group2_EvIb_Shl direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_012_OpGroup2_EvIb_Shl__OpOr_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Shl, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=65772 occurrences=95424 relation=RAW anchor=Push_Reg32_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_013_OpPush_Reg32_Edi__OpPush_Reg32_Esi(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=63122 occurrences=108654 relation=RAW anchor=Test_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_014_OpTest_EvGv_32_ModReg__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=61334 occurrences=78008 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_015_OpGroup5_Ev_Push_32_Flags__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=58466 occurrences=82184 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_016_OpGroup1_EvIb_Sub_32_Flags__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=57358 occurrences=802 relation=RAW anchor=Or_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_017_OpOr_EvGv_NF_32_ModReg__OpMov_EvGv_Eax(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=56140 occurrences=50378 relation=RAW anchor=Mov_Load_Eax_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_018_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=55304 occurrences=97818 relation=RAW anchor=Test_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_019_OpTest_EvGv_32_ModReg__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=53276 occurrences=64556 relation=RAW anchor=Test_EbGb direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_020_OpTest_EbGb__OpJcc_E_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EbGb, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=52546 occurrences=868 relation=RAW anchor=Imul_GvEv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_021_OpImul_GvEv__OpAdd_EvGv_NF_32_ModReg(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpImul_GvEv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=51834 occurrences=62266 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_022_OpPush_Reg32_Ebx__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=51502 occurrences=74356 relation=RAW anchor=Push_Reg32_Ebp direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_023_OpPush_Reg32_Ebp__OpPush_Reg32_Edi(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebp, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=48462 occurrences=66602 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_024_OpGroup1_EvIb_Sub_32_Flags__OpPush_Imm8(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=47516 occurrences=88150 relation=RAW anchor=Mov_Load_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_025_OpMov_Load_Eax__OpTest_EvGv_32_ModReg_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=47422 occurrences=51528 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_026_OpCmp_EvGv_32_ModReg__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=47134 occurrences=61572 relation=RAW anchor=Lea_32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_027_OpLea_32_Eax__OpPush_Reg32_Eax(EmuState* RESTRICT state,
                                                                          DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                          mem::MicroTLB utlb, uint32_t branch,
                                                                          uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=39414 occurrences=51004 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_028_OpPush_Imm8__OpCall_Rel(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                   int64_t instr_limit, mem::MicroTLB utlb,
                                                                   uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=38514 occurrences=66830 relation=RAW anchor=Sub_GvEv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_029_OpSub_GvEv__OpJcc_NE_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpSub_GvEv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=38162 occurrences=11814 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_030_OpGroup1_EvIb_Add_32_NoFlags_Eax__OpCmp_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=37196 occurrences=59586 relation=RAW anchor=Group1_EbIb_Cmp_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_031_OpGroup1_EbIb_Cmp_Flags__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=37028 occurrences=14964 relation=RAW anchor=Test_EvGv_32_ModReg_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_032_OpTest_EvGv_32_ModReg_Edx__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=37014 occurrences=48562 relation=RAW anchor=Push_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_033_OpPush_Reg32_Esi__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=36366 occurrences=132 relation=RAW anchor=Group2_EvIb_Sar direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_034_OpGroup2_EvIb_Sar__OpGroup1_EvIb_And_32_NoFlags_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Sar, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=36096 occurrences=63976 relation=RAW anchor=Test_EvGv_32_ModReg_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_035_OpTest_EvGv_32_ModReg_Edx__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=35244 occurrences=326 relation=RAW anchor=Mov_EvGv_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_036_OpMov_EvGv_Eax__OpGroup2_EvIb_Sar(EmuState* RESTRICT state,
                                                                             DecodedOp* RESTRICT op,
                                                                             int64_t instr_limit, mem::MicroTLB utlb,
                                                                             uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Sar, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=35212 occurrences=292 relation=RAW anchor=Imul_GvEv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_037_OpImul_GvEv__OpMov_EvGv_Eax(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpImul_GvEv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=35132 occurrences=138 relation=RAW anchor=Add_GvEv_NF direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_038_OpAdd_GvEv_NF__OpImul_GvEv(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAdd_GvEv_NF, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpImul_GvEv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=35060 occurrences=18 relation=RAW anchor=Group1_EvIb_And_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_039_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpImul_GvEv(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpImul_GvEv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=34574 occurrences=36724 relation=RAW anchor=Push_Reg32_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_040_OpPush_Reg32_Edi__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=33478 occurrences=63902 relation=RAW anchor=Group1_EbIb_Cmp_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_041_OpGroup1_EbIb_Cmp_Flags__OpJcc_NE_Rel32(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=32722 occurrences=55864 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_042_OpPush_Reg32_Ebx__OpGroup1_EvIb_Sub_32_NoFlags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=32304 occurrences=56804 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_043_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpSub_GvEv(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpSub_GvEv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=31666 occurrences=59416 relation=RAW anchor=Group1_EbIb_Cmp_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_044_OpGroup1_EbIb_Cmp_Flags__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=31448 occurrences=28 relation=RAW anchor=Mov_Load_Ebx_EspBaseNoIndexNoSegment direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_045_OpMov_Load_Ebx_EspBaseNoIndexNoSegment__OpMov_Store_Esi(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=30378 occurrences=882 relation=RAW anchor=Cmp_EvGv_16 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_046_OpCmp_EvGv_16__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_16, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=29830 occurrences=43158 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_047_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=28538 occurrences=50658 relation=RAW anchor=Test_EbGb direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_048_OpTest_EbGb__OpJcc_E_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EbGb, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=28270 occurrences=50828 relation=RAW anchor=Group1_EbIb_Cmp_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_049_OpGroup1_EbIb_Cmp_Flags__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=28178 occurrences=4 relation=RAW anchor=Mov_EvGv_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_050_OpMov_EvGv_Edx__OpMov_Load_Edx_EsiBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx_EsiBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=27774 occurrences=35982 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_051_OpPush_Imm8__OpGroup5_Ev_Push_32_Flags(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=27430 occurrences=41634 relation=RAW anchor=Lea_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_052_OpLea_32__OpLea_32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                              uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpLea_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpLea_32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=27054 occurrences=44214 relation=RAW anchor=Mov_Store_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t
SuperOpcode_053_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=27004 occurrences=46856 relation=RAW anchor=Test_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_054_OpTest_EvGv_32_ModReg__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=26700 occurrences=31288 relation=RAW anchor=Test_EbGb direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_055_OpTest_EbGb__OpJcc_NE_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EbGb, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=26278 occurrences=32418 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_056_OpPush_Reg32_Ebx__OpGroup1_EvIb_Sub_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=25116 occurrences=46964 relation=RAW anchor=Test_EbGb direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_057_OpTest_EbGb__OpJcc_NE_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EbGb, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=24818 occurrences=46292 relation=RAW anchor=Mov_Moffs_Load_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_058_OpMov_Moffs_Load_Word__OpTest_EvGv_32_ModReg_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Moffs_Load_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=24440 occurrences=434 relation=RAW anchor=Add_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_059_OpAdd_EvGv_NF_32_ModReg__OpOr_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=24408 occurrences=32912 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_060_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Ebx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=24142 occurrences=43004 relation=RAW anchor=Test_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_061_OpTest_EvGv_32_ModReg__OpJcc_NE_Rel32(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=24034 occurrences=28480 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_062_OpCmp_EvGv_32_ModReg__OpJcc_AE_Rel8(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_AE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=23510 occurrences=42896 relation=RAW anchor=Test_EvGv_32_ModReg_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_063_OpTest_EvGv_32_ModReg_Eax__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=23328 occurrences=41980 relation=RAW anchor=Mov_Load_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_064_OpMov_Load_Edx__OpTest_EvGv_32_ModReg_Edx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=23126 occurrences=37594 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_065_OpCmp_EvGv_32_ModReg__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=23106 occurrences=44388 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_066_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=22884 occurrences=35932 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_067_OpCmp_EvGv_32_ModReg__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=22138 occurrences=25652 relation=RAW anchor=Pop_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_068_OpPop_Reg32_Ebx__OpRet(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTLB utlb,
                                                                  uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpRet, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=21806 occurrences=10990 relation=RAW anchor=Group2_Ev1_Shr direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_069_OpGroup2_Ev1_Shr__OpAdd_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_Ev1_Shr, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=21082 occurrences=39520 relation=RAW anchor=Group3_Eb_Generic direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_070_OpGroup3_Eb_Generic__OpJcc_NE_Rel32(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup3_Eb_Generic, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=20950 occurrences=26100 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_071_OpPush_Imm8__OpPush_Reg32_Eax(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=20632 occurrences=37122 relation=RAW anchor=Test_EvGv_32_ModReg_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_072_OpTest_EvGv_32_ModReg_Eax__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=20614 occurrences=35480 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_073_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=20472 occurrences=25302 relation=RAW anchor=Sub_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_074_OpSub_EvGv_NF_32_ModReg__OpSub_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=20328 occurrences=37602 relation=RAW anchor=Test_EvGv_32_ModReg_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_075_OpTest_EvGv_32_ModReg_Edx__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=19814 occurrences=25942 relation=RAW anchor=Push_Reg32_Ebp direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_076_OpPush_Reg32_Ebp__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebp, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=19510 occurrences=6260 relation=RAW anchor=Movzx_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_077_OpMovzx_Word__OpCmp_EvGv_16(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_16, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=19400 occurrences=26 relation=RAW anchor=Group2_EvIb direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_078_OpGroup2_EvIb__OpMov_EvGv_Eax(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=19286 occurrences=36216 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_079_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18892 occurrences=22670 relation=RAW anchor=Push_Reg32_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_080_OpPush_Reg32_Ecx__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18854 occurrences=32138 relation=RAW anchor=Mov_Load_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_081_OpMov_Load_Ebx__OpTest_EvGv_32_ModReg(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18748 occurrences=1170 relation=RAW anchor=Mov_EvGv_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_082_OpMov_EvGv_Ecx__OpGroup1_EvIb_And_32_NoFlags_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18722 occurrences=34122 relation=RAW anchor=Group3_Eb_Generic direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_083_OpGroup3_Eb_Generic__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                             DecodedOp* RESTRICT op,
                                                                             int64_t instr_limit, mem::MicroTLB utlb,
                                                                             uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup3_Eb_Generic, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18504 occurrences=32466 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t
SuperOpcode_084_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax_EaxBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18262 occurrences=312 relation=RAW anchor=Or_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_085_OpOr_EvGv_NF_32_ModReg__OpMov_EvGv_Ecx(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Ecx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18236 occurrences=24736 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_086_OpPush_Imm8__OpPush_Imm32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=18162 occurrences=3760 relation=RAW anchor=Mov_Load_Edx_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_087_OpMov_Load_Edx_EaxBaseNoIndexNoSegment__OpMovzx_Word(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=17782 occurrences=7802 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg2_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_088_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=17654 occurrences=23726 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_089_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Esi(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=17550 occurrences=31818 relation=RAW anchor=Test_EvGv_32_ModReg_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_090_OpTest_EvGv_32_ModReg_Ecx__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=17074 occurrences=30174 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_091_OpCmp_AlImm__OpJcc_E_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16946 occurrences=19000 relation=RAW anchor=Pop_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_092_OpPop_Reg32_Esi__OpRet(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTLB utlb,
                                                                  uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpRet, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16930 occurrences=28632 relation=RAW anchor=Movzx_Byte_32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_093_OpMovzx_Byte_32_Eax__OpCmp_AlImm(EmuState* RESTRICT state,
                                                                            DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                            mem::MicroTLB utlb, uint32_t branch,
                                                                            uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte_32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16912 occurrences=740 relation=RAW anchor=Mov_EvGv_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_094_OpMov_EvGv_Ebx__OpGroup1_EvIb_And_32_NoFlags_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16910 occurrences=21850 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_095_OpPush_Reg32_Eax__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16424 occurrences=76 relation=RAW anchor=Add_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_096_OpAdd_EvGv_NF_32_ModReg__OpImul_GvEv(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpImul_GvEv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=16106 occurrences=28418 relation=RAW anchor=Cmp_EvGv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_097_OpCmp_EvGv_32__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=15524 occurrences=19484 relation=RAW anchor=Push_Reg32_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_098_OpPush_Reg32_Edx__OpPush_Reg32_Eax(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=15368 occurrences=27434 relation=RAW anchor=Group1_EvIz_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_099_OpGroup1_EvIz_Cmp_32_Flags__OpJcc_A_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14888 occurrences=17944 relation=RAW anchor=Group1_EvIb_Add_32_Flags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_100_OpGroup1_EvIb_Add_32_Flags_ModReg__OpRet(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpRet, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14872 occurrences=18410 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_101_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Edi(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14832 occurrences=17900 relation=RAW anchor=Pop_Reg32_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_102_OpPop_Reg32_Edi__OpRet(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                  int64_t instr_limit, mem::MicroTLB utlb,
                                                                  uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpRet, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14762 occurrences=26206 relation=RAW anchor=Group3_Eb_Generic direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_103_OpGroup3_Eb_Generic__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup3_Eb_Generic, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14516 occurrences=17610 relation=RAW anchor=Group2_EvIb_Shr direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_104_OpGroup2_EvIb_Shr__OpXor_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Shr, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14370 occurrences=24246 relation=RAW anchor=Test_EvGv_32_ModReg_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_105_OpTest_EvGv_32_ModReg_Ecx__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14264 occurrences=80 relation=RAW anchor=Movzx_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_106_OpMovzx_Word__OpXor_GvEv_NF(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpXor_GvEv_NF, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14232 occurrences=24 relation=RAW anchor=Xor_GvEv_NF direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_107_OpXor_GvEv_NF__OpGroup2_EvIb(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpXor_GvEv_NF, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=14126 occurrences=26734 relation=RAW anchor=Mov_Load_Edx_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_108_OpMov_Load_Edx_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Edx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13930 occurrences=16388 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_109_OpPush_Imm8__OpPush_Reg32_Ebx(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13820 occurrences=25026 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_110_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13662 occurrences=146 relation=RAW anchor=Mov_Load_Ebx_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_111_OpMov_Load_Ebx_EaxBaseNoIndexNoSegment__OpMovzx_Byte(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ebx_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13586 occurrences=6 relation=RAW anchor=Movzx_Byte direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_112_OpMovzx_Byte__OpCmp_EvGv_16(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_16, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13216 occurrences=13256 relation=RAW anchor=Group1_EvIb_Add_32_Flags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_113_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPush_Imm8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13174 occurrences=22978 relation=RAW anchor=Mov_Load_Edx_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_114_OpMov_Load_Edx_EspBaseNoIndexNoSegment__OpSub_GvEv(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpSub_GvEv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13166 occurrences=20694 relation=RAW anchor=Mov_Moffs_Load_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_115_OpMov_Moffs_Load_Word__OpMov_Store_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Moffs_Load_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13160 occurrences=19272 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_116_OpPush_Reg32_Eax__OpPush_Imm8(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=13158 occurrences=22082 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t
SuperOpcode_117_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpMov_Store_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12842 occurrences=23382 relation=RAW anchor=Mov_Load_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_118_OpMov_Load_Ecx__OpTest_EvGv_32_ModReg_Ecx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Ecx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12698 occurrences=23576 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_119_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12682 occurrences=4848 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg1_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_120_OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12648 occurrences=16726 relation=RAW anchor=Push_Reg32_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_121_OpPush_Reg32_Edx__OpCall_Rel(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCall_Rel, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12580 occurrences=18982 relation=RAW anchor=Lea_32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_122_OpLea_32_Eax__OpMov_Store_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12476 occurrences=14864 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_123_OpGroup5_Ev_Push_32_Flags__OpPush_Imm8(EmuState* RESTRICT state,
                                                                                  DecodedOp* RESTRICT op,
                                                                                  int64_t instr_limit,
                                                                                  mem::MicroTLB utlb, uint32_t branch,
                                                                                  uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=12436 occurrences=18330 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_124_OpCmp_AlImm__OpJcc_E_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11958 occurrences=12628 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_125_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpGroup1_EbIb_Cmp_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11870 occurrences=346 relation=RAW anchor=Add_EvGv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_126_OpAdd_EvGv__OpMovzx_Word(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                    int64_t instr_limit, mem::MicroTLB utlb,
                                                                    uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11814 occurrences=16104 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_127_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Edx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11772 occurrences=22076 relation=RAW anchor=Or_EvGv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_128_OpOr_EvGv__OpJcc_E_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                    int64_t instr_limit, mem::MicroTLB utlb,
                                                                    uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11702 occurrences=21740 relation=RAW anchor=Group3_Eb_Generic direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_129_OpGroup3_Eb_Generic__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup3_Eb_Generic, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11554 occurrences=72 relation=RAW anchor=Mov_EvGv_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_130_OpMov_EvGv_Eax__OpGroup1_EvIz_And_16_NoFlags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_And_16_NoFlags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11370 occurrences=21826 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_131_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11336 occurrences=19714 relation=RAW anchor=Mov_Load_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_132_OpMov_Load_Eax__OpMov_Load_Eax_EaxBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=11180 occurrences=20736 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_133_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_NE_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10798 occurrences=20398 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_134_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10694 occurrences=10084 relation=RAW anchor=Mov_Sse_Store direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_135_OpMov_Sse_Store__OpMovdqu_Load(EmuState* RESTRICT state,
                                                                          DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                          mem::MicroTLB utlb, uint32_t branch,
                                                                          uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Sse_Store, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovdqu_Load, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10614 occurrences=15746 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_136_OpCmp_EvGv_32_ModReg__OpJcc_AE_Rel32(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_AE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10602 occurrences=5378 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg2_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_137_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_E_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10542 occurrences=13924 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_138_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10540 occurrences=18698 relation=RAW anchor=Group1_EvIb_Sub_32_NoFlags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_139_OpGroup1_EvIb_Sub_32_NoFlags_Eax__OpCmp_AlImm(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10452 occurrences=17716 relation=RAW anchor=Group1_EvIb_Sub_32_NoFlags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_140_OpGroup1_EvIb_Sub_32_NoFlags__OpMov_Load_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10344 occurrences=14386 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_141_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Ebp(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebp, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10276 occurrences=14490 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_142_OpCmp_EvGv_32_ModReg__OpJcc_NE_Rel32(EmuState* RESTRICT state,
                                                                                DecodedOp* RESTRICT op,
                                                                                int64_t instr_limit, mem::MicroTLB utlb,
                                                                                uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10274 occurrences=1508 relation=RAW anchor=Group1_EvIb_Sub_32_NoFlags_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_143_OpGroup1_EvIb_Sub_32_NoFlags_Edx__OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=10124 occurrences=17652 relation=RAW anchor=Cmp_EvGv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_144_OpCmp_EvGv_32__OpJcc_E_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9998 occurrences=19008 relation=RAW anchor=Movzx_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_145_OpMovzx_Word__OpGroup1_EvIb_Cmp_16_Flags(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9948 occurrences=18086 relation=RAW anchor=Cmp_GvEv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_146_OpCmp_GvEv_32__OpJcc_E_Rel32(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9794 occurrences=6744 relation=RAW anchor=Pop_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_147_OpPop_Reg32_Eax__OpPop_Reg32_Edx(EmuState* RESTRICT state,
                                                                            DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                            mem::MicroTLB utlb, uint32_t branch,
                                                                            uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9738 occurrences=17474 relation=RAW anchor=Cmp_EvGv_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_148_OpCmp_EvGv_32_ModReg__OpJcc_B_Rel32(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_B_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9698 occurrences=17174 relation=RAW anchor=Mov_Load_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_149_OpMov_Load_Esi__OpTest_EvGv_32_ModReg(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9574 occurrences=16134 relation=RAW anchor=Mov_Load_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_150_OpMov_Load_Eax__OpMov_Store_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9572 occurrences=14058 relation=RAW anchor=Group1_EvIz_Add_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_151_OpGroup1_EvIz_Add_32_Flags__OpPop_Reg32_Ebx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_Add_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9486 occurrences=4586 relation=RAW anchor=Push_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_152_OpPush_Reg32_Esi__OpPush_Reg32_Edi(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9428 occurrences=16900 relation=RAW anchor=Cmp_EvGv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_153_OpCmp_EvGv_32__OpJcc_NE_Rel32(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9422 occurrences=17796 relation=RAW anchor=Group1_EvIz_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_154_OpGroup1_EvIz_Cmp_16_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9422 occurrences=17256 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_155_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_E_Rel8(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9406 occurrences=16272 relation=RAW anchor=Mov_Load_Eax_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t
SuperOpcode_156_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpMov_Store_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9328 occurrences=12074 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_157_OpPush_Reg32_Eax__OpLea_32_Eax(EmuState* RESTRICT state,
                                                                          DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                          mem::MicroTLB utlb, uint32_t branch,
                                                                          uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9236 occurrences=16562 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_158_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_E_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9208 occurrences=16086 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_159_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_A_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9148 occurrences=15446 relation=RAW anchor=Mov_Store_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_160_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Moffs_Load_Word(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Moffs_Load_Word, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9148 occurrences=28 relation=RAW anchor=Group1_EvIb_Add_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_161_OpGroup1_EvIb_Add_32_Flags__OpMovzx_Byte(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9088 occurrences=14806 relation=RAW anchor=Mov_Load_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_162_OpMov_Load_Edi__OpTest_EvGv_32_ModReg(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9064 occurrences=54 relation=RAW anchor=Movzx_Byte_32_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_163_OpMovzx_Byte_32_Ecx__OpXor_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte_32_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9052 occurrences=106 relation=RAW anchor=Group2_Ev1_Shr direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_164_OpGroup2_Ev1_Shr__OpOr_EvGv_NF_32_ModReg(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_Ev1_Shr, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=9032 occurrences=14924 relation=RAW anchor=Mov_Store_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_165_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8994 occurrences=6 relation=RAW anchor=Group1_EvIz_And_16_NoFlags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_166_OpGroup1_EvIz_And_16_NoFlags__OpAdd_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_And_16_NoFlags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8836 occurrences=10852 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_167_OpPush_Imm8__OpPush_Reg32_Esi(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8810 occurrences=15770 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_168_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpCmp_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8706 occurrences=14666 relation=RAW anchor=Mov_Load_Eax_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t
SuperOpcode_169_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpMov_Load_Eax_EaxBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8626 occurrences=15058 relation=RAW anchor=Test_EvGv_32_ModReg_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_170_OpTest_EvGv_32_ModReg_Edx__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8582 occurrences=9236 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_171_OpPush_Reg32_Ebx__OpPush_Imm8(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8514 occurrences=11424 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_172_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Esi(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8456 occurrences=10368 relation=RAW anchor=Group2_EvIb_Shl direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_173_OpGroup2_EvIb_Shl__OpXor_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Shl, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8376 occurrences=13882 relation=RAW anchor=Cmp_EvGv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_174_OpCmp_EvGv_32__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8348 occurrences=15738 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_175_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_NE_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8344 occurrences=15642 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_176_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_A_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8332 occurrences=15332 relation=RAW anchor=Sub_GvEv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_177_OpSub_GvEv__OpJcc_NE_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpSub_GvEv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8268 occurrences=14846 relation=RAW anchor=Or_EvGv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_178_OpOr_EvGv__OpJcc_E_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                   int64_t instr_limit, mem::MicroTLB utlb,
                                                                   uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8118 occurrences=11304 relation=RAW anchor=Xor_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_179_OpXor_EvGv_NF_32_ModReg__OpXor_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8076 occurrences=2176 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg2_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_180_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_BE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_BE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8026 occurrences=13080 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_181_OpCmp_AlImm__OpJcc_NE_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=8014 occurrences=10396 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_182_OpPush_Reg32_Ebx__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7882 occurrences=15106 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_183_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Edx_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7806 occurrences=14162 relation=RAW anchor=Mov_Load_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_184_OpMov_Load_Eax__OpGroup1_EbIb_Cmp_Flags(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7758 occurrences=10036 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_185_OpPush_Imm8__OpPush_Reg32_Ecx(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ecx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7630 occurrences=9696 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_186_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Ebx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7542 occurrences=13956 relation=RAW anchor=Mov_Load_Eax_EsiBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_187_OpMov_Load_Eax_EsiBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EsiBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7386 occurrences=7408 relation=RAW anchor=Group1_EvIb_Add_32_Flags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_188_OpGroup1_EvIb_Add_32_Flags_ModReg__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7364 occurrences=13840 relation=RAW anchor=Mov_Load_Esi_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_189_OpMov_Load_Esi_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Esi_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7358 occurrences=12936 relation=RAW anchor=Movzx_Byte_32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_190_OpMovzx_Byte_32_Eax__OpGroup1_EvIb_Sub_32_NoFlags_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte_32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7344 occurrences=9716 relation=RAW anchor=Push_Reg32_Esi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_191_OpPush_Reg32_Esi__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7294 occurrences=13724 relation=RAW anchor=Group1_EvIb_Sub_32_NoFlags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_192_OpGroup1_EvIb_Sub_32_NoFlags_Eax__OpGroup1_EvIb_Cmp_16_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7222 occurrences=12166 relation=RAW anchor=Mov_Store_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_193_OpMov_Store_Eax__OpMov_Load_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7186 occurrences=38 relation=RAW anchor=Mov_Store_Edx_EsiBaseNoIndexNoSegment direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_194_OpMov_Store_Edx_EsiBaseNoIndexNoSegment__OpPop_Reg32_Ebx(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Edx_EsiBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPop_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7180 occurrences=10988 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_195_OpGroup1_EvIb_Sub_32_Flags__OpPush_Imm32(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Imm32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7158 occurrences=8560 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_196_OpPush_Reg32_Eax__OpPush_Reg32_Ebx(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7144 occurrences=12668 relation=RAW anchor=Sub_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_197_OpSub_EvGv_NF_32_ModReg__OpCmp_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7120 occurrences=3650 relation=RAW anchor=Group1_EvIb_And_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_198_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpGroup2_EvIb_Shl(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup2_EvIb_Shl, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7076 occurrences=13284 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_199_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7074 occurrences=12538 relation=RAW anchor=Movdqa_Load direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_200_OpMovdqa_Load__OpMovd_Store(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovdqa_Load, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovd_Store, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7060 occurrences=12114 relation=RAW anchor=Mov_Store_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_201_OpMov_Store_Eax__OpMov_Load_Eax(EmuState* RESTRICT state,
                                                                           DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                           mem::MicroTLB utlb, uint32_t branch,
                                                                           uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7054 occurrences=12656 relation=RAW anchor=Cmp_GvEv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_202_OpCmp_GvEv_32__OpJcc_E_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7050 occurrences=13456 relation=RAW anchor=Cmp_EaxImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_203_OpCmp_EaxImm__OpJcc_E_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_EaxImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7034 occurrences=10680 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_204_OpCmp_AlImm__OpJcc_A_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7030 occurrences=10008 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_205_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpCmp_GvEv_32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7020 occurrences=12558 relation=RAW anchor=Cmp_GvEv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_206_OpCmp_GvEv_32__OpJcc_AE_Rel8(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_AE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7008 occurrences=13534 relation=RAW anchor=Group1_EvIz_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_207_OpGroup1_EvIz_Cmp_16_Flags__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=7000 occurrences=12120 relation=RAW anchor=Mov_Moffs_Load_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_208_OpMov_Moffs_Load_Word__OpMov_Load_Eax_EaxBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Moffs_Load_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6992 occurrences=12992 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_209_OpCmp_AlImm__OpJcc_NE_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6982 occurrences=6974 relation=RAW anchor=Group1_EvIb_Add_32_Flags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_210_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPush_Reg32_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_Flags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6934 occurrences=13420 relation=RAW anchor=Group1_EbIb_Cmp_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_211_OpGroup1_EbIb_Cmp_Flags__OpJcc_NS_Rel32(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NS_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6880 occurrences=8924 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_212_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Edi(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6818 occurrences=8816 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_213_OpPush_Imm8__OpPush_Reg32_Edi(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6744 occurrences=9164 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_214_OpPush_Reg32_Eax__OpGroup5_Ev_Call_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Call_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6716 occurrences=12432 relation=RAW anchor=Mov_Load_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_215_OpMov_Load_Eax__OpMovzx_Word(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6702 occurrences=11916 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_216_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_NE_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6680 occurrences=11308 relation=RAW anchor=Mov_Load_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_217_OpMov_Load_Edx__OpMov_Load_Eax(EmuState* RESTRICT state,
                                                                          DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                          mem::MicroTLB utlb, uint32_t branch,
                                                                          uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6664 occurrences=8390 relation=RAW anchor=Xor_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_218_OpXor_EvGv_NF_32_ModReg__OpSub_EvGv_NF_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6650 occurrences=11910 relation=RAW anchor=Mov_Load_Ebp direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_219_OpMov_Load_Ebp__OpTest_EvGv_32_ModReg(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ebp, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6640 occurrences=9000 relation=RAW anchor=Push_Reg32_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_220_OpPush_Reg32_Edx__OpMov_Store_Edx_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Store_Edx_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6610 occurrences=8566 relation=RAW anchor=Push_Reg32_Edi direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_221_OpPush_Reg32_Edi__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edi, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6584 occurrences=1750 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg3_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_222_OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags__OpJcc_E_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_E_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6572 occurrences=80 relation=RAW anchor=And_EaxImm_NF direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_223_OpAnd_EaxImm_NF__OpAdd_EvGv_NF_32_ModReg(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAnd_EaxImm_NF, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6556 occurrences=11206 relation=RAW anchor=Sub_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_224_OpSub_EvGv_NF_32_ModReg__OpGroup2_Ev1_Shr(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup2_Ev1_Shr, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6548 occurrences=8922 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_225_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_BE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_BE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6496 occurrences=8576 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_226_OpGroup1_EvIb_Sub_32_Flags__OpMov_Load_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6492 occurrences=12346 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_227_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_A_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6442 occurrences=8484 relation=RAW anchor=Push_Reg32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_228_OpPush_Reg32_Eax__OpPush_Reg32_Esi(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Esi, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6426 occurrences=8154 relation=RAW anchor=Mov_EvGv_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_229_OpMov_EvGv_Eax__OpSub_EvGv_NF_32_ModReg(EmuState* RESTRICT state,
                                                                                   DecodedOp* RESTRICT op,
                                                                                   int64_t instr_limit,
                                                                                   mem::MicroTLB utlb, uint32_t branch,
                                                                                   uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_EvGv_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpSub_EvGv_NF_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6396 occurrences=8736 relation=RAW anchor=Push_Imm8 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_230_OpPush_Imm8__OpLea_32_Eax(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Imm8, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6368 occurrences=10886 relation=RAW anchor=Push_Reg32_Ebx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_231_OpPush_Reg32_Ebx__OpGroup1_EvIz_Sub_32_NoFlags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIz_Sub_32_NoFlags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6360 occurrences=11686 relation=RAW anchor=Cmp_GvEv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_232_OpCmp_GvEv_32__OpJcc_AE_Rel32(EmuState* RESTRICT state,
                                                                         DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                         mem::MicroTLB utlb, uint32_t branch,
                                                                         uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_AE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6326 occurrences=1084 relation=RAW anchor=Movzx_Byte direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_233_OpMovzx_Byte__OpLea_32_Edx(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                      int64_t instr_limit, mem::MicroTLB utlb,
                                                                      uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Byte, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpLea_32_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6312 occurrences=904 relation=RAW anchor=Group1_EbIb_Cmp_ModReg_Reg3_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_234_OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags__OpJcc_NE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6282 occurrences=11974 relation=RAW anchor=Or_EvGv direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_235_OpOr_EvGv__OpJcc_NE_Rel32(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6210 occurrences=11234 relation=RAW anchor=Mov_Load_Ebx_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_236_OpMov_Load_Ebx_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ebx_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6192 occurrences=2210 relation=RAW anchor=Xor_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_237_OpXor_EvGv_NF_32_ModReg__OpAdd_EvGv(EmuState* RESTRICT state,
                                                                               DecodedOp* RESTRICT op,
                                                                               int64_t instr_limit, mem::MicroTLB utlb,
                                                                               uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpXor_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6190 occurrences=8108 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_238_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Ebp(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Ebp, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=6054 occurrences=10024 relation=RAW anchor=Group3_Eb_Generic direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_239_OpGroup3_Eb_Generic__OpCmov_NE_ModReg(EmuState* RESTRICT state,
                                                                                 DecodedOp* RESTRICT op,
                                                                                 int64_t instr_limit,
                                                                                 mem::MicroTLB utlb, uint32_t branch,
                                                                                 uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup3_Eb_Generic, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmov_NE_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5972 occurrences=11026 relation=RAW anchor=Group1_EvIb_Cmp_16_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_240_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_BE_Rel32(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_16_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_BE_Rel32, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5956 occurrences=8146 relation=RAW anchor=Cmp_GvEv_32 direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_241_OpCmp_GvEv_32__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                        DecodedOp* RESTRICT op, int64_t instr_limit,
                                                                        mem::MicroTLB utlb, uint32_t branch,
                                                                        uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_GvEv_32, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5948 occurrences=10212 relation=RAW anchor=Cmp_AlImm direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_242_OpCmp_AlImm__OpJcc_A_Rel8(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                     int64_t instr_limit, mem::MicroTLB utlb,
                                                                     uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCmp_AlImm, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_A_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5872 occurrences=7850 relation=RAW anchor=Group1_EvIb_Sub_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_243_OpGroup1_EvIb_Sub_32_Flags__OpLea_32_Eax(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5830 occurrences=6142 relation=RAW anchor=Cdq direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_244_OpCdq__OpPush_Reg32_Edx(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                   int64_t instr_limit, mem::MicroTLB utlb,
                                                                   uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpCdq, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpPush_Reg32_Edx, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5822 occurrences=11098 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_245_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Eax_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5732 occurrences=210 relation=RAW anchor=Group1_EvIb_And_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_246_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_And_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5682 occurrences=7158 relation=RAW anchor=Mov_Load_Eax_EspBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_247_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpGroup5_Ev_Push_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Eax_EspBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5600 occurrences=9132 relation=RAW anchor=Test_EvGv_32_ModReg_Ecx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_248_OpTest_EvGv_32_ModReg_Ecx__OpJcc_NE_Rel8(EmuState* RESTRICT state,
                                                                                    DecodedOp* RESTRICT op,
                                                                                    int64_t instr_limit,
                                                                                    mem::MicroTLB utlb, uint32_t branch,
                                                                                    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg_Ecx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5574 occurrences=10826 relation=RAW anchor=Group1_EvIb_Add_32_NoFlags_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_249_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Ecx_EspBaseNoIndexNoSegment(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Add_32_NoFlags_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Ecx_EspBaseNoIndexNoSegment, state, second_op, instr_limit, utlb, branch,
                       flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5568 occurrences=8582 relation=RAW anchor=Group5_Ev_Push_32_Flags direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_250_OpGroup5_Ev_Push_32_Flags__OpGroup5_Ev_Call_32_Flags(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Push_32_Flags, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup5_Ev_Call_32_Flags, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5558 occurrences=5980 relation=RAW anchor=Group1_EvIb_Cmp_32_Flags_Edx direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_251_OpGroup1_EvIb_Cmp_32_Flags_Edx__OpJcc_NE_Rel8(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Cmp_32_Flags_Edx, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpJcc_NE_Rel8, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5506 occurrences=9396 relation=RAW anchor=Lea_32_Eax direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_252_OpLea_32_Eax__OpCmp_EvGv_32_ModReg(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpLea_32_Eax, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpCmp_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5490 occurrences=10614 relation=RAW anchor=Mov_Load_Edi_EaxBaseNoIndexNoSegment
// direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_253_OpMov_Load_Edi_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMov_Load_Edi_EaxBaseNoIndexNoSegment, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpTest_EvGv_32_ModReg, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5490 occurrences=352 relation=RAW anchor=Add_EvGv_NF_32_ModReg direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_254_OpAdd_EvGv_NF_32_ModReg__OpOr_EvGv(EmuState* RESTRICT state,
                                                                              DecodedOp* RESTRICT op,
                                                                              int64_t instr_limit, mem::MicroTLB utlb,
                                                                              uint32_t branch, uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpAdd_EvGv_NF_32_ModReg, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpOr_EvGv, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

// weighted_exec_count=5484 occurrences=10442 relation=RAW anchor=Movzx_Word direction=successor
ATTR_PRESERVE_NONE int64_t SuperOpcode_255_OpMovzx_Word__OpGroup1_EvIb_Sub_32_NoFlags_Eax(
    EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
    uint64_t flags_cache) {
    RUN_SUPEROPCODE_OP(op::OpMovzx_Word, state, op, instr_limit, utlb, branch, flags_cache);

    DecodedOp* second_op = NextOp(op);
    RUN_SUPEROPCODE_OP(op::OpGroup1_EvIb_Sub_32_NoFlags_Eax, state, second_op, instr_limit, utlb, branch, flags_cache);

    if (auto* next_op = NextOp(second_op)) {
        ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
    }
    __builtin_unreachable();
}

__attribute__((used)) HandlerFunc GeneratedFindSuperOpcode(const DecodedOp* ops) {
    if (!ops) return nullptr;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi>)
        return SuperOpcode_000_OpPop_Reg32_Ebx__OpPop_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_001_OpTest_EvGv_32_ModReg_Eax__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_002_OpTest_EvGv_32_ModReg_Eax__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi>)
        return SuperOpcode_003_OpPop_Reg32_Esi__OpPop_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_004_OpGroup5_Ev_Push_32_Flags__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_005_OpPush_Imm8__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebp>)
        return SuperOpcode_006_OpPop_Reg32_Edi__OpPop_Reg32_Ebp;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_007_OpPush_Reg32_Eax__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebp> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpRet>)
        return SuperOpcode_008_OpPop_Reg32_Ebp__OpRet;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_B_Rel8>)
        return SuperOpcode_009_OpCmp_EvGv_32_ModReg__OpJcc_B_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>)
        return SuperOpcode_010_OpPush_Reg32_Esi__OpPush_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>)
        return SuperOpcode_011_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPop_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Shl> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv_NF_32_ModReg>)
        return SuperOpcode_012_OpGroup2_EvIb_Shl__OpOr_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>)
        return SuperOpcode_013_OpPush_Reg32_Edi__OpPush_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_014_OpTest_EvGv_32_ModReg__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_015_OpGroup5_Ev_Push_32_Flags__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_016_OpGroup1_EvIb_Sub_32_Flags__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax>)
        return SuperOpcode_017_OpOr_EvGv_NF_32_ModReg__OpMov_EvGv_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax>)
        return SuperOpcode_018_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_019_OpTest_EvGv_32_ModReg__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EbGb> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_020_OpTest_EbGb__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpImul_GvEv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg>)
        return SuperOpcode_021_OpImul_GvEv__OpAdd_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_022_OpPush_Reg32_Ebx__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>)
        return SuperOpcode_023_OpPush_Reg32_Ebp__OpPush_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_024_OpGroup1_EvIb_Sub_32_Flags__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax>)
        return SuperOpcode_025_OpMov_Load_Eax__OpTest_EvGv_32_ModReg_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_026_OpCmp_EvGv_32_ModReg__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_027_OpLea_32_Eax__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_028_OpPush_Imm8__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpSub_GvEv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_029_OpSub_GvEv__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg>)
        return SuperOpcode_030_OpGroup1_EvIb_Add_32_NoFlags_Eax__OpCmp_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_031_OpGroup1_EbIb_Cmp_Flags__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_032_OpTest_EvGv_32_ModReg_Edx__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_033_OpPush_Reg32_Esi__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Sar> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg>)
        return SuperOpcode_034_OpGroup2_EvIb_Sar__OpGroup1_EvIb_And_32_NoFlags_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_035_OpTest_EvGv_32_ModReg_Edx__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Sar>)
        return SuperOpcode_036_OpMov_EvGv_Eax__OpGroup2_EvIb_Sar;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpImul_GvEv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax>)
        return SuperOpcode_037_OpImul_GvEv__OpMov_EvGv_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_GvEv_NF> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpImul_GvEv>)
        return SuperOpcode_038_OpAdd_GvEv_NF__OpImul_GvEv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpImul_GvEv>)
        return SuperOpcode_039_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpImul_GvEv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_040_OpPush_Reg32_Edi__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_041_OpGroup1_EbIb_Cmp_Flags__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags>)
        return SuperOpcode_042_OpPush_Reg32_Ebx__OpGroup1_EvIb_Sub_32_NoFlags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpSub_GvEv>)
        return SuperOpcode_043_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpSub_GvEv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_044_OpGroup1_EbIb_Cmp_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Esi>)
        return SuperOpcode_045_OpMov_Load_Ebx_EspBaseNoIndexNoSegment__OpMov_Store_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_16> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_046_OpCmp_EvGv_16__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_047_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EbGb> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_048_OpTest_EbGb__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_049_OpGroup1_EbIb_Cmp_Flags__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EsiBaseNoIndexNoSegment>)
        return SuperOpcode_050_OpMov_EvGv_Edx__OpMov_Load_Edx_EsiBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_051_OpPush_Imm8__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32>)
        return SuperOpcode_052_OpLea_32__OpLea_32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_053_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_054_OpTest_EvGv_32_ModReg__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EbGb> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_055_OpTest_EbGb__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags>)
        return SuperOpcode_056_OpPush_Reg32_Ebx__OpGroup1_EvIb_Sub_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EbGb> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_057_OpTest_EbGb__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Moffs_Load_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax>)
        return SuperOpcode_058_OpMov_Moffs_Load_Word__OpTest_EvGv_32_ModReg_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv_NF_32_ModReg>)
        return SuperOpcode_059_OpAdd_EvGv_NF_32_ModReg__OpOr_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>)
        return SuperOpcode_060_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_061_OpTest_EvGv_32_ModReg__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_AE_Rel8>)
        return SuperOpcode_062_OpCmp_EvGv_32_ModReg__OpJcc_AE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_063_OpTest_EvGv_32_ModReg_Eax__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx>)
        return SuperOpcode_064_OpMov_Load_Edx__OpTest_EvGv_32_ModReg_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_065_OpCmp_EvGv_32_ModReg__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_066_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_067_OpCmp_EvGv_32_ModReg__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpRet>)
        return SuperOpcode_068_OpPop_Reg32_Ebx__OpRet;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_Ev1_Shr> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg>)
        return SuperOpcode_069_OpGroup2_Ev1_Shr__OpAdd_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup3_Eb_Generic> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_070_OpGroup3_Eb_Generic__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_071_OpPush_Imm8__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_072_OpTest_EvGv_32_ModReg_Eax__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_073_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg>)
        return SuperOpcode_074_OpSub_EvGv_NF_32_ModReg__OpSub_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_075_OpTest_EvGv_32_ModReg_Edx__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_076_OpPush_Reg32_Ebp__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_16>)
        return SuperOpcode_077_OpMovzx_Word__OpCmp_EvGv_16;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax>)
        return SuperOpcode_078_OpGroup2_EvIb__OpMov_EvGv_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax>)
        return SuperOpcode_079_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_080_OpPush_Reg32_Ecx__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_081_OpMov_Load_Ebx__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg>)
        return SuperOpcode_082_OpMov_EvGv_Ecx__OpGroup1_EvIb_And_32_NoFlags_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup3_Eb_Generic> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_083_OpGroup3_Eb_Generic__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment>)
        return SuperOpcode_084_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax_EaxBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Ecx>)
        return SuperOpcode_085_OpOr_EvGv_NF_32_ModReg__OpMov_EvGv_Ecx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm32>)
        return SuperOpcode_086_OpPush_Imm8__OpPush_Imm32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word>)
        return SuperOpcode_087_OpMov_Load_Edx_EaxBaseNoIndexNoSegment__OpMovzx_Word;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_088_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>)
        return SuperOpcode_089_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_090_OpTest_EvGv_32_ModReg_Ecx__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_091_OpCmp_AlImm__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpRet>)
        return SuperOpcode_092_OpPop_Reg32_Esi__OpRet;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte_32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm>)
        return SuperOpcode_093_OpMovzx_Byte_32_Eax__OpCmp_AlImm;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg>)
        return SuperOpcode_094_OpMov_EvGv_Ebx__OpGroup1_EvIb_And_32_NoFlags_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_095_OpPush_Reg32_Eax__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpImul_GvEv>)
        return SuperOpcode_096_OpAdd_EvGv_NF_32_ModReg__OpImul_GvEv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_097_OpCmp_EvGv_32__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_098_OpPush_Reg32_Edx__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel32>)
        return SuperOpcode_099_OpGroup1_EvIz_Cmp_32_Flags__OpJcc_A_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpRet>)
        return SuperOpcode_100_OpGroup1_EvIb_Add_32_Flags_ModReg__OpRet;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>)
        return SuperOpcode_101_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpRet>)
        return SuperOpcode_102_OpPop_Reg32_Edi__OpRet;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup3_Eb_Generic> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_103_OpGroup3_Eb_Generic__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Shr> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg>)
        return SuperOpcode_104_OpGroup2_EvIb_Shr__OpXor_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_105_OpTest_EvGv_32_ModReg_Ecx__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpXor_GvEv_NF>)
        return SuperOpcode_106_OpMovzx_Word__OpXor_GvEv_NF;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpXor_GvEv_NF> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb>)
        return SuperOpcode_107_OpXor_GvEv_NF__OpGroup2_EvIb;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx>)
        return SuperOpcode_108_OpMov_Load_Edx_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>)
        return SuperOpcode_109_OpPush_Imm8__OpPush_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_110_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte>)
        return SuperOpcode_111_OpMov_Load_Ebx_EaxBaseNoIndexNoSegment__OpMovzx_Byte;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_16>)
        return SuperOpcode_112_OpMovzx_Byte__OpCmp_EvGv_16;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_113_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpSub_GvEv>)
        return SuperOpcode_114_OpMov_Load_Edx_EspBaseNoIndexNoSegment__OpSub_GvEv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Moffs_Load_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_115_OpMov_Moffs_Load_Word__OpMov_Store_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_116_OpPush_Reg32_Eax__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_117_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpMov_Store_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Ecx>)
        return SuperOpcode_118_OpMov_Load_Ecx__OpTest_EvGv_32_ModReg_Ecx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_119_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_120_OpGroup1_EbIb_Cmp_ModReg_Reg1_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCall_Rel>)
        return SuperOpcode_121_OpPush_Reg32_Edx__OpCall_Rel;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_122_OpLea_32_Eax__OpMov_Store_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_123_OpGroup5_Ev_Push_32_Flags__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_124_OpCmp_AlImm__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags>)
        return SuperOpcode_125_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpGroup1_EbIb_Cmp_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word>)
        return SuperOpcode_126_OpAdd_EvGv__OpMovzx_Word;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edx>)
        return SuperOpcode_127_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_128_OpOr_EvGv__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup3_Eb_Generic> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_129_OpGroup3_Eb_Generic__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_And_16_NoFlags>)
        return SuperOpcode_130_OpMov_EvGv_Eax__OpGroup1_EvIz_And_16_NoFlags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_131_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment>)
        return SuperOpcode_132_OpMov_Load_Eax__OpMov_Load_Eax_EaxBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_133_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_134_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Sse_Store> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovdqu_Load>)
        return SuperOpcode_135_OpMov_Sse_Store__OpMovdqu_Load;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_AE_Rel32>)
        return SuperOpcode_136_OpCmp_EvGv_32_ModReg__OpJcc_AE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_137_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_138_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm>)
        return SuperOpcode_139_OpGroup1_EvIb_Sub_32_NoFlags_Eax__OpCmp_AlImm;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_140_OpGroup1_EvIb_Sub_32_NoFlags__OpMov_Load_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>)
        return SuperOpcode_141_OpGroup1_EvIb_Sub_32_Flags__OpPush_Reg32_Ebp;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_142_OpCmp_EvGv_32_ModReg__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags>)
        return SuperOpcode_143_OpGroup1_EvIb_Sub_32_NoFlags_Edx__OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_144_OpCmp_EvGv_32__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags>)
        return SuperOpcode_145_OpMovzx_Word__OpGroup1_EvIb_Cmp_16_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_146_OpCmp_GvEv_32__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Edx>)
        return SuperOpcode_147_OpPop_Reg32_Eax__OpPop_Reg32_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_B_Rel32>)
        return SuperOpcode_148_OpCmp_EvGv_32_ModReg__OpJcc_B_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_149_OpMov_Load_Esi__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_150_OpMov_Load_Eax__OpMov_Store_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_Add_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>)
        return SuperOpcode_151_OpGroup1_EvIz_Add_32_Flags__OpPop_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>)
        return SuperOpcode_152_OpPush_Reg32_Esi__OpPush_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_153_OpCmp_EvGv_32__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_154_OpGroup1_EvIz_Cmp_16_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_155_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_156_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpMov_Store_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax>)
        return SuperOpcode_157_OpPush_Reg32_Eax__OpLea_32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_158_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel32>)
        return SuperOpcode_159_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_A_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Moffs_Load_Word>)
        return SuperOpcode_160_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Moffs_Load_Word;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte>)
        return SuperOpcode_161_OpGroup1_EvIb_Add_32_Flags__OpMovzx_Byte;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_162_OpMov_Load_Edi__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte_32_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg>)
        return SuperOpcode_163_OpMovzx_Byte_32_Ecx__OpXor_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_Ev1_Shr> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv_NF_32_ModReg>)
        return SuperOpcode_164_OpGroup2_Ev1_Shr__OpOr_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax>)
        return SuperOpcode_165_OpMov_Store_Eax_EspBaseNoIndexNoSegment__OpMov_Load_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_And_16_NoFlags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg>)
        return SuperOpcode_166_OpGroup1_EvIz_And_16_NoFlags__OpAdd_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>)
        return SuperOpcode_167_OpPush_Imm8__OpPush_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg>)
        return SuperOpcode_168_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpCmp_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment>)
        return SuperOpcode_169_OpMov_Load_Eax_EaxBaseNoIndexNoSegment__OpMov_Load_Eax_EaxBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_170_OpTest_EvGv_32_ModReg_Edx__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8>)
        return SuperOpcode_171_OpPush_Reg32_Ebx__OpPush_Imm8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>)
        return SuperOpcode_172_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Shl> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg>)
        return SuperOpcode_173_OpGroup2_EvIb_Shl__OpXor_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_174_OpCmp_EvGv_32__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_175_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel32>)
        return SuperOpcode_176_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_A_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpSub_GvEv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_177_OpSub_GvEv__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_178_OpOr_EvGv__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg>)
        return SuperOpcode_179_OpXor_EvGv_NF_32_ModReg__OpXor_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_BE_Rel32>)
        return SuperOpcode_180_OpGroup1_EbIb_Cmp_ModReg_Reg2_Flags__OpJcc_BE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_181_OpCmp_AlImm__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_182_OpPush_Reg32_Ebx__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx_EspBaseNoIndexNoSegment>)
        return SuperOpcode_183_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Edx_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags>)
        return SuperOpcode_184_OpMov_Load_Eax__OpGroup1_EbIb_Cmp_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ecx>)
        return SuperOpcode_185_OpPush_Imm8__OpPush_Reg32_Ecx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>)
        return SuperOpcode_186_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EsiBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Eax>)
        return SuperOpcode_187_OpMov_Load_Eax_EsiBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_188_OpGroup1_EvIb_Add_32_Flags_ModReg__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Esi_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_189_OpMov_Load_Esi_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte_32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags_Eax>)
        return SuperOpcode_190_OpMovzx_Byte_32_Eax__OpGroup1_EvIb_Sub_32_NoFlags_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_191_OpPush_Reg32_Esi__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags>)
        return SuperOpcode_192_OpGroup1_EvIb_Sub_32_NoFlags_Eax__OpGroup1_EvIb_Cmp_16_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_193_OpMov_Store_Eax__OpMov_Load_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Edx_EsiBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPop_Reg32_Ebx>)
        return SuperOpcode_194_OpMov_Store_Edx_EsiBaseNoIndexNoSegment__OpPop_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm32>)
        return SuperOpcode_195_OpGroup1_EvIb_Sub_32_Flags__OpPush_Imm32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx>)
        return SuperOpcode_196_OpPush_Reg32_Eax__OpPush_Reg32_Ebx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg>)
        return SuperOpcode_197_OpSub_EvGv_NF_32_ModReg__OpCmp_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_EvIb_Shl>)
        return SuperOpcode_198_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpGroup2_EvIb_Shl;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_199_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovdqa_Load> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovd_Store>)
        return SuperOpcode_200_OpMovdqa_Load__OpMovd_Store;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax>)
        return SuperOpcode_201_OpMov_Store_Eax__OpMov_Load_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel8>)
        return SuperOpcode_202_OpCmp_GvEv_32__OpJcc_E_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EaxImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_203_OpCmp_EaxImm__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel32>)
        return SuperOpcode_204_OpCmp_AlImm__OpJcc_A_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32>)
        return SuperOpcode_205_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpCmp_GvEv_32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_AE_Rel8>)
        return SuperOpcode_206_OpCmp_GvEv_32__OpJcc_AE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_207_OpGroup1_EvIz_Cmp_16_Flags__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Moffs_Load_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EaxBaseNoIndexNoSegment>)
        return SuperOpcode_208_OpMov_Moffs_Load_Word__OpMov_Load_Eax_EaxBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_209_OpCmp_AlImm__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_Flags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax>)
        return SuperOpcode_210_OpGroup1_EvIb_Add_32_Flags_ModReg__OpPush_Reg32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NS_Rel32>)
        return SuperOpcode_211_OpGroup1_EbIb_Cmp_Flags__OpJcc_NS_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>)
        return SuperOpcode_212_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi>)
        return SuperOpcode_213_OpPush_Imm8__OpPush_Reg32_Edi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Call_32_Flags>)
        return SuperOpcode_214_OpPush_Reg32_Eax__OpGroup5_Ev_Call_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word>)
        return SuperOpcode_215_OpMov_Load_Eax__OpMovzx_Word;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_216_OpGroup1_EvIb_Cmp_32_Flags_Eax__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax>)
        return SuperOpcode_217_OpMov_Load_Edx__OpMov_Load_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg>)
        return SuperOpcode_218_OpXor_EvGv_NF_32_ModReg__OpSub_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebp> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_219_OpMov_Load_Ebp__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Store_Edx_EspBaseNoIndexNoSegment>)
        return SuperOpcode_220_OpPush_Reg32_Edx__OpMov_Store_Edx_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edi> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_221_OpPush_Reg32_Edi__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_E_Rel32>)
        return SuperOpcode_222_OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags__OpJcc_E_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAnd_EaxImm_NF> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg>)
        return SuperOpcode_223_OpAnd_EaxImm_NF__OpAdd_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup2_Ev1_Shr>)
        return SuperOpcode_224_OpSub_EvGv_NF_32_ModReg__OpGroup2_Ev1_Shr;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_BE_Rel32>)
        return SuperOpcode_225_OpGroup1_EvIb_Cmp_32_Flags__OpJcc_BE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_226_OpGroup1_EvIb_Sub_32_Flags__OpMov_Load_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel32>)
        return SuperOpcode_227_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_A_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Esi>)
        return SuperOpcode_228_OpPush_Reg32_Eax__OpPush_Reg32_Esi;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_EvGv_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpSub_EvGv_NF_32_ModReg>)
        return SuperOpcode_229_OpMov_EvGv_Eax__OpSub_EvGv_NF_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Imm8> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax>)
        return SuperOpcode_230_OpPush_Imm8__OpLea_32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIz_Sub_32_NoFlags>)
        return SuperOpcode_231_OpPush_Reg32_Ebx__OpGroup1_EvIz_Sub_32_NoFlags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_AE_Rel32>)
        return SuperOpcode_232_OpCmp_GvEv_32__OpJcc_AE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Byte> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Edx>)
        return SuperOpcode_233_OpMovzx_Byte__OpLea_32_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_234_OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel32>)
        return SuperOpcode_235_OpOr_EvGv__OpJcc_NE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ebx_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_236_OpMov_Load_Ebx_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpXor_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv>)
        return SuperOpcode_237_OpXor_EvGv_NF_32_ModReg__OpAdd_EvGv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Ebp>)
        return SuperOpcode_238_OpGroup5_Ev_Push_32_Flags__OpPush_Reg32_Ebp;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup3_Eb_Generic> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmov_NE_ModReg>)
        return SuperOpcode_239_OpGroup3_Eb_Generic__OpCmov_NE_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_16_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_BE_Rel32>)
        return SuperOpcode_240_OpGroup1_EvIb_Cmp_16_Flags__OpJcc_BE_Rel32;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_GvEv_32> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_241_OpCmp_GvEv_32__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_AlImm> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_A_Rel8>)
        return SuperOpcode_242_OpCmp_AlImm__OpJcc_A_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax>)
        return SuperOpcode_243_OpGroup1_EvIb_Sub_32_Flags__OpLea_32_Eax;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpCdq> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpPush_Reg32_Edx>)
        return SuperOpcode_244_OpCdq__OpPush_Reg32_Edx;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment>)
        return SuperOpcode_245_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Eax_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_And_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags>)
        return SuperOpcode_246_OpGroup1_EvIb_And_32_NoFlags_ModReg__OpGroup1_EbIb_Cmp_ModReg_Reg3_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Eax_EspBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags>)
        return SuperOpcode_247_OpMov_Load_Eax_EspBaseNoIndexNoSegment__OpGroup5_Ev_Push_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg_Ecx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_248_OpTest_EvGv_32_ModReg_Ecx__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Add_32_NoFlags_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Ecx_EspBaseNoIndexNoSegment>)
        return SuperOpcode_249_OpGroup1_EvIb_Add_32_NoFlags_ModReg__OpMov_Load_Ecx_EspBaseNoIndexNoSegment;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Push_32_Flags> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup5_Ev_Call_32_Flags>)
        return SuperOpcode_250_OpGroup5_Ev_Push_32_Flags__OpGroup5_Ev_Call_32_Flags;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Cmp_32_Flags_Edx> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpJcc_NE_Rel8>)
        return SuperOpcode_251_OpGroup1_EvIb_Cmp_32_Flags_Edx__OpJcc_NE_Rel8;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpLea_32_Eax> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpCmp_EvGv_32_ModReg>)
        return SuperOpcode_252_OpLea_32_Eax__OpCmp_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMov_Load_Edi_EaxBaseNoIndexNoSegment> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpTest_EvGv_32_ModReg>)
        return SuperOpcode_253_OpMov_Load_Edi_EaxBaseNoIndexNoSegment__OpTest_EvGv_32_ModReg;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpAdd_EvGv_NF_32_ModReg> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpOr_EvGv>)
        return SuperOpcode_254_OpAdd_EvGv_NF_32_ModReg__OpOr_EvGv;
    if (ops[0].handler == (HandlerFunc)DispatchWrapper<op::OpMovzx_Word> &&
        ops[1].handler == (HandlerFunc)DispatchWrapper<op::OpGroup1_EvIb_Sub_32_NoFlags_Eax>)
        return SuperOpcode_255_OpMovzx_Word__OpGroup1_EvIb_Sub_32_NoFlags_Eax;
    return nullptr;
}

}  // namespace fiberish
