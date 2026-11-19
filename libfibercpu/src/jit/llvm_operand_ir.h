#pragma once

#include <llvm/IR/Constants.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Transforms/Utils/Cloning.h>

#include "operand_policy.h"

namespace fiberish {

inline bool InlineLLVMKernel(llvm::CallInst& call, llvm::Function& kernel, const DecodedOperandShape* shape) {
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

inline void OptimizeLLVMModule(llvm::Module& module) {
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

}  // namespace fiberish
