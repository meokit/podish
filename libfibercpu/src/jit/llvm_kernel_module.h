#pragma once

#include <memory>

#include <llvm/ADT/ArrayRef.h>
#include <llvm/ADT/SmallVector.h>
#include <llvm/IR/Module.h>
#include <llvm/Transforms/Utils/Cloning.h>

namespace fiberish {

// Copy only the selected kernels and the globals they actually reference.
// Even the declaration shells for other kernels grow with the operation
// catalog, while most recipes need only a few entries.
class KernelModule final : private llvm::ValueMaterializer {
    std::unique_ptr<llvm::Module> module_;
    llvm::ValueToValueMapTy mappings_;
    llvm::SmallVector<const llvm::GlobalValue*, 16> pending_;

    llvm::Value* materialize(llvm::Value* value) override {
        if (const auto* function = llvm::dyn_cast<llvm::Function>(value)) {
            auto* copy = llvm::Function::Create(function->getFunctionType(), function->getLinkage(),
                                                function->getAddressSpace(), function->getName(), module_.get());
            copy->copyAttributesFrom(function);
            if (!function->isDeclaration()) pending_.push_back(function);
            return copy;
        }
        if (const auto* global = llvm::dyn_cast<llvm::GlobalVariable>(value)) {
            auto* copy =
                new llvm::GlobalVariable(*module_, global->getValueType(), global->isConstant(), global->getLinkage(),
                                         nullptr, global->getName(), nullptr, global->getThreadLocalMode(),
                                         global->getAddressSpace(), global->isExternallyInitialized());
            copy->copyAttributesFrom(global);
            pending_.push_back(global);
            return copy;
        }
        return nullptr;
    }

public:
    std::unique_ptr<llvm::Module> Clone(const llvm::Module& source, llvm::ArrayRef<const llvm::GlobalValue*> roots) && {
        // Module-level constructs can reference symbols outside the explicit roots.
        if (!source.getModuleInlineAsm().empty() || !source.alias_empty() || !source.ifunc_empty() ||
            source.getNamedGlobal("llvm.global_ctors") || source.getNamedGlobal("llvm.global_dtors") ||
            source.getNamedGlobal("llvm.used") || source.getNamedGlobal("llvm.compiler.used"))
            return llvm::CloneModule(source);

        module_ = std::make_unique<llvm::Module>(source.getModuleIdentifier(), source.getContext());
        module_->setDataLayout(source.getDataLayout());
        module_->setTargetTriple(source.getTargetTriple());
        module_->setSourceFileName(source.getSourceFileName());
        for (const auto* root : roots) llvm::MapValue(root, mappings_, llvm::RF_None, nullptr, this);
        for (const auto& named : source.named_metadata()) {
            auto* copy = module_->getOrInsertNamedMetadata(named.getName());
            for (const auto* node : named.operands())
                copy->addOperand(llvm::MapMetadata(node, mappings_, llvm::RF_None, nullptr, this));
        }

        // Mapping each definition may append further definitions to the queue.
        for (size_t i = 0; i < pending_.size(); ++i) {
            if (const auto* function = llvm::dyn_cast<llvm::Function>(pending_[i])) {
                auto* copy = llvm::cast<llvm::Function>(mappings_.lookup(function));
                auto arg = copy->arg_begin();
                for (const auto& old_arg : function->args()) {
                    arg->setName(old_arg.getName());
                    mappings_[&old_arg] = &*arg++;
                }
                llvm::SmallVector<llvm::ReturnInst*, 4> returns;
                llvm::CloneFunctionInto(copy, function, mappings_, llvm::CloneFunctionChangeType::ClonedModule, returns,
                                        "", nullptr, nullptr, this);
            } else {
                const auto* global = llvm::cast<llvm::GlobalVariable>(pending_[i]);
                auto* copy = llvm::cast<llvm::GlobalVariable>(mappings_.lookup(global));
                llvm::ValueMapper mapper(mappings_, llvm::RF_None, nullptr, this);
                if (global->hasInitializer()) copy->setInitializer(mapper.mapConstant(*global->getInitializer()));
                copy->copyMetadata(global, 0);
                mapper.remapGlobalObjectMetadata(*copy);
            }
        }
        return std::move(module_);
    }
};

}  // namespace fiberish
