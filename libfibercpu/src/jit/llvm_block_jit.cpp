#include "jit/llvm_block_jit.h"

#include <ankerl/unordered_dense.h>
#include <llvm/ADT/SmallBitVector.h>
#include <llvm/ADT/SmallPtrSet.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <utility>
#include <vector>

#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/ExecutionEngine/JITLink/JITLinkMemoryManager.h>
#include <llvm/ExecutionEngine/Orc/LLJIT.h>
#include <llvm/ExecutionEngine/Orc/SelfExecutorProcessControl.h>
#include <llvm/IR/Constants.h>
#include <llvm/IR/InstIterator.h>
#include <llvm/IR/Instructions.h>
#include <llvm/IR/Verifier.h>
#include <llvm/Passes/PassBuilder.h>
#include <llvm/Support/Error.h>
#include <llvm/Support/Process.h>
#include <llvm/Support/TargetSelect.h>
#include <llvm/Support/raw_ostream.h>
#include <llvm/Transforms/Utils/Cloning.h>
#include <llvm/Transforms/Utils/Local.h>

#include <pthread.h>

#include "operand_policy.h"
#include "ops.h"
#include "ops/ops_alu_impl.h"
#include "ops/ops_compare_impl.h"
#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_groups_impl.h"

namespace fiberish {
namespace {

#include "llvm_block_bitcode.inc"

enum class JitMode { Off, Generic, Exact };
constexpr size_t kMaxBlockOps = 64;
constexpr size_t kDefaultJitCodeBudget = 16 * 1024 * 1024;
constexpr uint16_t kDefaultHotThreshold = 64;
std::atomic<uint64_t> live_blocks{0};

}  // namespace

struct LLVMBlockCode {
    HandlerFunc handler;
    size_t mapped_bytes;
    // The ORC session must outlive the resource and any cache that holds it.
    std::shared_ptr<llvm::orc::LLJIT> engine;
    llvm::orc::ResourceTrackerSP tracker;

    LLVMBlockCode(HandlerFunc fn, size_t bytes, std::shared_ptr<llvm::orc::LLJIT> jit,
                  llvm::orc::ResourceTrackerSP resource)
        : handler(fn), mapped_bytes(bytes), engine(std::move(jit)), tracker(std::move(resource)) {
        live_blocks.fetch_add(1, std::memory_order_relaxed);
    }
    ~LLVMBlockCode() {
        llvm::consumeError(tracker->remove());
        live_blocks.fetch_sub(1, std::memory_order_relaxed);
    }
};

namespace {

JitMode RuntimeMode() {
    static const auto mode = [] {
        const char* value = std::getenv("FIBERCPU_LLVM_JIT");
        if (!value || !pthread_jit_write_protect_supported_np()) return JitMode::Off;
        if (std::strcmp(value, "1") == 0) return JitMode::Exact;
        if (std::strcmp(value, "generic") == 0) return JitMode::Generic;
        return JitMode::Off;
    }();
    return mode;
}

template <class T>
T ReadPositiveSetting(const char* name, T fallback) {
    const char* value = std::getenv(name);
    if (!value) return fallback;
    const char* end = value + std::strlen(value);
    T result{};
    const auto parsed = std::from_chars(value, end, result);
    return parsed.ec == std::errc{} && parsed.ptr == end && result != 0 ? result : fallback;
}

const char* LogicName(HandlerFunc handler) {
    static const ankerl::unordered_dense::map<HandlerFunc, const char*> names = {
#define FIBERCPU_LLVM_OP(Name, Exact) {DispatchWrapper<op::Name>, #Name},
#include "llvm_block_operations.inc"
#undef FIBERCPU_LLVM_OP
    };
    const auto match = names.find(handler);
    return match == names.end() ? nullptr : match->second;
}

struct Step {
    const char* name;
    DecodedOperandShape shape;
};

struct CodeBudgetExceeded : llvm::ErrorInfo<CodeBudgetExceeded> {
    static inline char ID = 0;
    void log(llvm::raw_ostream& out) const override { out << "LLVM block code budget exceeded"; }
    std::error_code convertToErrorCode() const override { return llvm::inconvertibleErrorCode(); }
};

class BlockMemoryManager final : public llvm::jitlink::InProcessMemoryManager {
    const size_t page_size_;

public:
    // LLJIT links synchronously, with BlockCompiler's mutex held.
    size_t mapped_bytes = 0;
    size_t blocked_bytes = 0;
    size_t budget = std::numeric_limits<size_t>::max();

    explicit BlockMemoryManager(size_t page_size) : InProcessMemoryManager(page_size), page_size_(page_size) {}

    size_t PageSize() const { return page_size_; }

    void allocate(const llvm::jitlink::JITLinkDylib* dylib, llvm::jitlink::LinkGraph& graph,
                  OnAllocatedFunction on_allocated) override {
        llvm::jitlink::BasicLayout layout(graph);
        auto sizes = layout.getContiguousPageBasedLayoutSizes(page_size_);
        if (!sizes) {
            on_allocated(sizes.takeError());
            return;
        }
        if (sizes->StandardSegs > budget - mapped_bytes) {
            blocked_bytes = mapped_bytes + sizes->StandardSegs;
            on_allocated(llvm::make_error<CodeBudgetExceeded>());
            return;
        }
        mapped_bytes += sizes->StandardSegs;
        InProcessMemoryManager::allocate(dylib, graph, std::move(on_allocated));
    }
};

struct BlockLookup {
    std::shared_ptr<const LLVMBlockCode> code;
    size_t blocked_bytes = 0;
};

void Optimize(llvm::Module& module) {
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

class BlockCompiler {
    llvm::orc::ThreadSafeContext context_{std::make_unique<llvm::LLVMContext>()};
    std::unique_ptr<llvm::Module> ir_;
    std::shared_ptr<llvm::orc::LLJIT> jit_;
    BlockMemoryManager* memory_ = nullptr;
    std::mutex mutex_;
    ankerl::unordered_dense::map<std::string, std::weak_ptr<const LLVMBlockCode>> compiled_;
    uint64_t next_symbol_ = 0;
    std::atomic<uint64_t> successes_{0};

    BlockLookup Compile(std::span<const Step> steps, bool exact, LLVMBlockExit exit, size_t budget) {
        const std::string prefix = exact ? "FiberLLVMTemplate_" : "FiberLLVMGeneric_";
        llvm::SmallPtrSet<const llvm::GlobalValue*, 32> kernels;
        for (const auto& step : steps) {
            const auto* kernel = ir_->getFunction(prefix + step.name);
            if (!kernel) return {};
            kernels.insert(kernel);
        }
        llvm::ValueToValueMapTy values;
        auto module = llvm::CloneModule(*ir_, values, [&](const llvm::GlobalValue* def) {
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
        if (guards.size() != kMaxBlockOps || !exit_site) return {};
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
            auto* target = module->getFunction(prefix + step.name);
            if (!target || target->isDeclaration() || target->getFunctionType() != call->getFunctionType()) return {};
            call->setCalledFunction(target);
            if (exact) {
                // The template ABI is step index, decoded fields, then LogicFuncParams.
                const auto bind = [&](unsigned arg, uint32_t value) {
                    call->setArgOperand(arg, llvm::ConstantInt::get(call->getArgOperand(arg)->getType(), value));
                };
                bind(1, step.shape.encoding);
                bind(2, step.shape.ea_desc);
                bind(3, step.shape.displacement);
                bind(7, step.shape.immediate);
            }
            llvm::InlineFunctionInfo info;
            if (!llvm::InlineFunction(*call, info).isSuccess()) return {};
        }

        const auto name = "FiberJitBlock_" + std::to_string(next_symbol_++);
        entry->setName(name);
        for (auto& function : *module) {
            if (&function != entry && !function.isDeclaration())
                function.setLinkage(llvm::GlobalValue::InternalLinkage);
        }
        Optimize(*module);
        if (llvm::verifyModule(*module)) return {};
        for (auto& inst : llvm::instructions(*entry)) {
            const auto* call = llvm::dyn_cast<llvm::CallBase>(&inst);
            const auto* fn = call ? call->getCalledFunction() : nullptr;
            if (fn && fn->getName().starts_with("FiberLLVM")) return {};
        }

        auto tracker = jit_->getMainJITDylib().createResourceTracker();
        if (auto error = jit_->addIRModule(tracker, {std::move(module), context_})) {
            llvm::consumeError(std::move(error));
            llvm::consumeError(tracker->remove());
            return {};
        }
        memory_->mapped_bytes = 0;
        memory_->blocked_bytes = 0;
        memory_->budget = budget;
        auto code = jit_->lookup(name);
        memory_->budget = std::numeric_limits<size_t>::max();
        if (!code) {
            llvm::consumeError(code.takeError());
            llvm::consumeError(tracker->remove());
            return {nullptr, memory_->blocked_bytes};
        }
        return {std::make_shared<LLVMBlockCode>(code->toPtr<HandlerFunc>(), memory_->mapped_bytes, jit_,
                                                std::move(tracker))};
    }

public:
    BlockCompiler() {
        if (llvm::InitializeNativeTarget() || llvm::InitializeNativeTargetAsmPrinter()) return;
        auto input = llvm::MemoryBufferRef(llvm::StringRef(reinterpret_cast<const char*>(kBitcode), sizeof(kBitcode)),
                                           "fibercpu-blocks");
        auto parsed =
            context_.withContextDo([&](llvm::LLVMContext* context) { return llvm::parseBitcodeFile(input, *context); });
        if (!parsed) {
            llvm::consumeError(parsed.takeError());
            return;
        }
        auto page_size = llvm::sys::Process::getPageSize();
        if (!page_size) {
            llvm::consumeError(page_size.takeError());
            return;
        }
        auto memory = std::make_unique<BlockMemoryManager>(*page_size);
        auto* memory_ptr = memory.get();
        auto executor = llvm::orc::SelfExecutorProcessControl::Create(nullptr, nullptr, std::move(memory));
        if (!executor) {
            llvm::consumeError(executor.takeError());
            return;
        }
        auto engine = llvm::orc::LLJITBuilder().setExecutorProcessControl(std::move(*executor)).create();
        if (!engine) {
            llvm::consumeError(engine.takeError());
            return;
        }
        (*engine)->getExecutionSession().setErrorReporter([](llvm::Error error) {
            error = llvm::handleErrors(std::move(error), [](const CodeBudgetExceeded&) {});
            if (error) llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "JIT session error: ");
        });
        llvm::orc::MangleAndInterner mangle((*engine)->getExecutionSession(), (*engine)->getDataLayout());
        llvm::orc::SymbolMap slow_paths;
        const std::pair<llvm::StringRef, HandlerFunc> helpers[] = {
            {"_ZN8fiberish15MemoryOpRestartE", MemoryOpRestart},
            {"_ZN8fiberish13MemoryOpRetryE", MemoryOpRetry},
            {"_ZN8fiberish27ResolveBranchTargetSlowLinkE", ResolveBranchTargetSlowLink},
            {"_ZN8fiberish34ResolveBranchTargetSlowControlFlowE", ResolveBranchTargetSlowControlFlow},
        };
        for (const auto& function : **parsed) {
            if (!function.isDeclaration()) continue;
            for (const auto& [prefix, target] : helpers) {
                if (!function.getName().starts_with(prefix)) continue;
                slow_paths[mangle(function.getName())] = {
                    llvm::orc::ExecutorAddr::fromPtr(target),
                    llvm::JITSymbolFlags::Exported | llvm::JITSymbolFlags::Callable};
            }
        }
        if (auto error = (*engine)->getMainJITDylib().define(llvm::orc::absoluteSymbols(std::move(slow_paths)))) {
            llvm::consumeError(std::move(error));
            return;
        }
        jit_ = std::move(*engine);
        memory_ = memory_ptr;
        ir_ = std::move(*parsed);
    }

    BlockLookup Find(std::span<const Step> steps, bool exact, LLVMBlockExit exit, size_t budget) {
        if (!jit_) return {};
        std::string key(1, static_cast<char>(exit));
        for (const auto& step : steps) {
            key += step.name;
            key += '\0';
            if (exact) {
                key.append(reinterpret_cast<const char*>(&step.shape.encoding), sizeof(step.shape.encoding));
                key.append(reinterpret_cast<const char*>(&step.shape.ea_desc), sizeof(step.shape.ea_desc));
                key.append(reinterpret_cast<const char*>(&step.shape.displacement), sizeof(step.shape.displacement));
                key.append(reinterpret_cast<const char*>(&step.shape.immediate), sizeof(step.shape.immediate));
            }
        }
        std::lock_guard lock(mutex_);
        const auto found = compiled_.find(key);
        if (found != compiled_.end()) {
            if (auto cached = found->second.lock()) return {std::move(cached)};
            compiled_.erase(found);
        }
        if (budget < memory_->PageSize()) return {nullptr, memory_->PageSize()};
        if (compiled_.size() > live_blocks.load(std::memory_order_relaxed) + 128)
            std::erase_if(compiled_, [](const auto& entry) { return entry.second.expired(); });
        auto result = Compile(steps, exact, exit, budget);
        if (result.code) {
            compiled_.emplace(std::move(key), result.code);
            successes_.fetch_add(1, std::memory_order_relaxed);
        }
        return result;
    }

    uint64_t Count() const { return successes_.load(std::memory_order_relaxed); }
};

BlockCompiler& Compiler() {
    static BlockCompiler compiler;
    return compiler;
}

}  // namespace

uint16_t LLVMJitHotThreshold() {
    static const uint16_t threshold =
        RuntimeMode() == JitMode::Off ? 0
                                      : ReadPositiveSetting("FIBERCPU_LLVM_JIT_HOT_THRESHOLD", kDefaultHotThreshold);
    return threshold;
}

void CompileHotLLVMBlock(EmuState* state, BasicBlock* block) {
    auto& mmu = state->mmu;
    if (mmu.code_cache_flush_pending() || !block->is_valid() || !block->has_unfused_handlers ||
        block->entry != block->FirstOp()->handler || mmu.lookup_cached_block(block->start_eip()) != block)
        return;

    const auto mode = RuntimeMode();
    if (mode == JitMode::Off) return;
    auto* ops = block->FirstOp();
    std::array<Step, kMaxBlockOps> steps;
    std::array<HandlerFunc, kMaxBlockOps> replay_handlers;
    size_t covered = 0;
    for (; covered < std::min<size_t>(block->sentinel_slot_index, steps.size()); ++covered) {
        const auto* op = ops + covered;
        if (!op->len || op->prefixes.flags.lock || (HasMem(op) && GetExtKind(op) != ExtKind::Data)) break;
        DecodedOp generic;
        if (!RedecodeGenericInstruction(state, *op, generic)) return;
        const auto* name = LogicName(generic.handler);
        if (!name) break;
        steps[covered] = {name, DecodedOperandShape::Read(&generic)};
        replay_handlers[covered] = generic.handler;
    }
    auto exit = LLVMBlockExit::Dispatch;
    if (covered == block->sentinel_slot_index) {
        auto handler = block->Sentinel()->handler;
        if (std::ranges::find(g_ExitHandlersFallthrough, handler) != g_ExitHandlersFallthrough.end()) {
            exit = LLVMBlockExit::Fallthrough;
        } else if (std::ranges::find(g_ExitHandlersBranch, handler) != g_ExitHandlersBranch.end()) {
            exit = LLVMBlockExit::Branch;
        }
    }
    if (covered == 0 || (covered == 1 && exit == LLVMBlockExit::Dispatch)) return;

    auto& cache = mmu.code_cache();
    static const size_t budget = ReadPositiveSetting("FIBERCPU_LLVM_JIT_CODE_BUDGET", kDefaultJitCodeBudget);
    const size_t remaining = budget - cache.llvm_block_bytes;
    auto [code, blocked_bytes] = Compiler().Find({steps.data(), covered}, mode == JitMode::Exact, exit, remaining);
    const bool already_owned = code && cache.llvm_blocks.contains(code);
    const size_t bytes = code ? code->mapped_bytes : blocked_bytes;
    if (!already_owned && bytes > remaining) {
        if (bytes <= budget) {
            cache.pending_flush = true;
            cache.pending_budget_flush = true;
        }
        return;
    }
    if (!code) return;
    if (!already_owned) {
        cache.llvm_blocks.insert(code);
        cache.llvm_block_bytes += bytes;
    }

    // Replay a slow load on its original scalar slot. The uncompiled suffix
    // keeps its independently selected SuperOpcodes and operand handlers.
    for (size_t i = 0; i < covered; ++i) {
        ops[i].handler = replay_handlers[i];
        ops[i].meta.flags.operand_lowered = 0;
    }
    block->entry = code->handler;
}

uint64_t LLVMJitBlockCount() { return RuntimeMode() == JitMode::Off ? 0 : Compiler().Count(); }

uint64_t LLVMJitLiveBlockCount() { return live_blocks.load(std::memory_order_relaxed); }

}  // namespace fiberish
