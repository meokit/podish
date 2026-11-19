#include "jit/llvm_block_jit.h"
#include "ops/ops_alu_impl.h"
#include "ops/ops_compare_impl.h"
#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_groups_impl.h"
#include "ops/ops_shift_bit_impl.h"
#include "superopcodes.h"

namespace fiberish {

extern "C" bool FiberLLVMUseStep(unsigned index);
extern "C" LLVMBlockExit FiberLLVMExit();
extern "C" LogicFlow FiberLLVMPlaceholder(unsigned index, uint32_t encoding, uint32_t ea_desc, uint32_t displacement,
                                          LogicFuncParams);

template <LogicFunc Live, LogicFunc Dead>
FORCE_INLINE LogicFlow RunLLVMFlagPolicy(uint8_t metadata, LogicFuncParams) {
    if (metadata & Meta::no_flags_mask) return Dead(LogicPassParams);
    return Live(LogicPassParams);
}

#define FIBERCPU_LLVM_OP(Name, Exact)                                                                       \
    extern "C" LogicFlow FiberLLVMGeneric_##Name(unsigned, uint32_t, uint32_t, uint32_t, LogicFuncParams) { \
        return op::Name(LogicPassParams);                                                                   \
    }                                                                                                       \
    extern "C" LogicFlow FiberLLVMTemplate_##Name(unsigned, uint32_t encoding, uint32_t ea_desc,            \
                                                  uint32_t displacement, LogicFuncParams) {                 \
        const auto metadata = uint8_t(encoding >> 16);                                                      \
        __builtin_assume(op->modrm == uint8_t(encoding));                                                   \
        __builtin_assume(op->prefixes.all == uint8_t(encoding >> 8));                                       \
        __builtin_assume((op->meta.all & DecodedOperandShape::metadata_mask) == metadata);                  \
        __builtin_assume(op->len == uint8_t(encoding >> 24));                                               \
        if ((metadata & 0xc2) == 2) {                                                                       \
            __builtin_assume(op->ext.data.ea_desc == ea_desc);                                              \
            __builtin_assume(op->ext.data.disp == displacement);                                            \
        }                                                                                                   \
        return Exact;                                                                                       \
    }
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP

#define FIBERCPU_LLVM_STEP(I)                                                                                      \
    do {                                                                                                           \
        if (!FiberLLVMUseStep(I)) goto dispatch_tail;                                                              \
        auto flow = FiberLLVMPlaceholder(I, 0, 0, 0, state, current, &utlb, GetImm(current), &branch, flags_cache, \
                                         FIBERCPU_GPR_CACHE);                                                      \
        if (flow != LogicFlow::Continue) {                                                                         \
            HANDLE_SUPEROPCODE_FLOW(flow, state, current, instr_limit, utlb, utlb_tags, utlb_addend, branch,       \
                                    flags_cache);                                                                  \
        }                                                                                                          \
        current = NextOp(current);                                                                                 \
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
