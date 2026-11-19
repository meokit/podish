#pragma once

#include <memory>
#include <span>

#include <llvm/ADT/StringRef.h>
#include <llvm/Support/Error.h>

#include "jit/block_recipe.h"

namespace llvm {
class Module;
class TargetMachine;
}  // namespace llvm

namespace fiberish {

bool InitializeLLVMTarget();
llvm::Expected<std::unique_ptr<llvm::TargetMachine>> CreateLLVMObjectTarget(const llvm::Module& templates);
std::unique_ptr<llvm::Module> PrepareLLVMBlock(const llvm::Module& templates, std::span<const block_recipe::Step> steps,
                                               bool exact, LLVMBlockExit exit, llvm::StringRef entry_name);

}  // namespace fiberish
