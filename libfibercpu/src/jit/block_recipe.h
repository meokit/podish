#pragma once

#include "jit/llvm_block_jit.h"
#include "llvm_op_ids.h"
#include "operand_policy.h"

#include <algorithm>
#include <array>
#include <span>

namespace fiberish::block_recipe {

inline constexpr size_t kMaxOps = 64;

enum LLVMRequirement : uint8_t {
    kRegisterOperand = 1,
    kMemoryOperand = 2,
    kExactOnly = 4,
};

struct Operation {
    OpId id;
    uint8_t requirements;
    uint8_t extensions;

    bool Matches(const DecodedOp& op, bool exact) const {
        if ((requirements & kRegisterOperand) && (!op.meta.flags.has_modrm || (op.modrm >> 6) != 3)) return false;
        if ((requirements & kMemoryOperand) && !HasMem(&op)) return false;
        if ((requirements & kExactOnly) && !exact) return false;
        return extensions == 0xff || (op.meta.flags.has_modrm && (extensions & (1u << ((op.modrm >> 3) & 7))));
    }
};

inline bool CanPlanOp(const DecodedOp& op) {
    return op.len && !op.prefixes.flags.lock && (!HasMem(&op) || GetExtKind(&op) == ExtKind::Data);
}

// The presence of operands distinguishes exact keys from generic keys.
struct BlockKeyView {
    LLVMBlockExit exit;
    std::span<const OpId> operations;
    std::span<const DecodedOperandShape> operands;

    bool IsExact() const { return !operands.empty(); }

    bool operator==(const BlockKeyView& other) const {
        return exit == other.exit && std::ranges::equal(operations, other.operations) &&
               std::ranges::equal(operands, other.operands);
    }

    // Keep this field order in sync with extract_linked_block_cache.py.
    auto operator<=>(const BlockKeyView& other) const {
        if (auto order = exit <=> other.exit; order != 0) return order;
        if (auto order = IsExact() <=> other.IsExact(); order != 0) return order;
        if (auto order = std::lexicographical_compare_three_way(operations.begin(), operations.end(),
                                                                other.operations.begin(), other.operations.end());
            order != 0)
            return order;
        return std::lexicographical_compare_three_way(operands.begin(), operands.end(), other.operands.begin(),
                                                      other.operands.end());
    }
};

struct Plan {
    std::array<OpId, kMaxOps> operations;
    std::array<DecodedOperandShape, kMaxOps> operands;
    std::array<HandlerFunc, kMaxOps> replay_handlers;
    size_t covered = 0;
    LLVMBlockExit exit = LLVMBlockExit::Dispatch;

    BlockKeyView Key(bool exact) const {
        return {exit, {operations.data(), covered}, {operands.data(), exact ? covered : 0}};
    }
    std::span<const HandlerFunc> ReplayHandlers() const { return {replay_handlers.data(), covered}; }
};

uint16_t HotThreshold();
void InitializeOperationLookup();
OpId FindOperation(const DecodedOp& op, bool exact);
bool CanPromoteBlock(EmuState& state, BasicBlock& block);
LLVMBlockExit FullBlockExit(const BasicBlock& block);

template <typename Redecode>
bool PlanBlock(std::span<const DecodedOp> ops, size_t sentinel, LLVMBlockExit full_exit, bool exact, Redecode redecode,
               Plan& plan) {
    plan.covered = 0;
    for (const auto& op : ops) {
        if (!CanPlanOp(op)) break;
        DecodedOp generic;
        if (!redecode(op, generic)) return false;
        const auto id = FindOperation(generic, exact);
        if (id == kInvalidOpId) break;
        plan.operations[plan.covered] = id;
        if (exact) plan.operands[plan.covered] = DecodedOperandShape::Read(&generic);
        plan.replay_handlers[plan.covered++] = generic.handler;
    }
    plan.exit = plan.covered == sentinel ? full_exit : LLVMBlockExit::Dispatch;
    return plan.covered > 1 || (plan.covered == 1 && plan.exit != LLVMBlockExit::Dispatch);
}

bool PlanBlock(EmuState& state, const BasicBlock& block, bool exact, Plan& plan);
void InstallBlockHandler(BasicBlock& block, std::span<const HandlerFunc> replay_handlers, HandlerFunc entry);

}  // namespace fiberish::block_recipe
