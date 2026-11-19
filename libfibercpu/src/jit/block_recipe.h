#pragma once

#include "jit/llvm_block_jit.h"
#include "operand_policy.h"

#include <array>
#include <span>
#include <string>
#include <string_view>

namespace fiberish::block_recipe {

inline constexpr size_t kMaxOps = 64;

struct Step {
    std::string_view name;
    DecodedOperandShape shape;
};

struct Plan {
    std::array<Step, kMaxOps> steps;
    std::array<HandlerFunc, kMaxOps> replay_handlers;
    size_t covered = 0;
    LLVMBlockExit exit = LLVMBlockExit::Dispatch;

    std::span<const Step> Steps() const { return {steps.data(), covered}; }
    std::span<const HandlerFunc> ReplayHandlers() const { return {replay_handlers.data(), covered}; }
};

uint16_t HotThreshold();
void InitializeOperationLookup();
const char* LogicName(const DecodedOp& op, bool exact);
std::string BlockKey(std::span<const Step> steps, bool exact, LLVMBlockExit exit);
bool CanPromoteBlock(EmuState& state, BasicBlock& block);
bool CanPlanOp(const DecodedOp& op);
LLVMBlockExit FullBlockExit(const BasicBlock& block);

template <typename Redecode>
bool PlanBlock(std::span<const DecodedOp> ops, size_t sentinel, LLVMBlockExit full_exit, bool exact, Redecode redecode,
               Plan& plan) {
    plan.covered = 0;
    for (const auto& op : ops) {
        if (!CanPlanOp(op)) break;
        DecodedOp generic;
        if (!redecode(op, generic)) return false;
        const auto* name = LogicName(generic, exact);
        if (!name) break;
        plan.steps[plan.covered] = {name, DecodedOperandShape::Read(&generic)};
        plan.replay_handlers[plan.covered++] = generic.handler;
    }
    plan.exit = plan.covered == sentinel ? full_exit : LLVMBlockExit::Dispatch;
    return plan.covered > 1 || (plan.covered == 1 && plan.exit != LLVMBlockExit::Dispatch);
}

bool PlanBlock(EmuState& state, const BasicBlock& block, bool exact, Plan& plan);
void InstallBlockHandler(BasicBlock& block, std::span<const HandlerFunc> replay_handlers, HandlerFunc entry);

}  // namespace fiberish::block_recipe
