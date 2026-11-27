#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <span>
#include <string>

#include <llvm/ADT/STLFunctionalExtras.h>
#include <llvm/ADT/StringRef.h>
#include <llvm/IR/IRBuilder.h>
#include <llvm/IR/PassManager.h>
#include <llvm/Support/Error.h>

namespace llvm {
class Function;
class LoadInst;
class Module;
class TargetMachine;
class Type;
class Value;
}  // namespace llvm

namespace fiberish {

bool InitializeLLVMTarget();
llvm::Expected<std::unique_ptr<llvm::TargetMachine>> CreateLLVMObjectTarget(const llvm::Module& templates);
llvm::Expected<std::string> ParseLLVMBlockPipeline(llvm::StringRef level, llvm::StringRef passes);

struct LLVMOptimizeOptions {
    bool prune_dead_globals = false;
    llvm::StringRef custom_pipeline = {};
    bool fold_operand_loads = false;
    llvm::function_ref<bool()> should_cancel = {};
    llvm::function_ref<void(llvm::Function&, llvm::FunctionAnalysisManager&)> pre_pipeline = {};
};

bool OptimizeLLVMFunction(llvm::Function& entry, const LLVMOptimizeOptions& options);

// Load an immutable field from a published PageRegion table.
llvm::LoadInst* LoadInvariantRegionField(llvm::IRBuilder<>& builder, llvm::Type* type, llvm::Value* field);

struct DecodedRegionSelector {
    llvm::Value* index = nullptr;
    llvm::Value* table = nullptr;
    llvm::Value* page = nullptr;
};

// Unpack a tagged PageRegion pointer (table | member_index) and load guest_page.
DecodedRegionSelector DecodeRegionSelector(llvm::IRBuilder<>& builder, llvm::Value* tagged_arg, uint64_t member_mask,
                                           size_t guest_page_offset, llvm::Type* index_type, llvm::Type* page_type);

// Emit external musttail entry wrappers `<name>_0`..`<name>_N` that load the
// PageRegion pointer from args[1] + context_offsets[i], tag it with `i`, and
// tail-call into `body`.
void EmitRegionEntryWrappers(llvm::Module& module, llvm::Function& body, llvm::StringRef name,
                             std::span<const size_t> context_offsets);

struct CyclicStatePromotionSpec {
    unsigned field_bits = 64;
    std::span<const size_t> offsets;
    // When true, rely on LLVM AliasAnalysis against all memory ops in `body`
    // (requires alias scopes separating state from guest memory). When false,
    // conservatively walk all uses of `arg(0)` to guarantee state does not escape.
    bool use_alias_analysis = true;
    bool clear_aa_metadata = false;
    // Restrict promotion to irreducible CFGs where LLVM LICM cannot hoist
    // register file loads/stores on its own (avoids over-promoting large
    // 32-GPR reducible loops on AArch64 after O2).
    bool only_irreducible = false;
};

// Promote scalar fields of `arg(0)` accessed inside cyclic SCCs of a region
// body into SSA via entry allocas and Mem2Reg, flushing modified fields before
// musttail exits and non-cyclic volatile 128-bit chain header checks.
void PromoteCyclicStateFields(llvm::Function& body, const CyclicStatePromotionSpec& spec,
                              llvm::FunctionAnalysisManager* functions = nullptr);

}  // namespace fiberish
