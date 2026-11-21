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

extern "C" bool FiberLLVMUseStep(unsigned index);
extern "C" unsigned FiberLLVMOperandLayout(unsigned index);
extern "C" LLVMBlockExit FiberLLVMExit();
extern "C" LogicFlow FiberLLVMPlaceholder(unsigned index, uint32_t encoding, uint32_t ea_desc, uint32_t displacement,
                                          LogicFuncParams);

template <LogicFunc Live, LogicFunc Dead>
FORCE_INLINE LogicFlow RunLLVMFlagPolicy(uint8_t metadata, LogicFuncParams) {
    if (metadata & Meta::no_flags_mask) return Dead(LogicPassParams);
    return Live(LogicPassParams);
}

#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions)                                         \
    extern "C" LogicFlow FiberLLVMGeneric_##Name(unsigned, uint32_t, uint32_t, uint32_t, LogicFuncParams) { \
        return op::Name(LogicPassParams);                                                                   \
    }                                                                                                       \
    extern "C" LogicFlow FiberLLVMTemplate_##Name(unsigned, uint32_t encoding, uint32_t ea_desc,            \
                                                  uint32_t displacement, LogicFuncParams) {                 \
        const auto metadata = uint8_t(encoding >> 16);                                                      \
        const OperandSpec::Baked operands{uint8_t(encoding >> 8), ea_desc, displacement};                   \
        gpr.baked_address = DecodedOperandShape::HasMemoryAddress(metadata) ? &operands.address : nullptr;  \
        return Exact;                                                                                       \
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

#define FIBERCPU_LLVM_STEP(I)                                                                                          \
    do {                                                                                                               \
        if (!FiberLLVMUseStep(I)) goto dispatch_tail;                                                                  \
        const auto layout = FiberLLVMOperandLayout(I);                                                               \
        const auto slots = layout & 3;                                                                               \
        const auto imm = layout == kLLVMBranchLinkSlot ? current->Primary()                                           \
                         : slots == 2                   ? (current + 1)->Primary()                                    \
                         : current->HasMemory()         ? GetSingleSlotMemoryImmediate(current)                       \
                                                        : current->Primary();                                         \
        auto flow =                                                                                                    \
            FiberLLVMPlaceholder(I, 0, 0, 0, state, current, &utlb, imm, 0, &branch, flags_cache, FIBERCPU_GPR_CACHE); \
        if (flow != LogicFlow::Continue) {                                                                             \
            HANDLE_SUPEROPCODE_FLOW(flow, state, current, instr_limit, utlb, utlb_tags, utlb_addend, branch,           \
                                    flags_cache);                                                                      \
        }                                                                                                              \
        current += slots;                                                                                              \
    } while (false)

// Clang supplies the exact threaded ABI and shared SuperOpcode exits. Step
// guards and calls are replaced before code generation.
extern "C" ATTR_PRESERVE_NONE int64_t FiberLLVMEntry(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                     int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                     mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                     uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    auto utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    auto* current = op;
#include "llvm_block_entries.inc"

dispatch_tail:
    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);
    switch (FiberLLVMExit()) {
        case LLVMBlockExit::Fallthrough:
            ATTR_MUSTTAIL return OpExitBlockImpl<false>(state, current, instr_limit, utlb_tags, utlb_addend, branch,
                                                        flags_cache FIBERCPU_PIN_RELOAD);
        case LLVMBlockExit::Branch:
            ATTR_MUSTTAIL return OpExitBlockImpl<true>(state, current, instr_limit, utlb_tags, utlb_addend, branch,
                                                       flags_cache FIBERCPU_PIN_RELOAD);
        case LLVMBlockExit::Dispatch:
            break;
    }
    ATTR_MUSTTAIL return current->handler(state, current, instr_limit, utlb_tags, utlb_addend, branch,
                                          flags_cache FIBERCPU_PIN_RELOAD);
}
#undef FIBERCPU_LLVM_STEP

}  // namespace fiberish
