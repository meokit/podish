#define FIBERCPU_LLVM_BLOCK_TEMPLATE
#include "jit/llvm_block_jit.h"
#include "llvm_op_ids.h"
#include "ops/ops_alu_impl.h"
#include "ops/ops_compare_impl.h"
#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_double_shift_impl.h"
#include "ops/ops_fpu_impl.h"
#include "ops/ops_groups_impl.h"
#include "ops/ops_mmx_impl.h"
#include "ops/ops_muldiv_impl.h"
#include "ops/ops_shift_bit_impl.h"
#include "ops/ops_sse_cvt_impl.h"
#include "ops/ops_sse_fp_impl.h"
#include "ops/ops_sse_int_impl.h"
#include "ops/ops_sse_mov_impl.h"
#include "superopcodes.h"

namespace fiberish {

extern "C" unsigned FiberLLVMOperandLayout();
extern "C" [[gnu::const]] unsigned FiberLLVMBranchSuffix();
extern "C" LLVMBlockExit FiberLLVMExit();
extern "C" LogicFlow FiberLLVMPlaceholder(uint32_t encoding, uint32_t ea_desc, uint32_t displacement, LogicFuncParams);
extern "C" ATTR_PRESERVE_NONE int64_t FiberLLVMNextStep(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                        int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                        mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                        uint64_t flags_cache FIBERCPU_PIN_PARAMS);

#ifdef FIBERCPU_LLVM_SHARED_REGION_EXITS
extern "C"
    [[gnu::noinline]] ATTR_PRESERVE_NONE int64_t FiberLLVMRegionReturn(EmuState* state, DecodedOp*, int64_t instr_limit,
                                                                       mem::MicroTlbAbiWord, mem::MicroTlbAbiWord,
                                                                       uint32_t,
                                                                       uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_COMMIT(state);
    CommitFlagsCache(state, flags_cache);
    return instr_limit;
}

#define FIBERCPU_DEFINE_REGION_CHAIN(Name, LinkSlot)                                                                 \
    extern "C" [[gnu::noinline]] ATTR_PRESERVE_NONE int64_t Name(                                                    \
        EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTlbAbiWord tags, mem::MicroTlbAbiWord addend, \
        uint32_t eip, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                                    \
        __builtin_assume(instr_limit > 0);                                                                           \
        ATTR_MUSTTAIL return ResolveBranchTargetInline<LinkSlot, false>(state, op, instr_limit, tags, addend, eip,   \
                                                                        flags_cache FIBERCPU_PIN_ARGS);              \
    }
FIBERCPU_DEFINE_REGION_CHAIN(FiberLLVMRegionChainSingle, 0)
FIBERCPU_DEFINE_REGION_CHAIN(FiberLLVMRegionChainDouble, 1)
#undef FIBERCPU_DEFINE_REGION_CHAIN

extern "C" const HandlerFunc FiberLLVMRegionMemoryExits[] = {&MemoryOpRestart, &MemoryOpRetry};
#endif

#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions)                                           \
    extern "C" LogicFlow FiberLLVMGeneric_##Name(uint32_t, uint32_t, uint32_t, LogicFuncParams) {             \
        return op::Name(LogicPassParams);                                                                     \
    }                                                                                                         \
    extern "C" LogicFlow FiberLLVMTemplate_##Name(uint32_t encoding, uint32_t ea_desc, uint32_t displacement, \
                                                  LogicFuncParams) {                                          \
        const auto metadata = uint8_t(encoding >> 16);                                                        \
        const OperandSpec::Baked operands{uint8_t(encoding >> 8), ea_desc, displacement};                     \
        gpr.baked_address = DecodedOperandShape::HasMemoryAddress(metadata) ? &operands.address : nullptr;    \
        return Exact;                                                                                         \
    }
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP

// Dense function slots in catalog ID order, with the generic template first.
struct LLVMOperationTable {
    char fingerprint[block_recipe::kOpIdFingerprint.size() + 1];
    decltype(&FiberLLVMPlaceholder) kernels[block_recipe::kOpIdCount * 2];
};

extern "C" const LLVMOperationTable FiberLLVMOperationTable = {
    FIBERCPU_LLVM_OP_ID_FINGERPRINT,
    {

#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions) &FiberLLVMGeneric_##Name, &FiberLLVMTemplate_##Name,
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
    },
};

// Clang supplies the exact threaded ABI, step dispatch, and SuperOpcode exits
// once per template instead of unrolling a fixed maximum block skeleton.
extern "C" ATTR_PRESERVE_NONE int64_t FiberLLVMStep(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                    int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                    mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    auto utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    const auto layout = FiberLLVMOperandLayout();
    const auto slots = layout & 3;
    const auto imm = layout == kLLVMBranchLinkSlot ? op->Primary()
                     : slots == 2                  ? (op + 1)->Primary()
                     : op->HasMemory()             ? GetSingleSlotMemoryImmediate(op)
                                                   : op->Primary();
    auto flow = FiberLLVMPlaceholder(0, 0, 0, state, op, &utlb, imm, 0, &branch, flags_cache, FIBERCPU_GPR_CACHE);
    if (flow != LogicFlow::Continue) {
        HANDLE_SUPEROPCODE_FLOW(flow, state, op, instr_limit, utlb, utlb_tags, utlb_addend, branch, flags_cache,
                                FiberLLVMBranchSuffix());
    }
    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);
    ATTR_MUSTTAIL return FiberLLVMNextStep(state, op + slots, instr_limit, utlb_tags, utlb_addend, branch,
                                           flags_cache FIBERCPU_PIN_RELOAD);
}

// Fast-path step template for kernels proven to always return LogicFlow::Continue.
extern "C" ATTR_PRESERVE_NONE int64_t FiberLLVMStepContinue(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                            int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                            mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                            uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    auto utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    const auto layout = FiberLLVMOperandLayout();
    const auto slots = layout & 3;
    const auto imm = layout == kLLVMBranchLinkSlot ? op->Primary()
                     : slots == 2                  ? (op + 1)->Primary()
                     : op->HasMemory()             ? GetSingleSlotMemoryImmediate(op)
                                                   : op->Primary();
    (void)FiberLLVMPlaceholder(0, 0, 0, state, op, &utlb, imm, 0, &branch, flags_cache, FIBERCPU_GPR_CACHE);
    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);
    ATTR_MUSTTAIL return FiberLLVMNextStep(state, op + slots, instr_limit, utlb_tags, utlb_addend, branch,
                                           flags_cache FIBERCPU_PIN_RELOAD);
}

extern "C" ATTR_PRESERVE_NONE int64_t FiberLLVMTail(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                    int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                    mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    switch (FiberLLVMExit()) {
        case LLVMBlockExit::Fallthrough:
            ATTR_MUSTTAIL return OpExitBlockImpl<false>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                        flags_cache FIBERCPU_PIN_RELOAD);
        case LLVMBlockExit::Branch:
            ATTR_MUSTTAIL return OpExitBlockImpl<true>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                       flags_cache FIBERCPU_PIN_RELOAD);
        case LLVMBlockExit::Dispatch:
            break;
    }
    RestoreLLVMBlockOnExit(state);
    ATTR_MUSTTAIL return op->handler(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                     flags_cache FIBERCPU_PIN_RELOAD);
}

}  // namespace fiberish
