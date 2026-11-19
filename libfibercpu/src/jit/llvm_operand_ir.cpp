#include "jit/llvm_operand_ir.h"

#include <vector>

#include <llvm/ADT/SmallBitVector.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/ExecutionEngine/Orc/JITTargetMachineBuilder.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/PassManager.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/TargetSelect.h>
#include <llvm/Transforms/IPO/GlobalDCE.h>
#include <llvm/Transforms/Scalar/SimplifyCFG.h>
#include <llvm/Transforms/Utils/Cloning.h>
#include <llvm/Transforms/Utils/Local.h>

namespace fiberish {
namespace {

bool InlineLLVMKernel(llvm::CallInst& call, llvm::Function& kernel, const DecodedOperandShape* shape) {
    if (kernel.isDeclaration() || kernel.getFunctionType() != call.getFunctionType()) return false;
    call.setCalledFunction(&kernel);
    if (shape) {
        // The template ABI starts with the step index and three decoded fields,
        // followed by LogicFuncParams, whose immediate is the fourth argument.
        const auto bind = [&](unsigned arg, uint32_t value) {
            call.setArgOperand(arg, llvm::ConstantInt::get(call.getArgOperand(arg)->getType(), value));
        };
        bind(1, shape->encoding);
        bind(2, shape->ea_desc);
        bind(3, shape->displacement);
        bind(7, shape->immediate);
    }
    llvm::InlineFunctionInfo info;
    return llvm::InlineFunction(call, info).isSuccess();
}

bool OptimizeLLVMModule(llvm::Module& module, bool prune_dead_globals, llvm::StringRef custom_pipeline) {
    llvm::LoopAnalysisManager loops;
    llvm::FunctionAnalysisManager functions;
    llvm::CGSCCAnalysisManager call_graph;
    llvm::ModuleAnalysisManager modules;
    llvm::PassBuilder passes;
    passes.registerModuleAnalyses(modules);
    passes.registerCGSCCAnalyses(call_graph);
    passes.registerFunctionAnalyses(functions);
    passes.registerLoopAnalyses(loops);
    passes.crossRegisterProxies(loops, functions, call_graph, modules);
    llvm::ModulePassManager pipeline;
    // The copied kernels have already been inlined into the entry. Prune
    // unreachable definitions before spending optimization time on them.
    if (prune_dead_globals) pipeline.addPass(llvm::GlobalDCEPass());
    if (custom_pipeline.empty()) {
        pipeline.addPass(passes.buildPerModuleDefaultPipeline(llvm::OptimizationLevel::O2));
    } else {
        // Resolve the constantized step and exit guards even for a minimal
        // user pipeline. Unused template bodies must never reach codegen.
        pipeline.addPass(llvm::createModuleToFunctionPassAdaptor(llvm::SimplifyCFGPass()));
        if (auto error = passes.parsePassPipeline(pipeline, custom_pipeline)) {
            llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "LLVM block pipeline: ");
            return false;
        }
        pipeline.addPass(llvm::GlobalDCEPass());
    }
    pipeline.run(module, modules);
    return true;
}

}  // namespace

llvm::Expected<std::string> ParseLLVMBlockPipeline(llvm::StringRef level, llvm::StringRef passes) {
    if (!level.empty() && !passes.empty())
        return llvm::createStringError("select an LLVM optimization level or a pass pipeline");
    if (!passes.empty()) {
        llvm::PassBuilder builder;
        llvm::ModulePassManager pipeline;
        if (auto error = builder.parsePassPipeline(pipeline, passes)) return std::move(error);
        return passes.str();
    }
    level.consume_front("-");
    if (level.empty() || level == "O2") return std::string{};
    if (level != "O0" && level != "O1" && level != "O3" && level != "Os" && level != "Oz")
        return llvm::createStringError("invalid LLVM optimization level: %s", level.str().c_str());
    return "default<" + level.str() + ">";
}

bool InitializeLLVMTarget() {
    static const bool initialized = !llvm::InitializeNativeTarget() && !llvm::InitializeNativeTargetAsmPrinter();
    return initialized;
}

llvm::Expected<std::unique_ptr<llvm::TargetMachine>> CreateLLVMObjectTarget(const llvm::Module& templates) {
    auto target = llvm::orc::JITTargetMachineBuilder::detectHost();
    if (!target) return target.takeError();
    // Host CPU code generation with the deployment target embedded in the interpreter bitcode.
    target->getTargetTriple() = templates.getTargetTriple();
    target->setRelocationModel(llvm::Reloc::PIC_).setOptions(llvm::TargetOptions{});
    return target->createTargetMachine();
}

LLVMOperandTemplates::LLVMOperandTemplates(const llvm::Module* module) : module_(module) {
    if (!module_) return;
    const auto* table = module_->getNamedGlobal("FiberLLVMOperationTable");
    if (!table || !table->hasInitializer()) return;
    const auto* contents = llvm::dyn_cast<llvm::ConstantStruct>(table->getInitializer());
    if (!contents || contents->getNumOperands() != 2) return;
    const auto* fingerprint = llvm::dyn_cast<llvm::ConstantDataArray>(contents->getOperand(0));
    if (!fingerprint || !fingerprint->isCString() ||
        fingerprint->getAsCString() != llvm::StringRef(block_recipe::kOpIdFingerprint))
        return;
    const auto* array = llvm::dyn_cast<llvm::ConstantArray>(contents->getOperand(1));
    if (!array || array->getNumOperands() != functions_.size()) return;
    for (size_t i = 0; i < functions_.size(); ++i) {
        const auto* fn = llvm::dyn_cast<llvm::Function>(array->getOperand(i)->stripPointerCasts());
        if (!fn || fn->isDeclaration()) return;
        functions_[i] = fn;
        kernels_.insert(fn);
    }
    table_ = table;
}

const llvm::Function* LLVMOperandTemplates::Get(block_recipe::OpId id, bool exact) const {
    const auto index = static_cast<size_t>(id);
    return index < block_recipe::kOpIdCount ? functions_[2 * index + exact] : nullptr;
}

bool LLVMOperandTemplates::IsTemplateGlobal(const llvm::GlobalValue* value) const {
    return value == table_ || kernels_.contains(value);
}

std::unique_ptr<llvm::Module> PrepareLLVMBlock(const LLVMOperandTemplates& templates, block_recipe::BlockKeyView key,
                                               llvm::StringRef entry_name, bool prune_dead_globals,
                                               llvm::StringRef custom_pipeline) {
    if (!templates.IsValid()) return {};
    const bool exact = key.IsExact();
    llvm::SmallPtrSet<const llvm::GlobalValue*, 32> kernels;
    for (auto id : key.operations) {
        const auto* kernel = templates.Get(id, exact);
        if (!kernel) return {};
        kernels.insert(kernel);
    }
    llvm::ValueToValueMapTy values;
    auto module = llvm::CloneModule(templates.Module(), values, [&](const llvm::GlobalValue* def) {
        return kernels.contains(def) || !templates.IsTemplateGlobal(def);
    });
    auto* entry = module->getFunction("FiberLLVMEntry");
    const auto* placeholder = module->getFunction("FiberLLVMPlaceholder");
    const auto* use_step = module->getFunction("FiberLLVMUseStep");
    const auto* exit_kind = module->getFunction("FiberLLVMExit");
    if (!entry || !placeholder || !use_step || !exit_kind) return {};

    std::vector<llvm::CallInst*> guards;
    llvm::CallInst* exit_site = nullptr;
    for (auto& inst : llvm::instructions(*entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (call && call->getCalledFunction() == use_step) guards.push_back(call);
        if (call && call->getCalledFunction() == exit_kind) exit_site = call;
    }
    if (guards.size() != block_recipe::kMaxOps || !exit_site) return {};
    exit_site->replaceAllUsesWith(llvm::ConstantInt::get(exit_site->getType(), static_cast<uint8_t>(key.exit)));
    exit_site->eraseFromParent();
    for (auto* guard : guards) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(guard->getArgOperand(0));
        if (!index) return {};
        guard->replaceAllUsesWith(
            llvm::ConstantInt::get(guard->getType(), index->getZExtValue() < key.operations.size()));
        guard->eraseFromParent();
    }
    llvm::removeUnreachableBlocks(*entry);

    std::vector<llvm::CallInst*> sites;
    for (auto& inst : llvm::instructions(*entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (call && call->getCalledFunction() == placeholder) sites.push_back(call);
    }
    if (sites.size() != key.operations.size()) return {};
    llvm::SmallBitVector slots(key.operations.size());
    for (auto* call : sites) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(0));
        if (!index || index->getZExtValue() >= key.operations.size() || slots.test(index->getZExtValue())) return {};
        slots.set(index->getZExtValue());
        const auto slot = index->getZExtValue();
        const auto* source = templates.Get(key.operations[slot], exact);
        auto* target = llvm::dyn_cast_or_null<llvm::Function>(values.lookup(source));
        if (!target || !InlineLLVMKernel(*call, *target, exact ? &key.operands[slot] : nullptr)) return {};
    }

    entry->setName(entry_name);
    for (auto& function : *module) {
        if (&function != entry && !function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    if (!OptimizeLLVMModule(*module, prune_dead_globals, custom_pipeline) || llvm::verifyModule(*module)) return {};
    for (auto& inst : llvm::instructions(*entry)) {
        const auto* call = llvm::dyn_cast<llvm::CallBase>(&inst);
        const auto* fn = call ? call->getCalledFunction() : nullptr;
        if (fn && fn->getName().starts_with("FiberLLVM")) return {};
    }
    return module;
}

}  // namespace fiberish
