#include "jit/block_recipe.h"

#include <ankerl/unordered_dense.h>
#include <algorithm>

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

enum LLVMRequirement : uint8_t {
    kRegisterOperand = 1,
    kMemoryOperand = 2,
    kExactOnly = 4,
};

struct LLVMOperation {
    const char* name;
    uint8_t requirements;
    uint8_t extensions;

    bool Matches(const DecodedOp& op, bool exact) const {
        if ((requirements & kRegisterOperand) && (!op.meta.flags.has_modrm || (op.modrm >> 6) != 3)) return false;
        if ((requirements & kMemoryOperand) && !HasMem(&op)) return false;
        if ((requirements & kExactOnly) && !exact) return false;
        return extensions == 0xff || (op.meta.flags.has_modrm && (extensions & (1u << ((op.modrm >> 3) & 7))));
    }
};

const auto& Operations() {
    static const ankerl::unordered_dense::map<HandlerFunc, LLVMOperation> operations = {
#define FIBERCPU_LLVM_OP(Name, Exact, Requirements, Extensions) \
    {DispatchWrapper<op::Name>, {#Name, Requirements, Extensions}},
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
    };
    return operations;
}

}  // namespace

uint16_t HotThreshold() {
#if FIBERCPU_LLVM_JIT
    if (const auto online = LLVMJitHotThreshold()) return online;
#endif
#if FIBERCPU_LINKED_BLOCK_CACHE
    return LinkedBlockCacheEnabled() ? 1 : 0;
#else
    return 0;
#endif
}

void InitializeOperationLookup() { (void)Operations(); }

const char* LogicName(const DecodedOp& op, bool exact) {
    const auto& operations = Operations();
    const auto match = operations.find(op.handler);
    return match != operations.end() && match->second.Matches(op, exact) ? match->second.name : nullptr;
}

std::string BlockKey(std::span<const Step> steps, bool exact, LLVMBlockExit exit) {
    std::string key(1, static_cast<char>(exit));
    const auto append_u32 = [&](uint32_t value) {
        for (unsigned shift = 0; shift < 32; shift += 8) key.push_back(static_cast<char>(value >> shift));
    };
    for (const auto& step : steps) {
        key += step.name;
        key += '\0';
        if (exact) {
            append_u32(step.shape.encoding);
            append_u32(step.shape.ea_desc);
            append_u32(step.shape.displacement);
            append_u32(step.shape.immediate);
        }
    }
    return key;
}

bool CanPromoteBlock(EmuState& state, BasicBlock& block) {
    return !state.mmu.code_cache_flush_pending() && block.is_valid() && block.has_unfused_handlers &&
           block.entry == block.FirstOp()->handler && state.mmu.lookup_cached_block(block.start_eip()) == &block;
}

bool CanPlanOp(const DecodedOp& op) {
    return op.len && !op.prefixes.flags.lock && (!HasMem(&op) || GetExtKind(&op) == ExtKind::Data);
}

LLVMBlockExit FullBlockExit(const BasicBlock& block) {
    auto handler = block.Sentinel()->handler;
    if (std::ranges::find(g_ExitHandlersFallthrough, handler) != g_ExitHandlersFallthrough.end())
        return LLVMBlockExit::Fallthrough;
    if (std::ranges::find(g_ExitHandlersBranch, handler) != g_ExitHandlersBranch.end()) return LLVMBlockExit::Branch;
    return LLVMBlockExit::Dispatch;
}

bool PlanBlock(EmuState& state, const BasicBlock& block, bool exact, Plan& plan) {
    const auto ops = std::span(block.FirstOp(), std::min<size_t>(block.sentinel_slot_index, kMaxOps));
    return PlanBlock(
        ops, block.sentinel_slot_index, FullBlockExit(block), exact,
        [&](const DecodedOp& op, DecodedOp& generic) { return RedecodeGenericInstruction(&state, op, generic); }, plan);
}

void InstallBlockHandler(BasicBlock& block, std::span<const HandlerFunc> replay_handlers, HandlerFunc entry) {
    // Replay a slow load on its original scalar slot. The uncompiled suffix
    // keeps its independently selected SuperOpcodes and operand handlers.
    auto* ops = block.FirstOp();
    for (size_t i = 0; i < replay_handlers.size(); ++i) {
        ops[i].handler = replay_handlers[i];
        ops[i].meta.flags.operand_lowered = 0;
    }
    block.entry = entry;
}

}  // namespace fiberish::block_recipe
