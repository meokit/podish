#include "jit/llvm_block_jit.h"
#include "jit/block_recipe.h"
#include "jit/linked_block_cache_host.h"
#include "jit/llvm_block_cache.h"
#include "jit/llvm_block_memory.h"
#include "jit/llvm_operand_ir.h"
#include "linked_block_cache_abi.h"

#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <atomic>
#include <charconv>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <utility>
#include <vector>

#include <llvm/Bitcode/BitcodeReader.h>
#include <llvm/Config/llvm-config.h>
#include <llvm/ExecutionEngine/Orc/CompileUtils.h>
#include <llvm/ExecutionEngine/Orc/IRCompileLayer.h>
#include <llvm/ExecutionEngine/Orc/LLJIT.h>
#include <llvm/ExecutionEngine/Orc/SelfExecutorProcessControl.h>
#include <llvm/Support/Error.h>
#include <llvm/Support/FileSystem.h>
#include <llvm/Support/Process.h>
#include <llvm/Support/SHA256.h>
#include <llvm/Support/raw_ostream.h>
#include <llvm/TargetParser/Host.h>

#include <pthread.h>
#include <strings.h>

#include "float80.h"
#include "ops.h"
#include "ops/ops_control_impl.h"

namespace fiberish {
namespace {

#include "llvm_block_bitcode.inc"

enum class JitMode { Off, Generic, Exact, Profile };
using block_recipe::kMaxOps;
using block_recipe::Step;
constexpr size_t kDefaultJitCodeBudget = 16 * 1024 * 1024;
constexpr uint16_t kDefaultHotThreshold = 64;
std::atomic<uint64_t> live_blocks{0};
std::atomic<uint64_t> materializations{0};

}  // namespace

struct LLVMBlockCode {
    HandlerFunc handler;
    size_t allocated_bytes;
    // The ORC session must outlive the resource and any cache that holds it.
    std::shared_ptr<llvm::orc::LLJIT> engine;
    llvm::orc::ResourceTrackerSP tracker;

    LLVMBlockCode(HandlerFunc fn, size_t bytes, std::shared_ptr<llvm::orc::LLJIT> jit,
                  llvm::orc::ResourceTrackerSP resource)
        : handler(fn), allocated_bytes(bytes), engine(std::move(jit)), tracker(std::move(resource)) {
        live_blocks.fetch_add(1, std::memory_order_relaxed);
    }
    ~LLVMBlockCode() {
        llvm::consumeError(tracker->remove());
        live_blocks.fetch_sub(1, std::memory_order_relaxed);
    }
};

namespace {

const auto& ProfileFingerprint() {
    static const auto digest = [] {
        llvm::SHA256 hash;
        hash.update(llvm::ArrayRef<uint8_t>(kBitcode, sizeof(kBitcode)));
        hash.update(LLVM_VERSION_STRING);
        hash.update(llvm::sys::getProcessTriple());
        hash.update(llvm::sys::getHostCPUName());
        const auto cpu_features = llvm::sys::getHostCPUFeatures();
        std::vector<std::pair<llvm::StringRef, bool>> sorted;
        for (const auto& feature : cpu_features) sorted.emplace_back(feature.getKey(), feature.getValue());
        std::sort(sorted.begin(), sorted.end());
        for (const auto& [feature, enabled] : sorted) {
            hash.update(enabled ? "+" : "-");
            hash.update(feature);
        }
        return hash.final();
    }();
    return digest;
}

void WriteProfileHeader(llvm::raw_ostream& out) {
    const auto magic = llvm_block_cache::profile_magic;
    out.write(magic.data(), magic.size());
    const auto& fingerprint = ProfileFingerprint();
    out.write(reinterpret_cast<const char*>(fingerprint.data()), fingerprint.size());
}

bool ReadProfileHeader(std::string_view& bytes) {
    const auto magic = llvm_block_cache::profile_magic;
    if (!bytes.starts_with(magic)) return false;
    bytes.remove_prefix(magic.size());
    const auto& fingerprint = ProfileFingerprint();
    if (bytes.size() < fingerprint.size() || std::memcmp(bytes.data(), fingerprint.data(), fingerprint.size()) != 0)
        return false;
    bytes.remove_prefix(fingerprint.size());
    return true;
}

void RecordBlock(const std::string& key) {
    static auto output = []() -> std::unique_ptr<llvm::raw_fd_ostream> {
        const auto* path = std::getenv("FIBERCPU_LLVM_JIT_PROFILE");
        if (!path || !*path) return {};
        std::error_code error;
        auto file = std::make_unique<llvm::raw_fd_ostream>(path, error, llvm::sys::fs::OF_None);
        if (error) return {};
        WriteProfileHeader(*file);
        return file;
    }();
    if (!output) return;
    static std::mutex mutex;
    static ankerl::unordered_dense::set<std::string> seen;
    std::lock_guard lock(mutex);
    if (seen.insert(key).second) {
        llvm_block_cache::WriteRecord(*output, key);
        output->flush();
        if (output->has_error()) {
            llvm::errs() << "Failed writing LLVM block profile: " << output->error().message() << '\n';
            output->clear_error();
        }
    }
}

JitMode RuntimeMode() {
    static const auto mode = [] {
        const char* value = std::getenv("FIBERCPU_LLVM_JIT");
        if (!value || !pthread_jit_write_protect_supported_np()) return JitMode::Off;
        if (std::strcmp(value, "1") == 0) return JitMode::Exact;
        if (std::strcmp(value, "generic") == 0) return JitMode::Generic;
        if (std::strcmp(value, "profile") == 0 && std::getenv("FIBERCPU_LLVM_JIT_PROFILE")) return JitMode::Profile;
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

bool DecodeRecipe(std::string_view key, std::vector<Step>& steps, LLVMBlockExit& exit) {
    if (key.empty() || static_cast<uint8_t>(key.front()) > static_cast<uint8_t>(LLVMBlockExit::Branch)) return false;
    exit = static_cast<LLVMBlockExit>(key.front());
    key.remove_prefix(1);
    while (!key.empty()) {
        const auto end = key.find('\0');
        if (end == 0 || end == std::string_view::npos || steps.size() == kMaxOps) return false;
        const auto name = key.substr(0, end);
        key.remove_prefix(end + 1);
        DecodedOperandShape shape;
        if (!llvm_block_cache::ReadU32(key, shape.encoding) || !llvm_block_cache::ReadU32(key, shape.ea_desc) ||
            !llvm_block_cache::ReadU32(key, shape.displacement) || !llvm_block_cache::ReadU32(key, shape.immediate))
            return false;
        steps.push_back({name, shape});
    }
    return !steps.empty();
}

std::string ObjectEntryName(uint64_t index) { return "FiberCachedBlock_" + std::to_string(index); }

struct BlockLookup {
    std::shared_ptr<const LLVMBlockCode> code;
    size_t blocked_bytes = 0;
};

std::unique_ptr<llvm::Module> ReadBlockIR(llvm::LLVMContext& context) {
    auto input = llvm::MemoryBufferRef(llvm::StringRef(reinterpret_cast<const char*>(kBitcode), sizeof(kBitcode)),
                                       "fibercpu-blocks");
    auto parsed = llvm::parseBitcodeFile(input, context);
    if (parsed) return std::move(*parsed);
    llvm::consumeError(parsed.takeError());
    return {};
}

struct BlockIR {
    llvm::LLVMContext context;
    std::unique_ptr<llvm::Module> module = ReadBlockIR(context);
};

class BlockCompiler {
    std::shared_ptr<llvm::orc::LLJIT> jit_;
    BlockMemoryManager* memory_ = nullptr;
    std::mutex mutex_;
    std::condition_variable compilation_done_;
    ankerl::unordered_dense::map<std::string, std::weak_ptr<const LLVMBlockCode>> compiled_;
    ankerl::unordered_dense::set<std::string> compiling_;
    uint64_t next_symbol_ = 0;

    BlockLookup Materialize(llvm::StringRef name, size_t budget, std::unique_ptr<llvm::MemoryBuffer> object) {
        auto tracker = jit_->getMainJITDylib().createResourceTracker();
        auto error = jit_->addObjectFile(tracker, std::move(object));
        if (error) {
            llvm::consumeError(std::move(error));
            llvm::consumeError(tracker->remove());
            return {};
        }
        memory_->allocated_bytes = 0;
        memory_->blocked_bytes = 0;
        memory_->budget = budget;
        auto code = jit_->lookup(name);
        memory_->budget = std::numeric_limits<size_t>::max();
        if (!code) {
            llvm::consumeError(code.takeError());
            llvm::consumeError(tracker->remove());
            return {nullptr, memory_->blocked_bytes};
        }
        return {std::make_shared<LLVMBlockCode>(code->toPtr<HandlerFunc>(), memory_->allocated_bytes, jit_,
                                                std::move(tracker))};
    }

public:
    BlockCompiler() {
        if (!InitializeLLVMTarget()) return;
        BlockIR ir;
        if (!ir.module) return;
        auto page_size = llvm::sys::Process::getPageSize();
        if (!page_size) {
            llvm::consumeError(page_size.takeError());
            return;
        }
        auto memory = std::make_unique<BlockMemoryManager>(*page_size);
        if (!memory->IsAvailable()) return;
        auto* memory_ptr = memory.get();
        auto executor = llvm::orc::SelfExecutorProcessControl::Create(nullptr, nullptr, std::move(memory));
        if (!executor) {
            llvm::consumeError(executor.takeError());
            return;
        }
        auto engine = llvm::orc::LLJITBuilder()
                          .setExecutorProcessControl(std::move(*executor))
                          .setCompileFunctionCreator(
                              [](llvm::orc::JITTargetMachineBuilder target)
                                  -> llvm::Expected<std::unique_ptr<llvm::orc::IRCompileLayer::IRCompiler>> {
                                  return std::make_unique<llvm::orc::ConcurrentIRCompiler>(std::move(target));
                              })
                          .create();
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
        const auto bind = [&](llvm::StringRef name, auto* target) {
            slow_paths[mangle(name)] = {llvm::orc::ExecutorAddr::fromPtr(target),
                                        llvm::JITSymbolFlags::Exported | llvm::JITSymbolFlags::Callable};
        };
        const std::pair<llvm::StringRef, HandlerFunc> helpers[] = {
            {"_ZN8fiberish15MemoryOpRestartE", MemoryOpRestart},
            {"_ZN8fiberish13MemoryOpRetryE", MemoryOpRetry},
            {"_ZN8fiberish27ResolveBranchTargetSlowLinkE", ResolveBranchTargetSlowLink},
            {"_ZN8fiberish34ResolveBranchTargetSlowControlFlowE", ResolveBranchTargetSlowControlFlow},
        };
        for (const auto& function : *ir.module) {
            if (!function.isDeclaration()) continue;
            for (const auto& [prefix, target] : helpers) {
                if (!function.getName().starts_with(prefix)) continue;
                bind(function.getName(), target);
            }
        }
        // x87 uses the same host floating-point ABI and thread-local library state as native dispatch.
#define FIBERCPU_BIND_JIT_HELPER(Name) bind(#Name, &Name)
        FIBERCPU_BIND_JIT_HELPER(f80_abs);
        FIBERCPU_BIND_JIT_HELPER(f80_add);
        FIBERCPU_BIND_JIT_HELPER(f80_atan2);
        FIBERCPU_BIND_JIT_HELPER(f80_cos);
        FIBERCPU_BIND_JIT_HELPER(f80_div);
        FIBERCPU_BIND_JIT_HELPER(f80_eq);
        FIBERCPU_BIND_JIT_HELPER(f80_from_double);
        FIBERCPU_BIND_JIT_HELPER(f80_from_int);
        FIBERCPU_BIND_JIT_HELPER(f80_is_signaling_nan);
        FIBERCPU_BIND_JIT_HELPER(f80_is_supported);
        FIBERCPU_BIND_JIT_HELPER(f80_isdenormal);
        FIBERCPU_BIND_JIT_HELPER(f80_isinf);
        FIBERCPU_BIND_JIT_HELPER(f80_isnan);
        FIBERCPU_BIND_JIT_HELPER(f80_iszero);
        FIBERCPU_BIND_JIT_HELPER(f80_log2);
        FIBERCPU_BIND_JIT_HELPER(f80_lt);
        FIBERCPU_BIND_JIT_HELPER(f80_mod);
        FIBERCPU_BIND_JIT_HELPER(f80_mul);
        FIBERCPU_BIND_JIT_HELPER(f80_neg);
        FIBERCPU_BIND_JIT_HELPER(f80_rem);
        FIBERCPU_BIND_JIT_HELPER(f80_round);
        FIBERCPU_BIND_JIT_HELPER(f80_scale);
        FIBERCPU_BIND_JIT_HELPER(f80_sin);
        FIBERCPU_BIND_JIT_HELPER(f80_sqrt);
        FIBERCPU_BIND_JIT_HELPER(f80_sub);
        FIBERCPU_BIND_JIT_HELPER(f80_sync_from_soft);
        FIBERCPU_BIND_JIT_HELPER(f80_sync_to_soft);
        FIBERCPU_BIND_JIT_HELPER(f80_tan);
        FIBERCPU_BIND_JIT_HELPER(f80_to_double);
        FIBERCPU_BIND_JIT_HELPER(f80_to_int16_checked);
        FIBERCPU_BIND_JIT_HELPER(f80_to_int32_checked);
        FIBERCPU_BIND_JIT_HELPER(f80_to_int64_checked);
        FIBERCPU_BIND_JIT_HELPER(f80_trunc_to_int);
        FIBERCPU_BIND_JIT_HELPER(f80_uncomparable);
        FIBERCPU_BIND_JIT_HELPER(f80_xtract);
#undef FIBERCPU_BIND_JIT_HELPER
        bind("frexp", static_cast<double (*)(double, int*)>(std::frexp));
        bind("exp2", static_cast<double (*)(double)>(std::exp2));
        bind("log2", static_cast<double (*)(double)>(std::log2));
        bind("pow", static_cast<double (*)(double, double)>(std::pow));
        bind("memcmp", &std::memcmp);
        bind("memcpy", &std::memcpy);
        bind("memmove", &std::memmove);
        bind("memset", &std::memset);
        bind("bzero", &::bzero);
        bind("_Znam", static_cast<void* (*)(size_t)>(::operator new[]));
        bind("FiberReadRealtimeTsc", &FiberReadRealtimeTsc);
        if (auto error = (*engine)->getMainJITDylib().define(llvm::orc::absoluteSymbols(std::move(slow_paths)))) {
            llvm::consumeError(std::move(error));
            return;
        }
        jit_ = std::move(*engine);
        memory_ = memory_ptr;
    }

    BlockLookup FindOrCompile(std::span<const Step> steps, std::string key, bool exact, LLVMBlockExit exit,
                              size_t budget) {
        if (!jit_) return {};
        std::unique_lock lock(mutex_);
        compilation_done_.wait(lock, [&] { return !compiling_.contains(key); });
        const auto found = compiled_.find(key);
        if (found != compiled_.end()) {
            if (auto code = found->second.lock()) return {std::move(code)};
            compiled_.erase(found);
        }
        if (budget < memory_->MinimumAllocationSize()) return {nullptr, memory_->MinimumAllocationSize()};
        if (compiled_.size() > live_blocks.load(std::memory_order_relaxed) + 128)
            std::erase_if(compiled_, [](const auto& entry) { return entry.second.expired(); });
        const auto name = "FiberJitBlock_" + std::to_string(next_symbol_++);
        compiling_.insert(key);
        lock.unlock();
        // Each caller owns its IR and target machine while optimizing and
        // emitting the object. Only ORC linking and budget tracking serialize.
        thread_local BlockIR worker_ir;
        std::unique_ptr<llvm::MemoryBuffer> object;
        if (worker_ir.module) {
            if (auto module = PrepareLLVMBlock(*worker_ir.module, steps, exact, exit, name)) {
                auto compiled = jit_->getIRCompileLayer().getCompiler()(*module);
                if (compiled)
                    object = std::move(*compiled);
                else
                    llvm::consumeError(compiled.takeError());
            }
        }
        lock.lock();
        compiling_.erase(key);
        compilation_done_.notify_all();
        if (!object) return {};
        auto result = Materialize(name, budget, std::move(object));
        if (result.code) {
            compiled_.emplace(std::move(key), result.code);
            materializations.fetch_add(1, std::memory_order_relaxed);
        }
        return result;
    }

    static int BuildLinkedCache(std::string_view profile, llvm::raw_ostream& output) {
        if (!InitializeLLVMTarget()) return -1;
        BlockIR ir;
        if (!ir.module) return -1;
        auto machine = CreateLLVMObjectTarget(*ir.module);
        if (!machine) {
            llvm::consumeError(machine.takeError());
            return -1;
        }
        llvm::orc::TMOwningSimpleCompiler compiler(std::move(*machine));
        output << llvm_block_cache::linked_magic << FIBERCPU_LINKED_BLOCK_CACHE_ABI;
        if (!llvm_block_cache::WriteRecord(output, LinkedBlockCacheHostCPU())) return -1;
        int compiled = 0;
        ankerl::unordered_dense::set<std::string_view> seen;
        std::vector<Step> steps;
        while (!profile.empty()) {
            std::string_view key;
            LLVMBlockExit exit;
            steps.clear();
            if (!llvm_block_cache::ReadRecord(profile, key) || !DecodeRecipe(key, steps, exit)) return -1;
            if (!seen.insert(key).second) continue;
            auto module = PrepareLLVMBlock(*ir.module, steps, true, exit, ObjectEntryName(compiled));
            if (!module) continue;
            auto object = compiler(*module);
            if (!object) {
                llvm::logAllUnhandledErrors(object.takeError(), llvm::errs(), "LLVM block cache: ");
                continue;
            }
            if (!llvm_block_cache::WriteRecord(output, key) ||
                !llvm_block_cache::WriteRecord(output, (*object)->getBuffer()))
                return -1;
            ++compiled;
        }
        return compiled;
    }
};

struct CompilerStorage {
    std::once_flag initialized;
    std::unique_ptr<BlockCompiler> compiler;
};

CompilerStorage& Storage() {
    static CompilerStorage storage;
    return storage;
}

BlockCompiler& Compiler() {
    auto& storage = Storage();
    std::call_once(storage.initialized, [&] { storage.compiler = std::make_unique<BlockCompiler>(); });
    return *storage.compiler;
}

constexpr size_t kMaxPendingPerCache = 32;
constexpr size_t kMaxQueued = 64;
constexpr uint16_t kRetryHotCount = 256;

struct AsyncResult {
    std::atomic<bool> done{false};
    BlockLookup lookup;
    std::vector<HandlerFunc> replay;
};

struct PendingBlock {
    std::shared_ptr<AsyncResult> result;
    BasicBlock* block;
    uint32_t eip;
    uint16_t revision;
};

class AsyncSignals {
    std::mutex mutex_;
    std::condition_variable idle_;
    size_t active_ = 0;
    bool closing_ = false;

public:
    std::atomic<bool> ready{false};

    bool Begin() {
        std::lock_guard lock(mutex_);
        if (closing_) return false;
        ++active_;
        return true;
    }

    void End() {
        std::lock_guard lock(mutex_);
        if (--active_ == 0) idle_.notify_one();
    }

    void Close() {
        std::unique_lock lock(mutex_);
        closing_ = true;
        idle_.wait(lock, [&] { return active_ == 0; });
    }
};

}  // namespace

struct LLVMAsyncState {
    std::shared_ptr<AsyncSignals> signals = std::make_shared<AsyncSignals>();
    std::vector<PendingBlock> pending;

    ~LLVMAsyncState() {
        // Prevent publication and skip queued work. A final MMU release must
        // also finish in-flight LLVM calls before returning to a host that
        // can immediately unload the native library or exit.
        pending.clear();
        signals->Close();
    }
};

void CancelPendingLLVMBlockCompilations(LLVMAsyncState& state) {
    // Queued jobs hold weak result pointers. The same MMU's shutdown token
    // stays live so an eventual destroy can drain old and new in-flight jobs.
    state.pending.clear();
}

namespace {

struct CompilationJob {
    std::vector<DecodedOp> ops;
    std::vector<uint8_t> bytes;
    std::weak_ptr<AsyncResult> result;
    std::weak_ptr<AsyncSignals> signals;
    size_t sentinel;
    size_t budget;
    LLVMBlockExit exit;
    bool exact;

    void Compile(AsyncSignals& control) {
        if (result.expired()) return;
        block_recipe::Plan plan;
        auto input = std::span<const uint8_t>(bytes);
        const auto redecode = [&](const DecodedOp& op, DecodedOp& generic) {
            const auto instruction = input.first(op.len);
            input = input.subspan(op.len);
            return RedecodeGenericInstruction(instruction, op, generic);
        };
        BlockLookup lookup;
        const bool planned = block_recipe::PlanBlock(ops, sentinel, exit, exact, redecode, plan);
        if (result.expired()) return;
        if (planned) {
            auto key = block_recipe::BlockKey(plan.Steps(), exact, plan.exit);
            lookup = Compiler().FindOrCompile(plan.Steps(), std::move(key), exact, plan.exit, budget);
        }
        if (auto output = result.lock()) {
            output->lookup = std::move(lookup);
            if (output->lookup.code) output->replay.assign(plan.ReplayHandlers().begin(), plan.ReplayHandlers().end());
            output->done.store(true, std::memory_order_release);
            control.ready.store(true, std::memory_order_release);
        }
    }

    void Run() {
        auto control = signals.lock();
        if (!control || !control->Begin()) return;
        Compile(*control);
        control->End();
    }
};

class CompilationPool {
    std::mutex mutex_;
    std::condition_variable queued_;
    std::deque<CompilationJob> jobs_;
    bool stopping_ = false;
    // Destroy these first so no worker can still touch the queue.
    std::vector<std::jthread> workers_;

    void Run() {
        for (;;) {
            CompilationJob job;
            {
                std::unique_lock lock(mutex_);
                queued_.wait(lock, [&] { return stopping_ || !jobs_.empty(); });
                if (stopping_) return;
                job = std::move(jobs_.front());
                jobs_.pop_front();
            }
            job.Run();
        }
    }

public:
    explicit CompilationPool(unsigned count) {
        workers_.reserve(count);
        for (unsigned i = 0; i < count; ++i) workers_.emplace_back([this] { Run(); });
    }

    ~CompilationPool() {
        {
            std::lock_guard lock(mutex_);
            stopping_ = true;
        }
        queued_.notify_all();
    }

    bool Submit(CompilationJob job) {
        {
            std::unique_lock lock(mutex_, std::try_to_lock);
            if (!lock.owns_lock()) return false;
            if (jobs_.size() == kMaxQueued) {
                std::erase_if(jobs_, [](const CompilationJob& queued) { return queued.result.expired(); });
                if (jobs_.size() == kMaxQueued) return false;
            }
            jobs_.push_back(std::move(job));
        }
        queued_.notify_one();
        return true;
    }
};

unsigned WorkerCount() {
    static const auto count = [] {
        const auto* value = std::getenv("FIBERCPU_LLVM_JIT_WORKERS");
        if (!value) return 2u;
        unsigned threads;
        const auto* end = value + std::strlen(value);
        const auto parsed = std::from_chars(value, end, threads);
        return parsed.ec == std::errc{} && parsed.ptr == end ? std::min(threads, 8u) : 2u;
    }();
    return count;
}

CompilationPool& Workers() {
    static CompilationPool pool([] {
        // These statics must outlive the pool. Compiler construction
        // itself stays lazy so the VM thread does not parse bitcode here.
        (void)Storage();
        block_recipe::InitializeOperationLookup();
        return WorkerCount();
    }());
    return pool;
}

size_t CodeBudget() {
    static const size_t budget = ReadPositiveSetting("FIBERCPU_LLVM_JIT_CODE_BUDGET", kDefaultJitCodeBudget);
    return budget;
}

void InstallResult(EmuState* state, BasicBlock* block, const BlockLookup& lookup, std::span<const HandlerFunc> replay) {
    auto& cache = state->mmu.code_cache();
    const auto& code = lookup.code;
    const auto budget = CodeBudget();
    const size_t remaining = budget - cache.llvm_block_bytes;
    const bool already_owned = code && cache.llvm_blocks.contains(code);
    const size_t bytes = code ? code->allocated_bytes : lookup.blocked_bytes;
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

    block_recipe::InstallBlockHandler(*block, replay, code->handler);
}

void QueueBlock(EmuState* state, BasicBlock* block, bool exact) {
    auto& async = state->mmu.core_handle()->llvm_async;
    if (!async) async = std::make_shared<LLVMAsyncState>();
    if (async->pending.size() == kMaxPendingPerCache) {
        block->chain.llvm_hot_count = kRetryHotCount;
        return;
    }
    auto result = std::make_shared<AsyncResult>();
    CompilationJob job{
        .ops = {},
        .bytes = {},
        .result = result,
        .signals = async->signals,
        .sentinel = block->sentinel_slot_index,
        .budget = CodeBudget() - state->mmu.code_cache().llvm_block_bytes,
        .exit = block_recipe::FullBlockExit(*block),
        .exact = exact,
    };
    const auto ops = std::span(block->FirstOp(), std::min(job.sentinel, kMaxOps));
    job.ops.reserve(ops.size());
    job.bytes.reserve(ops.size() * 15);
    for (const auto& op : ops) {
        if (!block_recipe::CanPlanOp(op)) break;
        const auto offset = job.bytes.size();
        job.bytes.resize(offset + op.len);
        if (!CopyDecodedInstructionBytes(state, op, std::span(job.bytes).subspan(offset))) return;
        auto& captured = job.ops.emplace_back(op);
        captured.handler = nullptr;
        if (GetExtKind(&captured) == ExtKind::ControlFlow)
            SetCachedTarget(&captured, nullptr);
        else if (GetExtKind(&captured) == ExtKind::Link)
            SetNextBlock(&captured, nullptr);
    }
    if (job.ops.empty()) return;
    if (!Workers().Submit(std::move(job))) {
        block->chain.llvm_hot_count = kRetryHotCount;
        return;
    }
    async->pending.push_back({std::move(result), block, block->start_eip(), block->invalidation_revision});
}

}  // namespace

uint16_t LLVMJitHotThreshold() {
    static const uint16_t threshold = []() -> uint16_t {
        if (RuntimeMode() == JitMode::Off) return 0;
        return ReadPositiveSetting("FIBERCPU_LLVM_JIT_HOT_THRESHOLD", kDefaultHotThreshold);
    }();
    return threshold;
}

void CompileHotLLVMBlock(EmuState* state, BasicBlock* block) {
    if (!block_recipe::CanPromoteBlock(*state, *block)) return;
    const auto mode = RuntimeMode();
    if (mode == JitMode::Off) return;
    const bool exact = mode != JitMode::Generic;
    if (mode != JitMode::Profile && WorkerCount()) {
        QueueBlock(state, block, exact);
        return;
    }
    block_recipe::Plan plan;
    if (!block_recipe::PlanBlock(*state, *block, exact, plan)) return;
    auto key = block_recipe::BlockKey(plan.Steps(), exact, plan.exit);
    if (mode == JitMode::Profile) {
        RecordBlock(key);
        return;
    }
    const size_t remaining = CodeBudget() - state->mmu.code_cache().llvm_block_bytes;
    auto result = Compiler().FindOrCompile(plan.Steps(), std::move(key), exact, plan.exit, remaining);
    InstallResult(state, block, result, plan.ReplayHandlers());
}

void PollLLVMBlockCompilations(EmuState* state) {
    const auto& async = state->mmu.core_handle()->llvm_async;
    if (!async || !async->signals->ready.exchange(false, std::memory_order_acquire)) return;
    std::erase_if(async->pending, [&](const PendingBlock& candidate) {
        if (!candidate.result->done.load(std::memory_order_acquire)) return false;
        auto* block = state->mmu.lookup_cached_block(candidate.eip);
        if (block != candidate.block) return true;
        if (block->invalidation_revision != candidate.revision) {
            if (block->entry == block->FirstOp()->handler) block->chain.llvm_hot_count = 1;
        } else if (block_recipe::CanPromoteBlock(*state, *block)) {
            InstallResult(state, block, candidate.result->lookup, candidate.result->replay);
        }
        return true;
    });
}

int BuildLinkedLLVMBlockCache(const char* profile_path, const char* cache_path) {
    if (!profile_path || !cache_path) return -1;
    auto input = llvm::MemoryBuffer::getFile(profile_path);
    if (!input) return -1;
    std::string_view profile = (*input)->getBuffer();
    if (!ReadProfileHeader(profile) || profile.empty()) return -1;

    auto temp = llvm::sys::fs::TempFile::create(llvm::Twine(cache_path) + ".%%%%%%");
    if (!temp) {
        llvm::consumeError(temp.takeError());
        return -1;
    }
    int count;
    {
        llvm::raw_fd_ostream output(temp->FD, false);
        count = BlockCompiler::BuildLinkedCache(profile, output);
        output.flush();
        if (output.has_error()) {
            output.clear_error();
            count = -1;
        }
    }
    if (count <= 0) {
        llvm::consumeError(temp->discard());
        return -1;
    }
    if (auto error = temp->keep(cache_path)) {
        llvm::consumeError(std::move(error));
        llvm::consumeError(temp->discard());
        return -1;
    }
    return count;
}

uint64_t LLVMJitBlockCount() { return materializations.load(std::memory_order_relaxed); }

uint64_t LLVMJitLiveBlockCount() { return live_blocks.load(std::memory_order_relaxed); }

}  // namespace fiberish
