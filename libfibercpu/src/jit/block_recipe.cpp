#include "jit/block_recipe.h"

#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <ranges>

#if FIBERCPU_LINKED_BLOCK_CACHE
#include "jit/linked_block_cache.h"
#endif

#include "ops.h"
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

namespace fiberish::block_recipe {
namespace {

const auto& Operations() {
    static const ankerl::unordered_dense::map<uint32_t, Operation> operations = {
#define FIBERCPU_LLVM_OP(Id, Name, Exact, Requirements, Extensions) \
    {FindScalarTrainingId(DispatchWrapper<op::Name>), {OpId{Id}, Requirements, Extensions}},
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
    };
    return operations;
}

}  // namespace

uint16_t HotThreshold(const EmuState& state) {
#if FIBERCPU_LLVM_JIT
    if (const auto online = LLVMJitHotThreshold(state)) return online;
#else
    (void)state;
#endif
#if FIBERCPU_LINKED_BLOCK_CACHE
    return LinkedBlockCacheEnabled() ? 1 : 0;
#else
    return 0;
#endif
}

void InitializeOperationLookup() { (void)Operations(); }

OpId FindOperation(const DecodedInstruction& op, const BoundScalarHandler& binding, bool exact) {
    const auto& operations = Operations();
    const auto match = operations.find(binding.id);
    return match != operations.end() && match->second.Matches(op, exact) ? match->second.id : kInvalidOpId;
}

bool CanPromoteBlock(EmuState& state, BasicBlock& block) {
    return !state.mmu.code_cache_flush_pending() && block.is_valid() && block.has_unfused_handlers &&
           block.entry == block.FirstOp()->handler && state.mmu.lookup_cached_block(block.start_eip()) == &block;
}

LLVMBlockExit FullBlockExit(const BasicBlock& block) {
    auto handler = block.Sentinel()->handler;
    if (std::ranges::find(g_ExitHandlersFallthrough, handler) != g_ExitHandlersFallthrough.end())
        return LLVMBlockExit::Fallthrough;
    if (std::ranges::find(g_ExitHandlersBranch, handler) != g_ExitHandlersBranch.end()) return LLVMBlockExit::Branch;
    return LLVMBlockExit::Dispatch;
}

bool PlanBlock(EmuState& state, const BasicBlock& block, bool exact, Plan& plan) {
    const auto count = std::min<size_t>(block.inst_count(), kMaxOps);
    const auto ops =
        std::views::iota(size_t{0}, count) | std::views::transform([&](size_t slot) { return block.ReadOp(slot); });
    return PlanBlock(
        ops, block.inst_count(), FullBlockExit(block), exact,
        [&](const DecodedInstruction& op, DecodedInstruction& generic) {
            return RedecodeGenericInstruction(&state, op, generic);
        },
        plan);
}

void InstallBlockHandler(BasicBlock& block, std::span<const HandlerFunc> replay_handlers, HandlerFunc entry) {
    // Replay a slow load on its original scalar slot. The uncompiled suffix
    // keeps its independently selected SuperOpcodes and operand handlers.
    auto* op = block.FirstOp();
    for (const auto handler : replay_handlers) {
        op->handler = handler;
        op->ClearOperandLowered();
        op = NextOp(op);
    }
    block.entry = entry;
}

}  // namespace fiberish::block_recipe
