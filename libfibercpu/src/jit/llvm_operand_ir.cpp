#include "jit/llvm_operand_ir.h"

#include <vector>

#include <llvm/ADT/SmallBitVector.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <llvm/ExecutionEngine/Orc/JITTargetMachineBuilder.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/TargetSelect.h>
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

void OptimizeLLVMModule(llvm::Module& module) {
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
    auto pipeline = passes.buildPerModuleDefaultPipeline(llvm::OptimizationLevel::O2);
    pipeline.run(module, modules);
}

}  // namespace

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

std::unique_ptr<llvm::Module> PrepareLLVMBlock(const llvm::Module& templates, std::span<const block_recipe::Step> steps,
                                               bool exact, LLVMBlockExit exit, llvm::StringRef entry_name) {
    const std::string prefix = exact ? "FiberLLVMTemplate_" : "FiberLLVMGeneric_";
    const auto template_name = [&](const block_recipe::Step& step) { return prefix + std::string(step.name); };
    llvm::SmallPtrSet<const llvm::GlobalValue*, 32> kernels;
    for (const auto& step : steps) {
        const auto* kernel = templates.getFunction(template_name(step));
        if (!kernel) return {};
        kernels.insert(kernel);
    }
    llvm::ValueToValueMapTy values;
    auto module = llvm::CloneModule(templates, values, [&](const llvm::GlobalValue* def) {
        const auto name = def->getName();
        return (!name.starts_with("FiberLLVMTemplate_") && !name.starts_with("FiberLLVMGeneric_")) ||
               kernels.contains(def);
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
    exit_site->replaceAllUsesWith(llvm::ConstantInt::get(exit_site->getType(), static_cast<uint8_t>(exit)));
    exit_site->eraseFromParent();
    for (auto* guard : guards) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(guard->getArgOperand(0));
        if (!index) return {};
        guard->replaceAllUsesWith(llvm::ConstantInt::get(guard->getType(), index->getZExtValue() < steps.size()));
        guard->eraseFromParent();
    }
    llvm::removeUnreachableBlocks(*entry);

    std::vector<llvm::CallInst*> sites;
    for (auto& inst : llvm::instructions(*entry)) {
        auto* call = llvm::dyn_cast<llvm::CallInst>(&inst);
        if (call && call->getCalledFunction() == placeholder) sites.push_back(call);
    }
    if (sites.size() != steps.size()) return {};
    llvm::SmallBitVector slots(steps.size());
    for (auto* call : sites) {
        const auto* index = llvm::dyn_cast<llvm::ConstantInt>(call->getArgOperand(0));
        if (!index || index->getZExtValue() >= steps.size() || slots.test(index->getZExtValue())) return {};
        slots.set(index->getZExtValue());
        const auto& step = steps[index->getZExtValue()];
        auto* target = module->getFunction(template_name(step));
        if (!target || !InlineLLVMKernel(*call, *target, exact ? &step.shape : nullptr)) return {};
    }

    entry->setName(entry_name);
    for (auto& function : *module) {
        if (&function != entry && !function.isDeclaration()) function.setLinkage(llvm::GlobalValue::InternalLinkage);
    }
    OptimizeLLVMModule(*module);
    if (llvm::verifyModule(*module)) return {};
    for (auto& inst : llvm::instructions(*entry)) {
        const auto* call = llvm::dyn_cast<llvm::CallBase>(&inst);
        const auto* fn = call ? call->getCalledFunction() : nullptr;
        if (fn && fn->getName().starts_with("FiberLLVM")) return {};
    }
    return module;
}

}  // namespace fiberish
