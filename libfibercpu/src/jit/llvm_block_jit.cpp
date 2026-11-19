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
#include <bit>
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
#include <type_traits>
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
#include <llvm/Support/xxhash.h>
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
using block_recipe::BlockKeyView;
using block_recipe::kMaxOps;
constexpr uint16_t kDefaultHotThreshold = 64;
std::atomic<uint64_t> live_blocks{0};
std::atomic<uint64_t> materializations{0};
std::atomic<uint64_t> cache_epoch{0};
std::atomic<bool> flush_requested{false};
std::atomic<size_t> mmu_count{0};
std::shared_mutex execution_gate;

bool AcceptsJobs(uint64_t epoch) {
    return !flush_requested.load(std::memory_order_acquire) &&
           cache_epoch.load(std::memory_order_acquire) == epoch;
}

struct BlockKey {
    explicit BlockKey(BlockKeyView key)
        : exit(key.exit),
          operations(key.operations.begin(), key.operations.end()),
          operands(key.operands.begin(), key.operands.end()) {}

    operator BlockKeyView() const { return {exit, operations, operands}; }

    LLVMBlockExit exit;
    std::vector<block_recipe::OpId> operations;
    std::vector<DecodedOperandShape> operands;
};

struct BlockKeyHash {
    using is_transparent = void;
    using is_avalanching = void;

    size_t operator()(BlockKeyView key) const {
        // IDs and operand shapes have no padding. Hashing their native byte
        // spans is local to this process; disk records are encoded separately.
        static_assert(std::has_unique_object_representations_v<block_recipe::OpId>);
        static_assert(std::has_unique_object_representations_v<DecodedOperandShape>);
        const auto hash = [](auto span) {
            return llvm::xxh3_64bits({reinterpret_cast<const uint8_t*>(span.data()), span.size_bytes()});
        };
        return std::rotl(hash(key.operations), static_cast<int>(key.exit)) ^
               (key.IsExact() ? std::rotl(hash(key.operands), 23) : 0);
    }
};

struct BlockKeyEqual {
    using is_transparent = void;
    bool operator()(BlockKeyView lhs, BlockKeyView rhs) const { return lhs == rhs; }
};

using BlockKeySet = ankerl::unordered_dense::set<BlockKey, BlockKeyHash, BlockKeyEqual>;

const auto& ProfileFingerprint() {
    static const auto digest = [] {
        llvm::SHA256 hash;
        hash.update(llvm::ArrayRef<uint8_t>(kBitcode, sizeof(kBitcode)));
        hash.update(block_recipe::kOpIdFingerprint);
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

void RecordBlock(BlockKeyView key) {
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
    static BlockKeySet seen;
    std::lock_guard lock(mutex);
    if (!seen.contains(key)) {
        seen.emplace(key);
        const auto bytes = llvm_block_cache::EncodeKey(key);
        llvm_block_cache::WriteRecord(*output, {bytes.data(), bytes.size()});
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

std::string ObjectEntryName(uint64_t index) { return "FiberCachedBlock_" + std::to_string(index); }

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
    LLVMOperandTemplates templates{module.get()};
};

class BlockCompiler {
    std::unique_ptr<llvm::orc::LLJIT> jit_;
    BlockMemoryManager* memory_ = nullptr;
    llvm::orc::ResourceTrackerSP generation_tracker_{};
    std::mutex mutex_;
    std::mutex link_mutex_;
    std::condition_variable compilation_done_;
    ankerl::unordered_dense::map<BlockKey, HandlerFunc, BlockKeyHash, BlockKeyEqual> compiled_;
    BlockKeySet compiling_;
    uint64_t next_symbol_ = 0;

    unsigned failed_links_ = 0;

    void ClearDeadSymbols() {
        jit_->getExecutionSession().getSymbolStringPool()->clearDeadEntries();
        failed_links_ = 0;
    }

    HandlerFunc Materialize(llvm::StringRef name, std::unique_ptr<llvm::MemoryBuffer> object) {
        memory_->BeginLink();
        // Use a scratch tracker so a failed link cannot remove earlier
        // objects. Successful objects leave only the generation tracker live.
        auto tracker = jit_->getMainJITDylib().createResourceTracker();
        const auto fail_link = [&] {
            llvm::consumeError(tracker->remove());
            if (++failed_links_ == 256) ClearDeadSymbols();
            if (live_blocks.load(std::memory_order_relaxed) && memory_->NeedsFlush())
                flush_requested.store(true, std::memory_order_release);
        };
        if (auto error = jit_->addObjectFile(tracker, std::move(object))) {
            llvm::consumeError(std::move(error));
            fail_link();
            return nullptr;
        }
        auto code = jit_->lookup(name);
        if (!code) {
            llvm::consumeError(code.takeError());
            fail_link();
            return nullptr;
        }
        if (!generation_tracker_) generation_tracker_ = jit_->getMainJITDylib().createResourceTracker();
        tracker->transferTo(*generation_tracker_);
        return code->toPtr<HandlerFunc>();
    }

public:
    ~BlockCompiler() { live_blocks.store(0, std::memory_order_relaxed); }

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

    HandlerFunc FindOrCompile(BlockKeyView key, uint64_t epoch) {
        if (!jit_ || !AcceptsJobs(epoch)) return nullptr;
        std::unique_lock lock(mutex_);
        compilation_done_.wait(lock, [&] { return !AcceptsJobs(epoch) || !compiling_.contains(key); });
        if (!AcceptsJobs(epoch)) return nullptr;
        if (auto found = compiled_.find(key); found != compiled_.end()) return found->second;
        const auto name = "FiberJitBlock_" + std::to_string(next_symbol_++);
        compiling_.emplace(key);
        lock.unlock();

        // An old job can finish optimizing after a reset. Recheck the epoch
        // under the same lock that serializes the linker and arena reset.
        thread_local BlockIR worker_ir;
        std::unique_ptr<llvm::MemoryBuffer> object;
        if (auto module = PrepareLLVMBlock(worker_ir.templates, key, name)) {
            auto result = jit_->getIRCompileLayer().getCompiler()(*module);
            if (result)
                object = std::move(*result);
            else
                llvm::consumeError(result.takeError());
        }

        std::lock_guard link_lock(link_mutex_);
        auto entry = object && AcceptsJobs(epoch) ? Materialize(name, std::move(object)) : nullptr;
        lock.lock();
        auto recipe = compiling_.extract(key);
        if (entry) {
            compiled_.emplace(std::move(*recipe), entry);
            live_blocks.fetch_add(1, std::memory_order_relaxed);
            materializations.fetch_add(1, std::memory_order_relaxed);
        }
        compilation_done_.notify_all();
        return entry;
    }

    void ResetGeneration() {
        std::lock_guard link_lock(link_mutex_);
        std::lock_guard lock(mutex_);
        // Invalidate idle MMU caches before releasing their entry addresses.
        cache_epoch.fetch_add(1, std::memory_order_release);
        compiled_.clear();
        if (generation_tracker_) {
            if (auto error = generation_tracker_->remove())
                llvm::logAllUnhandledErrors(std::move(error), llvm::errs(), "JIT cache flush: ");
            generation_tracker_.reset();
            ClearDeadSymbols();
        }
        live_blocks.store(0, std::memory_order_relaxed);
        flush_requested.store(false, std::memory_order_release);
        compilation_done_.notify_all();
    }

    static int BuildLinkedCache(std::string_view profile, llvm::raw_ostream& output) {
        if (!InitializeLLVMTarget()) return -1;
        BlockIR ir;
        if (!ir.templates.IsValid()) return -1;
        auto machine = CreateLLVMObjectTarget(*ir.module);
        if (!machine) {
            llvm::consumeError(machine.takeError());
            return -1;
        }
        llvm::orc::TMOwningSimpleCompiler compiler(std::move(*machine));
        output << llvm_block_cache::linked_magic << FIBERCPU_LINKED_BLOCK_CACHE_ABI << block_recipe::kOpIdFingerprint;
        if (!llvm_block_cache::WriteRecord(output, LinkedBlockCacheHostCPU())) return -1;
        int compiled = 0;
        BlockKeySet seen;
        block_recipe::Plan plan;
        while (!profile.empty()) {
            std::string_view record;
            bool exact;
            if (!llvm_block_cache::ReadRecord(profile, record) || !llvm_block_cache::DecodeKey(record, plan, exact) ||
                !exact)
                return -1;
            const auto key = plan.Key(exact);
            if (seen.contains(key)) continue;
            seen.emplace(key);
            auto module = PrepareLLVMBlock(ir.templates, key, ObjectEntryName(compiled));
            if (!module) continue;
            auto object = compiler(*module);
            if (!object) {
                llvm::logAllUnhandledErrors(object.takeError(), llvm::errs(), "LLVM block cache: ");
                continue;
            }
            if (!llvm_block_cache::WriteRecord(output, record) ||
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
    HandlerFunc entry = nullptr;
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
    uint64_t epoch;
    LLVMBlockExit exit;
    bool exact;

    void Compile(AsyncSignals& control) {
        if (result.expired() || !AcceptsJobs(epoch)) return;
        block_recipe::Plan plan;
        auto input = std::span<const uint8_t>(bytes);
        const auto redecode = [&](const DecodedOp& op, DecodedOp& generic) {
            const auto instruction = input.first(op.len);
            input = input.subspan(op.len);
            return RedecodeGenericInstruction(instruction, op, generic);
        };
        const bool planned = block_recipe::PlanBlock(ops, sentinel, exit, exact, redecode, plan);
        if (result.expired() || !AcceptsJobs(epoch)) return;
        const auto entry = planned ? Compiler().FindOrCompile(plan.Key(exact), epoch) : nullptr;
        if (auto output = result.lock()) {
            output->entry = entry;
            if (entry) output->replay.assign(plan.ReplayHandlers().begin(), plan.ReplayHandlers().end());
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

void FinishPendingFlush() {
    if (!flush_requested.load(std::memory_order_acquire)) return;
    std::unique_lock lock(execution_gate);
    if (flush_requested.load(std::memory_order_acquire)) Compiler().ResetGeneration();
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
        .epoch = LLVMJitCacheEpoch(),
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

ScopedLLVMJitExecution::ScopedLLVMJitExecution() {
    const auto mode = RuntimeMode();
    if (mode != JitMode::Exact && mode != JitMode::Generic) return;

    for (;;) {
        FinishPendingFlush();
        lock_ = std::shared_lock(execution_gate);
        if (!LLVMJitCacheFlushPending()) break;
        lock_.unlock();
    }
}

ScopedLLVMJitExecution::~ScopedLLVMJitExecution() {
    if (lock_.owns_lock()) {
        lock_.unlock();
        FinishPendingFlush();
    }
}

bool LLVMJitCacheFlushPending() { return flush_requested.load(std::memory_order_acquire); }

uint64_t LLVMJitCacheEpoch() { return cache_epoch.load(std::memory_order_acquire); }

void LLVMJitMmuCreated() { mmu_count.fetch_add(1, std::memory_order_relaxed); }

void LLVMJitMmuDestroyed() {
    if (mmu_count.fetch_sub(1, std::memory_order_acq_rel) != 1 || !LLVMJitLiveBlockCount()) return;
    flush_requested.store(true, std::memory_order_release);
    FinishPendingFlush();
}

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
    const auto key = plan.Key(exact);
    if (mode == JitMode::Profile) {
        RecordBlock(key);
        return;
    }
    if (auto entry = Compiler().FindOrCompile(key, LLVMJitCacheEpoch()))
        block_recipe::InstallBlockHandler(*block, plan.ReplayHandlers(), entry);
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
        } else if (candidate.result->entry && block_recipe::CanPromoteBlock(*state, *block)) {
            block_recipe::InstallBlockHandler(*block, candidate.result->replay, candidate.result->entry);
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
