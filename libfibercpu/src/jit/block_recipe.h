#pragma once

#include "instruction_variants.h"
#include "jit/llvm_block_jit.h"
#include "llvm_op_ids.h"

#include <algorithm>
#include <array>
#include <span>

namespace fiberish::block_recipe {

inline constexpr size_t kMaxOps = 64;

enum LLVMRequirement : uint8_t {
    kRegisterOperand = 1,
    kMemoryOperand = 2,
    kExactOnly = 4,
    kMemoryWith66 = 8,
};

struct Operation {
    OpId id;
    uint8_t requirements;
    uint8_t extensions;

    bool Matches(const DecodedInstruction& op, bool exact) const {
        if ((requirements & kRegisterOperand) && (!op.meta.flags.has_modrm || (op.modrm >> 6) != 3)) return false;
        if ((requirements & kMemoryOperand) && !HasMem(&op)) return false;
        if ((requirements & kExactOnly) && !exact) return false;
        if ((requirements & kMemoryWith66) && op.prefixes.flags.opsize && !HasMem(&op)) return false;
        return extensions == 0xff || (op.meta.flags.has_modrm && (extensions & (1u << ((op.modrm >> 3) & 7))));
    }
};

inline bool CanPlanOp(const DecodedInstruction& op) {
    return op.len && !op.prefixes.flags.lock && (!HasMem(&op) || GetExtKind(&op) == ExtKind::Data);
}

struct BoundOperation {
    OpId id;
    BoundOperandShape shape;
    auto operator<=>(const BoundOperation&) const = default;
};

// The presence of operands distinguishes exact keys from generic keys.
// Generic kernels still bake the mandatory runtime binding.
struct BlockKeyView {
    LLVMBlockExit exit;
    std::span<const BoundOperation> operations;
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
    std::array<BoundOperation, kMaxOps> operations;
    std::array<DecodedOperandShape, kMaxOps> operands;
    std::array<HandlerFunc, kMaxOps> replay_handlers;
    size_t covered = 0;
    LLVMBlockExit exit = LLVMBlockExit::Dispatch;

    BlockKeyView Key(bool exact) const {
        return {exit, {operations.data(), covered}, {operands.data(), exact ? covered : 0}};
    }
    std::span<const HandlerFunc> ReplayHandlers() const { return {replay_handlers.data(), covered}; }
};

uint16_t HotThreshold(const EmuState& state);
void InitializeOperationLookup();
OpId FindOperation(const DecodedInstruction& op, const BoundScalarHandler& binding, bool exact);
bool CanPromoteBlock(EmuState& state, BasicBlock& block);
LLVMBlockExit FullBlockExit(const BasicBlock& block);

template <typename Ops, typename Redecode>
bool PlanBlock(Ops&& ops, size_t sentinel, LLVMBlockExit full_exit, bool exact, Redecode redecode, Plan& plan) {
    plan.covered = 0;
    for (const auto& op : ops) {
        if (!CanPlanOp(op)) break;
        DecodedInstruction generic;
        if (!redecode(op, generic)) return false;
        const auto binding = BindScalarHandler(generic);
        if (!binding.handler) break;
        const auto id = FindOperation(generic, binding, exact);
        if (id == kInvalidOpId) break;
        plan.operations[plan.covered] = {id, binding.shape};
        if (exact) plan.operands[plan.covered] = DecodedOperandShape::Read(&generic);
        plan.replay_handlers[plan.covered++] = binding.handler;
    }
    plan.exit = plan.covered == sentinel ? full_exit : LLVMBlockExit::Dispatch;
    return plan.covered > 1 || (plan.covered == 1 && plan.exit != LLVMBlockExit::Dispatch);
}

bool PlanBlock(EmuState& state, const BasicBlock& block, bool exact, Plan& plan);
void InstallBlockHandler(BasicBlock& block, std::span<const HandlerFunc> replay_handlers, HandlerFunc entry);

}  // namespace fiberish::block_recipe
