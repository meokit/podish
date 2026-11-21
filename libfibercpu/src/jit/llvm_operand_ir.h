#pragma once

#include <memory>
#include <string>
#include <vector>

#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/ADT/StringRef.h>
#include <llvm/Support/Error.h>

#include "jit/block_recipe.h"

namespace llvm {
class Module;
class Function;
class GlobalValue;
class GlobalVariable;
class TargetMachine;
}  // namespace llvm

namespace fiberish {

bool InitializeLLVMTarget();
llvm::Expected<std::unique_ptr<llvm::TargetMachine>> CreateLLVMObjectTarget(const llvm::Module& templates);
class LLVMOperandTemplates {
public:
    explicit LLVMOperandTemplates(const llvm::Module* module);

    bool IsValid() const { return table_ != nullptr; }
    const llvm::Module& Module() const { return *module_; }
    const llvm::Function* Get(block_recipe::OpId id, bool exact) const;
    bool IsTemplateGlobal(const llvm::GlobalValue* value) const;

private:
    const llvm::Module* module_;
    const llvm::GlobalVariable* table_ = nullptr;
    std::array<const llvm::Function*, block_recipe::kOpIdCount * 2> functions_{};
    llvm::SmallPtrSet<const llvm::GlobalValue*, 32> kernels_;
};

llvm::Expected<std::string> ParseLLVMBlockPipeline(llvm::StringRef level, llvm::StringRef passes);

std::unique_ptr<llvm::Module> PrepareLLVMBlock(const LLVMOperandTemplates& templates, block_recipe::BlockKeyView key,
                                               llvm::StringRef entry_name, bool prune_dead_globals = false,
                                               llvm::StringRef custom_pipeline = {}, bool fold_operand_loads = false);

struct LLVMPageMember {
    block_recipe::BlockKeyView key;
    uint32_t page_offset;
};

std::unique_ptr<llvm::Module> PrepareLLVMPageRegion(const LLVMOperandTemplates& templates,
                                                    std::span<const LLVMPageMember> candidates, llvm::StringRef name,
                                                    llvm::StringRef pipeline, bool fold_operand_loads,
                                                    std::vector<size_t>& included);

}  // namespace fiberish
