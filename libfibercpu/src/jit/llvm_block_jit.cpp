#include "jit/llvm_block_jit.h"
#include "jit/block_recipe.h"
#include "jit/executable_arena.h"
#include "jit/linked_block_cache_host.h"
#include "jit/llvm_async_signals.h"
#include "jit/llvm_block_cache.h"
#include "jit/llvm_block_memory.h"
#include "jit/llvm_operand_ir.h"
#if FIBERCPU_GUEST_I386
#include "linked_block_cache_abi.h"
#endif
#if FIBERCPU_GUEST_AARCH64
#include "aarch64/softfloat_jit_helpers.h"
#include "aarch64/system_handlers.h"
#include "jit/llvm_arm_ir.h"
#include "jit/llvm_arm_jit.h"
#endif

#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <atomic>
#include <bit>
#include <cfenv>
#include <charconv>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <deque>
#include <functional>
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

#include <llvm/ADT/StringExtras.h>
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
#include "operand_specialization.h"
#include "ops.h"
#include "ops/ops_control_impl.h"

namespace fiberish {

#if FIBERCPU_GUEST_AARCH64
extern "C" uint64_t FiberArmReadCounter() noexcept { return aarch64::detail::ReadCounterValue(); }
#endif

#if FIBERCPU_GUEST_I386
using RecipeWeights = ankerl::unordered_dense::map<std::string, uint64_t>;

struct WeightedProfile {
    explicit WeightedProfile(bool same_page) : same_page(same_page) {}

    const bool same_page;
    std::mutex mutex;
    ankerl::unordered_dense::map<const BasicBlock*, std::string> blocks;
    RecipeWeights retired;
};

#endif

namespace {

#if FIBERCPU_GUEST_I386
#include "llvm_block_bitcode.inc"

using block_recipe::BlockKeyView;
using block_recipe::kMaxOps;
#endif

constexpr uint16_t kDefaultHotThreshold = 64;
std::atomic<uint64_t> live_blocks{0};
std::atomic<uint64_t> materializations{0};
std::atomic<uint64_t> cache_epoch{0};
std::atomic<bool> flush_requested{false};
std::atomic<size_t> mmu_count{0};
std::shared_mutex execution_gate;

unsigned DefaultWorkerCount() {
    const auto* value = std::getenv("FIBERCPU_LLVM_JIT_WORKERS");
    if (!value) return 2;
    unsigned threads;
    const auto* end = value + std::strlen(value);
    const auto parsed = std::from_chars(value, end, threads);
    return parsed.ec == std::errc{} && parsed.ptr == end ? std::min(threads, 8u) : 2u;
}

bool DefaultFoldOperandLoads() {
    const auto* value = std::getenv("FIBERCPU_LLVM_FOLD_OPERAND_LOADS");
    return value && std::strcmp(value, "1") == 0;
}

struct RuntimeOptions {
    std::string pipeline;
    unsigned workers = DefaultWorkerCount();
    bool fold_operand_loads = DefaultFoldOperandLoads();
};

struct RuntimeConfiguration {
    std::mutex mutex;
    RuntimeOptions options;
    bool frozen = false;
};

RuntimeConfiguration& Configuration() {
    static RuntimeConfiguration config;
    return config;
}

const RuntimeOptions& Options() {
    static const auto options = [] {
        auto& config = Configuration();
        std::lock_guard lock(config.mutex);
        config.frozen = true;
        return config.options;
    }();
    return options;
}

const CpuRunOptions* RunOptions(const EmuState& state) {
    const auto* core = state.mmu.core_handle();
    return core ? core->code_cache.run_options.get() : nullptr;
}

#if FIBERCPU_GUEST_I386
void AddWeight(uint64_t& total, uint64_t count) {
    total += std::min(count, std::numeric_limits<uint64_t>::max() - total);
}

void TrackWeightedBlock(EmuState& state, BasicBlock& block, BlockKeyView key) {
    auto& profile = *RunOptions(state)->profile;
    const auto encoded = llvm_block_cache::EncodeKey(key);
    std::lock_guard lock(profile.mutex);
    profile.blocks.try_emplace(&block, encoded.data(), encoded.size());
    state.mmu.code_cache().has_weighted_profile = true;
}

void CollectPendingPageProfile(EmuState& state) {
    auto* core = state.mmu.core_handle();
    auto& pending = core->code_cache.pending_page_profile;
    if (pending.empty()) return;
    auto snapshot = std::exchange(pending, {});
    auto& profile = *RunOptions(state)->profile;
    block_recipe::Plan plan;
    for (const auto& [block, revision] : snapshot) {
        if (block->invalidation_revision != revision || !block_recipe::CanPromoteBlock(state, *block)) continue;
        {
            std::lock_guard lock(profile.mutex);
            if (profile.blocks.contains(block)) continue;
        }
        const bool planned = block_recipe::PlanBlock(state, *block, true, plan);
        if (state.mmu.core_handle() != core || core->code_cache.pending_flush) return;
        if (planned && block->is_valid() && block->invalidation_revision == revision)
            TrackWeightedBlock(state, *block, plan.Key(true));
    }
}

#endif

bool AcceptsJobs(uint64_t epoch) {
    return !flush_requested.load(std::memory_order_acquire) && cache_epoch.load(std::memory_order_acquire) == epoch;
}

#if FIBERCPU_GUEST_I386
struct BlockKey {
    explicit BlockKey(BlockKeyView key)
        : exit(key.exit),
          operations(key.operations.begin(), key.operations.end()),
          operands(key.operands.begin(), key.operands.end()),
          eips(key.eips.begin(), key.eips.end()) {}

    operator BlockKeyView() const { return {exit, operations, operands, eips}; }

    LLVMBlockExit exit;
    std::vector<block_recipe::BoundOperation> operations;
    std::vector<DecodedOperandShape> operands;
    std::vector<block_recipe::StepEip> eips;
};

struct BlockKeyHash {
    using is_transparent = void;
    using is_avalanching = void;

    size_t operator()(BlockKeyView key) const {
        // IDs and operand shapes have no padding. Hashing their native byte
        // spans is local to this process; disk records are encoded separately.
        static_assert(std::has_unique_object_representations_v<block_recipe::BoundOperation>);
        static_assert(std::has_unique_object_representations_v<DecodedOperandShape>);
        static_assert(std::has_unique_object_representations_v<block_recipe::StepEip>);
        const auto hash = [](auto span) {
            return llvm::xxh3_64bits({reinterpret_cast<const uint8_t*>(span.data()), span.size_bytes()});
        };
        return std::rotl(hash(key.operations), static_cast<int>(key.exit)) ^
               (key.IsExact() ? std::rotl(hash(key.operands), 23) ^ std::rotl(hash(key.eips), 37) : 0);
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

void WriteProfileHeader(llvm::raw_ostream& out, bool weighted = false) {
    const auto magic = weighted ? llvm_block_cache::weighted_profile_magic : llvm_block_cache::profile_magic;
    out.write(magic.data(), magic.size());
    const auto& fingerprint = ProfileFingerprint();
    out.write(reinterpret_cast<const char*>(fingerprint.data()), fingerprint.size());
}

bool ReadProfileHeader(std::string_view& bytes, bool& weighted) {
    weighted = bytes.starts_with(llvm_block_cache::weighted_profile_magic);
    const auto magic = weighted ? llvm_block_cache::weighted_profile_magic : llvm_block_cache::profile_magic;
    if (!bytes.starts_with(magic)) return false;
    bytes.remove_prefix(magic.size());
    const auto& fingerprint = ProfileFingerprint();
    if (bytes.size() < fingerprint.size() || std::memcmp(bytes.data(), fingerprint.data(), fingerprint.size()) != 0)
        return false;
    bytes.remove_prefix(fingerprint.size());
    return true;
}

struct ProfileRecipe {
    std::string_view key;
    uint64_t weight;
};

bool ReadProfileRecipes(std::string_view bytes, bool weighted, std::vector<ProfileRecipe>& recipes) {
    ankerl::unordered_dense::map<std::string_view, size_t> indexes;
    block_recipe::Plan plan;
    while (!bytes.empty()) {
        uint64_t weight = 0;
        if (weighted) {
            if (bytes.size() < sizeof(weight)) return false;
            weight = llvm::support::endian::read64le(bytes.data());
            bytes.remove_prefix(sizeof(weight));
            if (!weight) return false;
        }
        std::string_view key;
        bool exact;
        if (!llvm_block_cache::ReadRecord(bytes, key) || !llvm_block_cache::DecodeKey(key, plan, exact) || !exact)
            return false;
        auto [it, added] = indexes.try_emplace(key, recipes.size());
        if (added)
            recipes.push_back({key, weight});
        else
            AddWeight(recipes[it->second].weight, weight);
    }
    if (weighted) {
        std::ranges::sort(recipes, [](const auto& a, const auto& b) {
            return a.weight != b.weight ? a.weight > b.weight : a.key < b.key;
        });
    }
    return !recipes.empty();
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

#endif

JitMode RuntimeMode(const EmuState& state) {
    if (const auto* options = RunOptions(state); options && options->jit_mode) return *options->jit_mode;
    static const auto mode = [] {
        const char* value = std::getenv("FIBERCPU_LLVM_JIT");
        if (!value || !pthread_jit_write_protect_supported_np()) return JitMode::Off;
        if (std::strcmp(value, "1") == 0) return JitMode::Exact;
        if (std::strcmp(value, "generic") == 0) return JitMode::Generic;
        if (std::strcmp(value, "region") == 0) return JitMode::Region;
        if (std::strcmp(value, "profile") == 0 && std::getenv("FIBERCPU_LLVM_JIT_PROFILE"))
            return JitMode::LegacyProfile;
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

#if FIBERCPU_GUEST_I386
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

using PageRegionEntry = mem::CodeCacheState::PageRegionEntry;
using PageRegionStamp = mem::Mmu::PageRegionStamp;

struct CompiledPageRegion {
    std::vector<size_t> included;
    std::vector<HandlerFunc> entries;
};

constexpr size_t kMaxRegionMembers = 12;

void WriteRegionMember(llvm::raw_ostream& out, const LLVMPageMember& member) {
    llvm_block_cache::WriteU32(out, member.page_offset);
    const auto key = llvm_block_cache::EncodeKey(member.key);
    llvm_block_cache::WriteRecord(out, {key.data(), key.size()});
}

bool ReadRegionRecord(std::string_view input, std::vector<std::pair<uint32_t, std::string_view>>& chunks) {
    chunks.clear();
    if (input.empty() || uint8_t(input.front()) != llvm_block_cache::region_tag) return false;
    input.remove_prefix(1);
    while (!input.empty()) {
        uint32_t offset;
        std::string_view record;
        if (chunks.size() == kMaxRegionMembers || !llvm_block_cache::ReadU32(input, offset) ||
            offset > mem::PAGE_MASK || (!chunks.empty() && offset <= chunks.back().first) ||
            !llvm_block_cache::ReadRecord(input, record))
            return false;
        chunks.emplace_back(offset, record);
    }
    return chunks.size() >= 2;
}

#endif

class BlockCompiler {
    std::unique_ptr<llvm::orc::LLJIT> jit_;
    BlockMemoryManager* memory_ = nullptr;
    llvm::orc::ResourceTrackerSP generation_tracker_{};
    std::mutex mutex_;
    std::mutex link_mutex_;
    std::condition_variable compilation_done_;
#if FIBERCPU_GUEST_I386
    ankerl::unordered_dense::map<BlockKey, HandlerFunc, BlockKeyHash, BlockKeyEqual> compiled_;
    BlockKeySet compiling_;
    ankerl::unordered_dense::map<std::string, CompiledPageRegion> compiled_regions_;
#endif

#if FIBERCPU_GUEST_AARCH64
    ankerl::unordered_dense::map<std::string, std::vector<aarch64::detail::HandlerFunc>> compiled_arm_;
    ankerl::unordered_dense::set<std::string> compiling_arm_;
#endif
    uint64_t next_symbol_ = 0;

    unsigned failed_links_ = 0;

    void ClearDeadSymbols() {
        jit_->getExecutionSession().getSymbolStringPool()->clearDeadEntries();
        failed_links_ = 0;
    }

    template <class Entry>
    bool Materialize(std::span<const std::string> names, std::unique_ptr<llvm::MemoryBuffer> object,
                     std::span<Entry> entries) {
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
            return false;
        }
        for (auto [i, name] : llvm::enumerate(names)) {
            auto code = jit_->lookup(name);
            if (!code) {
                llvm::consumeError(code.takeError());
                fail_link();
                return false;
            }
            entries[i] = code->toPtr<Entry>();
        }
        if (!generation_tracker_) generation_tracker_ = jit_->getMainJITDylib().createResourceTracker();
        tracker->transferTo(*generation_tracker_);
        return true;
    }

public:
    ~BlockCompiler() { live_blocks.store(0, std::memory_order_relaxed); }

    BlockCompiler() {
        if (!InitializeLLVMTarget()) return;
#if FIBERCPU_GUEST_I386
        BlockIR ir;
        if (!ir.module) return;
#endif

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
#if FIBERCPU_GUEST_I386
        const std::pair<llvm::StringRef, HandlerFunc> helpers[] = {
            {"_ZN8fiberish15MemoryOpRestartE", MemoryOpRestart},
            {"_ZN8fiberish13MemoryOpRetryE", MemoryOpRetry},
            {"_ZN8fiberish27ResolveBranchTargetSlowLinkE", ResolveBranchTargetSlowLink},
            {"_ZN8fiberish33ResolveBranchTargetSlowDoubleSlotE", ResolveBranchTargetSlowDoubleSlot},
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
#endif

        bind("memcmp", &std::memcmp);
        bind("memcpy", &std::memcpy);
        bind("memmove", &std::memmove);
        bind("memset", &std::memset);
        bind("bzero", &::bzero);
#if FIBERCPU_GUEST_I386
        bind("_Znam", static_cast<void* (*)(size_t)>(::operator new[]));
        bind("FiberReadRealtimeTsc", &FiberReadRealtimeTsc);
#endif

#if FIBERCPU_GUEST_AARCH64
        bind("FiberArmJitRetry", &aarch64::detail::FiberArmJitRetry);
        bind("FiberArmJitLeave", &aarch64::detail::FiberArmJitLeave);
        bind("FiberArmJitChainMiss", &aarch64::detail::FiberArmJitChainMiss);
        bind("FiberArmJitRefetch", &aarch64::detail::FiberArmJitRefetch);
        bind("FiberArmReadCounter", &FiberArmReadCounter);
#if FIBERCPU_AARCH64_SOFTFLOAT
        bind("FiberArmSoftFloatArithmetic", &aarch64::detail::FiberArmSoftFloatArithmetic);
        bind("FiberArmSoftFloatConvert", &aarch64::detail::FiberArmSoftFloatConvert);
        bind("FiberArmSoftFloatIntToFP", &aarch64::detail::FiberArmSoftFloatIntToFP);
#else
        bind("feholdexcept", &std::feholdexcept);
        bind("fesetenv", &std::fesetenv);
        bind("fesetround", &std::fesetround);
        bind("fetestexcept", &std::fetestexcept);
#endif
#endif
        if (auto error = (*engine)->getMainJITDylib().define(llvm::orc::absoluteSymbols(std::move(slow_paths)))) {
            llvm::consumeError(std::move(error));
            return;
        }
        jit_ = std::move(*engine);
        memory_ = memory_ptr;
    }

#if FIBERCPU_GUEST_I386
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
        const auto& options = Options();
        if (auto module = PrepareLLVMBlock(worker_ir.templates, key, name, /*prune_dead_globals=*/true,
                                           options.pipeline, options.fold_operand_loads)) {
            auto result = jit_->getIRCompileLayer().getCompiler()(*module);
            if (result)
                object = std::move(*result);
            else
                llvm::consumeError(result.takeError());
        }

        std::lock_guard link_lock(link_mutex_);
        HandlerFunc entry = nullptr;
        if (object && AcceptsJobs(epoch) && !Materialize(std::span(&name, 1), std::move(object), std::span(&entry, 1)))
            entry = nullptr;
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

    CompiledPageRegion FindOrCompileRegion(std::span<const LLVMPageMember> members, uint64_t epoch) {
        if (!jit_ || !AcceptsJobs(epoch)) return {};
        std::string key;
        llvm::raw_string_ostream encoded(key);
        for (const auto& member : members) {
            const auto block = llvm_block_cache::EncodeKey(member.key);
            const auto offset = uint16_t(member.page_offset);
            encoded << char(offset) << char(offset >> 8);
            llvm_block_cache::WriteRecord(encoded, llvm::StringRef(block.data(), block.size()));
        }
        std::string name;
        {
            std::lock_guard lock(mutex_);
            if (const auto found = compiled_regions_.find(key); found != compiled_regions_.end()) return found->second;
            name = "FiberJitPage_" + std::to_string(next_symbol_++);
        }

        thread_local BlockIR worker_ir;
        CompiledPageRegion compiled;
        const auto& options = Options();
        auto module = PrepareLLVMPageRegion(worker_ir.templates, members, name, options.pipeline,
                                            options.fold_operand_loads, compiled.included);
        if (!module) return {};
        auto object = jit_->getIRCompileLayer().getCompiler()(*module);
        if (!object) {
            llvm::consumeError(object.takeError());
            return {};
        }
        std::vector<std::string> names;
        names.reserve(compiled.included.size());
        for (size_t i = 0; i < compiled.included.size(); ++i) names.push_back(name + "_" + std::to_string(i));
        compiled.entries.resize(names.size());

        std::lock_guard link_lock(link_mutex_);
        std::lock_guard lock(mutex_);
        if (!AcceptsJobs(epoch)) return {};
        if (const auto found = compiled_regions_.find(key); found != compiled_regions_.end()) return found->second;
        if (!Materialize(names, std::move(*object), std::span(compiled.entries))) return {};
        live_blocks.fetch_add(compiled.entries.size(), std::memory_order_relaxed);
        materializations.fetch_add(compiled.entries.size(), std::memory_order_relaxed);
        return compiled_regions_.emplace(std::move(key), std::move(compiled)).first->second;
    }

#endif

#if FIBERCPU_GUEST_AARCH64
    std::vector<aarch64::detail::HandlerFunc> FindOrCompileArm(std::span<const arm_jit::Member> members,
                                                               uint64_t epoch) {
        if (!jit_ || !AcceptsJobs(epoch)) return {};
        std::string key;
        for (const auto& member : members) {
            const uint16_t offset = members.size() == 1 ? 0 : member.offset;
            const uint16_t count = uint16_t(member.steps.size());
            key.append(reinterpret_cast<const char*>(&offset), sizeof(offset));
            key.append(reinterpret_cast<const char*>(&count), sizeof(count));
            for (const auto& step : member.steps) {
                key.append(reinterpret_cast<const char*>(&step.word), sizeof(step.word));
                key.push_back(char(step.slots));
            }
        }
        std::unique_lock lock(mutex_);
        compilation_done_.wait(lock, [&] { return !AcceptsJobs(epoch) || !compiling_arm_.contains(key); });
        if (!AcceptsJobs(epoch)) return {};
        if (const auto found = compiled_arm_.find(key); found != compiled_arm_.end()) return found->second;
        const auto name =
            (members.size() == 1 ? "FiberArmJitBlock_" : "FiberArmJitPage_") + std::to_string(next_symbol_++);
        compiling_arm_.insert(key);
        lock.unlock();

        const auto& options = Options();
        std::unique_ptr<llvm::MemoryBuffer> object;
        if (auto module = arm_jit::Prepare(members, name, options.pipeline, options.fold_operand_loads)) {
            auto emitted = jit_->getIRCompileLayer().getCompiler()(*module);
            if (emitted)
                object = std::move(*emitted);
            else
                llvm::logAllUnhandledErrors(emitted.takeError(), llvm::errs(), "A64 JIT: ");
        }

        std::vector<std::string> names;
        if (members.size() == 1)
            names.push_back(name);
        else
            for (size_t i = 0; i < members.size(); ++i) names.push_back(name + "_" + std::to_string(i));
        std::vector<aarch64::detail::HandlerFunc> entries(names.size());
        std::lock_guard link_lock(link_mutex_);
        if (!object || !AcceptsJobs(epoch) ||
            !Materialize(std::span<const std::string>(names), std::move(object), std::span(entries)))
            entries.clear();
        lock.lock();
        compiling_arm_.erase(key);
        if (!entries.empty()) {
            live_blocks.fetch_add(entries.size(), std::memory_order_relaxed);
            materializations.fetch_add(entries.size(), std::memory_order_relaxed);
            compiled_arm_.emplace(std::move(key), entries);
        }
        compilation_done_.notify_all();
        return entries;
    }
#endif

    void ResetGeneration() {
        std::lock_guard link_lock(link_mutex_);
        std::lock_guard lock(mutex_);
        // Invalidate idle MMU caches before releasing their entry addresses.
        cache_epoch.fetch_add(1, std::memory_order_release);
#if FIBERCPU_GUEST_I386
        compiled_.clear();
        compiled_regions_.clear();
#endif

#if FIBERCPU_GUEST_AARCH64
        compiled_arm_.clear();
#endif
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

#if FIBERCPU_GUEST_I386
    static int BuildLinkedCache(std::span<const ProfileRecipe> recipes, std::span<const std::string> regions,
                                llvm::raw_ostream& output, uint32_t limit, llvm::StringRef pipeline) {
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
        block_recipe::Plan plan;
        const bool fold_operand_loads = DefaultFoldOperandLoads();
        for (const auto& recipe : recipes) {
            const auto record = recipe.key;
            bool exact;
            if (!llvm_block_cache::DecodeKey(record, plan, exact) || !exact) return -1;
            auto module = PrepareLLVMBlock(ir.templates, plan.Key(true), ObjectEntryName(compiled),
                                           /*prune_dead_globals=*/false, pipeline, fold_operand_loads);
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
            if (limit && static_cast<uint32_t>(compiled) == limit) break;
        }
        size_t baked_regions = 0;
        std::vector<std::pair<uint32_t, std::string_view>> chunks;
        for (const auto& region : regions) {
            if (!ReadRegionRecord(region, chunks)) return -1;
            std::vector<block_recipe::Plan> plans(chunks.size());
            std::vector<LLVMPageMember> members;
            for (auto [i, chunk] : llvm::enumerate(chunks)) {
                const auto& [offset, record] = chunk;
                bool exact = false;
                if (!llvm_block_cache::DecodeKey(record, plans[i], exact) || !exact ||
                    plans[i].exit == LLVMBlockExit::Dispatch)
                    return -1;
                members.push_back({plans[i].Key(true), offset});
            }
            if (members.size() < 2) continue;
            std::vector<size_t> included;
            const auto name = "FiberCachedPage_" + std::to_string(compiled);
            auto module = PrepareLLVMPageRegion(ir.templates, members, name, pipeline, fold_operand_loads, included);
            if (!module) continue;
            auto object = compiler(*module);
            if (!object) {
                llvm::logAllUnhandledErrors(object.takeError(), llvm::errs(), "LLVM page cache: ");
                continue;
            }
            std::string record;
            llvm::raw_string_ostream encoded(record);
            encoded << char(llvm_block_cache::region_tag);
            for (const auto i : included) WriteRegionMember(encoded, members[i]);
            if (!llvm_block_cache::WriteRecord(output, record) ||
                !llvm_block_cache::WriteRecord(output, (*object)->getBuffer()))
                return -1;
            ++compiled;
            ++baked_regions;
        }
        if (!regions.empty()) {
            if (!baked_regions) return -1;
            llvm::outs() << "Compiled " << baked_regions << " page regions\n";
        }
        return compiled;
    }
#endif
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

#if FIBERCPU_GUEST_I386
constexpr size_t kMaxCandidates = 128;
constexpr uint16_t kCandidateCounter = UINT16_MAX;
constexpr uint16_t kMinCandidateRetryCount = 1024;
#endif

using JitClock = std::chrono::steady_clock;
#if FIBERCPU_GUEST_I386
using namespace std::chrono_literals;
constexpr auto kSampleInterval = 2ms;
constexpr auto kCandidateExpiry = 60ms;

#endif

uint64_t Ns(JitClock::duration duration) {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(duration).count();
}

std::atomic<uint64_t> traced_worker_cpu_ns{0};
void ReportJitSchedulerTotals();

FILE* JitSchedulerTrace() {
    static FILE* trace = []() -> FILE* {
        const char* path = std::getenv("FIBERCPU_LLVM_JIT_SCHEDULER_TRACE");
        if (!path || !*path) return nullptr;
        auto* file = std::fopen(path, "w");
        if (!file) std::perror("LLVM JIT scheduler trace");
        if (file) std::atexit(ReportJitSchedulerTotals);
        return file;
    }();
    return trace;
}

void ReportJitSchedulerTotals() {
    std::fprintf(JitSchedulerTrace(), "worker_cpu_ns,%llu\n",
                 (unsigned long long)traced_worker_cpu_ns.load(std::memory_order_relaxed));
}

#if FIBERCPU_GUEST_I386
struct RegionResult {
    PageRegionStamp stamp;
    std::string recipe;
    std::vector<PageRegionEntry> entries;
    std::vector<std::vector<HandlerFunc>> replay;
};

bool PublishRegion(EmuState& state, const RegionResult& result) {
    if (!state.mmu.publish_page_region(result.stamp, result.entries)) return false;
    for (auto [i, member] : llvm::enumerate(result.entries)) {
        // These scalar slots also serve the interpreter after region revocation.
        block_recipe::InstallBlockHandler(*member.block, result.replay[i], member.handler);
        member.block->chain.llvm_hot_count = 0;
    }
    if (auto* trace = JitSchedulerTrace()) {
        std::fprintf(trace, "region_install,%llu,%u,%zu\n", (unsigned long long)result.stamp.mmu_identity,
                     result.stamp.guest_page, result.entries.size());
        const auto fingerprint = llvm::toHex(llvm::ArrayRef<uint8_t>(ProfileFingerprint()));
        const auto recipe = llvm::toHex(result.recipe);
        std::fprintf(trace, "region_recipe,%s,%s\n", fingerprint.c_str(), recipe.c_str());
    }
    return true;
}

struct AsyncResult {
    std::atomic<bool> done{false};
    HandlerFunc entry = nullptr;
    std::vector<HandlerFunc> replay;
    std::optional<RegionResult> region;
};

struct PendingBlock {
    std::shared_ptr<AsyncResult> result;
    BasicBlock* block;
    uint32_t eip;
    uint16_t revision;
};

struct HotCandidate {
    BasicBlock* block;
    uint32_t eip;
    uint16_t revision;
    float benefit_per_cost;
    JitClock::time_point sampled;
    JitClock::time_point last_active;
    uint16_t counter = kCandidateCounter;
    float rate = 1;
    bool sampled_once = false;

    float Score() const { return rate * benefit_per_cost; }
};

}  // namespace

struct LLVMAsyncState {
    std::shared_ptr<AsyncSignals> signals = std::make_shared<AsyncSignals>();
    std::vector<PendingBlock> pending;
    std::vector<HotCandidate> candidates;
    JitClock::time_point next_sample{};
    unsigned poll_ticks = 0;

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
    state.candidates.clear();
    state.next_sample = {};
}

namespace {

struct BlockInput {
    std::vector<DecodedInstruction> ops;
    std::vector<uint8_t> bytes;
    size_t sentinel;
    uint32_t eip;
    LLVMBlockExit exit;

    bool Plan(bool exact, block_recipe::Plan& plan) const {
        auto input = std::span<const uint8_t>(bytes);
        const auto redecode = [&](const DecodedInstruction& op, DecodedInstruction& generic) {
            const auto instruction = input.first(op.len);
            input = input.subspan(op.len);
            return RedecodeGenericInstruction(instruction, op, generic);
        };
        return block_recipe::PlanBlock(ops, eip, sentinel, exit, exact, redecode, plan);
    }
};

std::optional<BlockInput> CaptureBlock(EmuState& state, const BasicBlock& block) {
    BlockInput input{{}, {}, block.inst_count(), block.start_eip(), block_recipe::FullBlockExit(block)};
    const auto count = std::min(input.sentinel, kMaxOps);
    input.ops.reserve(count);
    input.bytes.reserve(count * 15);
    for (size_t slot = 0; slot < count; ++slot) {
        auto op = block.ReadOp(slot);
        if (!block_recipe::CanPlanOp(op)) break;
        const auto offset = input.bytes.size();
        input.bytes.resize(offset + op.len);
        if (!CopyDecodedInstructionBytes(&state, op, std::span(input.bytes).subspan(offset))) return std::nullopt;
        op.handler = nullptr;
        input.ops.push_back(op);
    }
    if (input.ops.empty()) return std::nullopt;
    return input;
}

struct CapturedRegion {
    PageRegionStamp stamp;

    struct Member {
        PageRegionEntry entry;
        BlockInput input;
    };
    std::vector<Member> members;

    std::optional<RegionResult> Compile(uint64_t epoch) const {
        std::vector<block_recipe::Plan> plans(members.size());
        std::vector<LLVMPageMember> keys;
        std::vector<size_t> eligible;
        for (auto [i, member] : llvm::enumerate(members)) {
            auto& plan = plans[i];
            if (!member.input.Plan(true, plan) || plan.exit == LLVMBlockExit::Dispatch) continue;
            keys.push_back({plan.Key(true), member.entry.eip - stamp.guest_page});
            eligible.push_back(i);
        }
        if (keys.size() < 2 || !AcceptsJobs(epoch)) return std::nullopt;
        const auto compiled = Compiler().FindOrCompileRegion(keys, epoch);
        if (compiled.entries.empty()) return std::nullopt;
        RegionResult out{stamp, {}, {}, {}};
        const bool tracing = JitSchedulerTrace() != nullptr;
        llvm::raw_string_ostream encoded(out.recipe);
        if (tracing) encoded << char(llvm_block_cache::region_tag);
        for (auto [i, entry] : llvm::enumerate(compiled.entries)) {
            const auto index = eligible[compiled.included[i]];
            auto member = members[index].entry;
            const auto& plan = plans[index];
            member.handler = entry;
            if (tracing) WriteRegionMember(encoded, keys[compiled.included[i]]);
            out.entries.push_back(member);
            out.replay.emplace_back(plan.ReplayHandlers().begin(), plan.ReplayHandlers().end());
        }
        return out;
    }
};

std::optional<CapturedRegion> CapturePage(EmuState& state, BasicBlock& root) {
    auto stamp = state.mmu.page_region_stamp(root.start_eip());
    if (!stamp) return std::nullopt;
    auto& cache = state.mmu.code_cache();
    if (cache.page_regions->live.contains(stamp->guest_page)) return std::nullopt;
    const auto page = cache.page_to_blocks.find(stamp->host_page);
    if (page == cache.page_to_blocks.end()) return std::nullopt;

    constexpr size_t max_members = kMaxRegionMembers;
    constexpr size_t max_instructions = 192;
    std::vector<BasicBlock*> available;
    for (const auto& reference : page->second) {
        const auto& item = cache.block_entries[reference.block_index];
        auto* block = item.block;
        if (block && block->is_valid() && block->chain.has_region_context && item.host_pages.size() == 1 &&
            item.cache_key.host_page_base == stamp->host_page &&
            (block->start_eip() & ~mem::PAGE_MASK) == stamp->guest_page && block->end_eip > block->start_eip() &&
            ((block->end_eip - 1) & ~mem::PAGE_MASK) == stamp->guest_page)
            available.push_back(block);
    }
    if (std::ranges::find(available, &root) == available.end()) return std::nullopt;
    std::ranges::sort(available, {}, &BasicBlock::start_eip);
    std::vector<BasicBlock*> selected{&root};
    size_t instructions = root.inst_count();
    const auto add = [&](BasicBlock* next) {
        if (selected.size() == max_members || instructions + next->inst_count() > max_instructions ||
            std::ranges::find(selected, next) != selected.end())
            return;
        selected.push_back(next);
        instructions += next->inst_count();
    };
    for (size_t i = 0; i < selected.size() && selected.size() < max_members; ++i) {
        auto* from = selected[i];
        for (auto* candidate : available) {
            if (candidate->start_eip() == from->branch_target_eip || candidate->start_eip() == from->fallthrough_eip ||
                candidate->branch_target_eip == from->start_eip() || candidate->fallthrough_eip == from->start_eip())
                add(candidate);
        }
    }
    if (selected.size() < 2) return std::nullopt;
    std::ranges::sort(selected, {}, &BasicBlock::start_eip);

    CapturedRegion out{*stamp, {}};
    for (auto* block : selected) {
        const auto revision = block->invalidation_revision;
        auto input = CaptureBlock(state, *block);
        if (state.mmu.page_region_stamp(stamp->guest_page) != stamp) return std::nullopt;
        if (input && input->ops.size() == input->sentinel)
            out.members.push_back({{block->start_eip(), block, revision, nullptr}, std::move(*input)});
    }
    if (out.members.size() < 2) return std::nullopt;
    return out;
}

struct CompilationJob {
    BlockInput input;
    std::weak_ptr<AsyncResult> result;
    std::weak_ptr<AsyncSignals> signals;
    std::optional<CapturedRegion> region;
    uint64_t epoch;
    bool exact;
    JitClock::time_point queued{};
    uintptr_t mmu_identity = 0;
    uint16_t revision = 0;

    uint64_t Run() {
        auto control = signals.lock();
        if (!control || !control->Begin()) return 0;
        const bool tracing = queued != JitClock::time_point{};
        const auto start = tracing ? JitClock::now() : JitClock::time_point{};
        const auto cpu_start = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID);
        block_recipe::Plan plan;
        auto output = result.lock();
        auto compiled_region = output && region && AcceptsJobs(epoch) ? region->Compile(epoch) : std::nullopt;
        output.reset();
        const auto entry = compiled_region ? nullptr : Compile(plan);
        const auto cpu_ns = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID) - cpu_start;
        if (tracing) {
            traced_worker_cpu_ns.fetch_add(cpu_ns, std::memory_order_relaxed);
            std::fprintf(JitSchedulerTrace(), "job,%llu,%u,%u,%llu,%llu,%llu,%zu\n", (unsigned long long)mmu_identity,
                         input.eip, revision, (unsigned long long)Ns(start - queued),
                         (unsigned long long)Ns(JitClock::now() - start), (unsigned long long)cpu_ns,
                         entry ? plan.covered : 0);
        }
        if (auto output = result.lock()) {
            output->entry = entry;
            output->region = std::move(compiled_region);
            if (entry) output->replay.assign(plan.ReplayHandlers().begin(), plan.ReplayHandlers().end());
            output->done.store(true, std::memory_order_release);
            control->ready.store(true, std::memory_order_release);
        }
        control->End();
        return cpu_ns;
    }

    HandlerFunc Compile(block_recipe::Plan& plan) {
        if (result.expired() || !AcceptsJobs(epoch)) return nullptr;
        if (!input.Plan(exact, plan) || result.expired() || !AcceptsJobs(epoch)) return nullptr;
        return Compiler().FindOrCompile(plan.Key(exact), epoch);
    }
};

#endif

class CompilationPool {
    std::mutex mutex_;
    std::condition_variable queued_;
    struct Job {
        std::function<uint64_t()> run;
        int64_t reserved_cpu_ns = 0;
    };
    std::deque<Job> jobs_;
    bool stopping_ = false;
    const unsigned worker_count_;
    // Spend half of the available worker CPU over time, while allowing a
    // short burst to compile the first hot loop on both workers.
    static constexpr int64_t kBurstNs = 64'000'000;
    int64_t credit_ns_ = kBurstNs;
    int64_t job_estimate_ns_ = 16'000'000;
    JitClock::time_point budget_updated_ = JitClock::now();
    // Destroy these first so no worker can still touch the queue.
    std::vector<std::jthread> workers_;

    bool HasCredit() {
        const auto now = JitClock::now();
        const auto refill = std::min<int64_t>(Ns(now - budget_updated_), kBurstNs * 2);
        credit_ns_ = std::min(kBurstNs, credit_ns_ + refill * worker_count_ / 2);
        budget_updated_ = now;
        return credit_ns_ >= job_estimate_ns_ && jobs_.size() < worker_count_;
    }

    void Run() {
        for (;;) {
            Job job;
            {
                std::unique_lock lock(mutex_);
                queued_.wait(lock, [&] { return stopping_ || !jobs_.empty(); });
                if (stopping_) return;
                job = std::move(jobs_.front());
                jobs_.pop_front();
            }
            const auto cpu_ns = job.run();
            std::lock_guard lock(mutex_);
            credit_ns_ = std::min(kBurstNs, credit_ns_ + job.reserved_cpu_ns - int64_t(cpu_ns));
            if (cpu_ns) {
                job_estimate_ns_ =
                    std::clamp((7 * job_estimate_ns_ + int64_t(cpu_ns)) / 8, int64_t{1'000'000}, kBurstNs);
            }
        }
    }

public:
    explicit CompilationPool(unsigned count) : worker_count_(count) {
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

    bool CanSubmit() {
        std::unique_lock lock(mutex_, std::try_to_lock);
        return lock.owns_lock() && HasCredit();
    }

    bool Submit(std::function<uint64_t()> run) {
        {
            std::unique_lock lock(mutex_, std::try_to_lock);
            if (!lock.owns_lock() || !HasCredit()) return false;
            credit_ns_ -= job_estimate_ns_;
            jobs_.push_back({std::move(run), job_estimate_ns_});
        }
        queued_.notify_one();
        return true;
    }

#if FIBERCPU_GUEST_I386
    bool Submit(CompilationJob job) {
        return Submit([job = std::move(job)]() mutable { return job.Run(); });
    }
#endif
};

unsigned WorkerCount() { return Options().workers; }

CompilationPool& Workers() {
    static CompilationPool pool([] {
        // These statics must outlive the pool. Compiler construction
        // itself stays lazy so the VM thread does not parse bitcode here.
        (void)Storage();
#if FIBERCPU_GUEST_I386
        block_recipe::InitializeOperationLookup();
#endif
        return WorkerCount();
    }());
    return pool;
}

void FinishPendingFlush() {
    if (!flush_requested.load(std::memory_order_acquire)) return;
    std::unique_lock lock(execution_gate);
    if (flush_requested.load(std::memory_order_acquire)) Compiler().ResetGeneration();
}

#if FIBERCPU_GUEST_I386
enum class QueueResult { Submitted, Retry, Invalid };

QueueResult QueueBlock(EmuState* state, LLVMAsyncState& async, BasicBlock* block, bool exact) {
    auto result = std::make_shared<AsyncResult>();
    auto input = CaptureBlock(*state, *block);
    if (!input) return QueueResult::Invalid;
    auto region = RuntimeMode(*state) == JitMode::Region &&
                          !std::ranges::any_of(async.pending,
                                               [&](const PendingBlock& pending) {
                                                   return (pending.eip & ~mem::PAGE_MASK) ==
                                                          (block->start_eip() & ~mem::PAGE_MASK);
                                               })
                      ? CapturePage(*state, *block)
                      : std::nullopt;
    CompilationJob job{
        .input = std::move(*input),
        .result = result,
        .signals = async.signals,
        .region = std::move(region),
        .epoch = LLVMJitCacheEpoch(),
        .exact = exact,
    };
    auto* trace = JitSchedulerTrace();
    if (trace) {
        job.queued = JitClock::now();
        job.mmu_identity = state->mmu.core_handle()->identity;
        job.revision = block->invalidation_revision;
    }
    if (!Workers().Submit(std::move(job))) {
        return QueueResult::Retry;
    }
    async.pending.push_back({std::move(result), block, block->start_eip(), block->invalidation_revision});
    if (trace)
        std::fprintf(trace, "submit,%llu,%u,%u\n", (unsigned long long)state->mmu.core_handle()->identity,
                     block->start_eip(), block->invalidation_revision);
    return QueueResult::Submitted;
}

float EstimateBenefit(const BasicBlock& block, bool exact) {
    // Estimate from the published scalar identities. Native variants without
    // LLVM kernels can still have a supported generic fallback.
    static const auto generic_ids = [] {
        ankerl::unordered_dense::map<HandlerFunc, uint32_t> ids;
        for (const auto& variant : InstructionVariantCatalog()) {
            if (!variant.is_fallback && variant.opcode < 512) {
                ids.try_emplace(reinterpret_cast<HandlerFunc>(variant.handler),
                                FindScalarTrainingId(g_Handlers[variant.opcode]));
            }
        }
        return ids;
    }();
    unsigned covered = 0;
    unsigned memory_ops = 0;
    const auto* handlers = block.UnfusedHandlers();
    for (; covered < block.inst_count(); ++covered) {
        const auto decoded = block.ReadOp(covered);
        if (!block_recipe::CanPlanOp(decoded)) break;
        BoundScalarHandler binding{nullptr, FindScalarTrainingId(handlers[covered]), {}};
        if (block_recipe::FindOperation(decoded, binding, exact) == block_recipe::kInvalidOpId) {
            const auto alias = generic_ids.find(handlers[covered]);
            if (alias == generic_ids.end()) break;
            binding.id = alias->second;
            if (block_recipe::FindOperation(decoded, binding, exact) == block_recipe::kInvalidOpId) break;
        }
        memory_ops += HasMem(&decoded);
    }
    unsigned dispatches = 0;
    for (unsigned slot = 0; slot < covered;) {
        unsigned width = 1;
#if FIBERCPU_LOWER_OPERANDS
        width = BakedFusionWidth(block.LogicalOp(slot)->handler);
#endif
        if (slot + width > covered) break;
        ++dispatches;
        slot += width;
    }
    const bool whole_block =
        covered == block.inst_count() && block_recipe::FullBlockExit(block) != LLVMBlockExit::Dispatch;
    const unsigned saved = dispatches - (dispatches && !whole_block);
    // A small residual value permits wins from register/operand folding even
    // when the static interpreter already has a single fused dispatch.
    return float(1 + 4 * saved + covered / 4) / (8 + covered + 2 * memory_ops);
}

void RefreshCandidate(HotCandidate& candidate, JitClock::time_point now) {
    const auto counter = candidate.block->chain.llvm_hot_count;
    const auto visits = candidate.counter >= counter ? unsigned(candidate.counter) - counter : 0u;
    candidate.counter = counter;
    if (visits) candidate.last_active = now;
    const auto window = std::max(1ms, std::chrono::duration_cast<std::chrono::milliseconds>(now - candidate.sampled));
    const float current = float(visits) / window.count();
    candidate.rate = 0.5f * (candidate.rate + current);
    candidate.sampled = now;
    candidate.sampled_once = true;
}

void ScheduleCandidates(EmuState* state, LLVMAsyncState& async, JitClock::time_point now) {
    if (async.candidates.empty() || now < async.next_sample || state->mmu.code_cache_flush_pending()) return;
    async.next_sample = now + kSampleInterval;
    const auto retry = std::max(kMinCandidateRetryCount, LLVMJitHotThreshold(*state));
    std::erase_if(async.candidates, [&](HotCandidate& candidate) {
        auto* block = state->mmu.lookup_cached_block(candidate.eip);
        if (block != candidate.block || !block->is_valid()) return true;
        if (block->invalidation_revision != candidate.revision) {
            if (block->entry == block->FirstOp()->handler) block->chain.llvm_hot_count = retry;
            return true;
        }
        RefreshCandidate(candidate, now);
        if (now - candidate.last_active < kCandidateExpiry) return false;
        block->chain.llvm_hot_count = retry;
        return true;
    });

    const bool exact = RuntimeMode(*state) != JitMode::Generic;
    while (!async.candidates.empty() && async.pending.size() < WorkerCount() && Workers().CanSubmit()) {
        auto best = std::ranges::max_element(async.candidates, {}, &HotCandidate::Score);
        auto* block = best->block;
        if (!block_recipe::CanPromoteBlock(*state, *block)) {
            async.candidates.erase(best);
            continue;
        }
        const auto result = QueueBlock(state, async, block, exact);
        // The executable fetch may request a cache flush.
        if (state->mmu.code_cache_flush_pending()) return;
        if (result == QueueResult::Retry) break;
        block->chain.llvm_hot_count = 0;
        async.candidates.erase(best);
    }
}

void ObserveHotBlock(EmuState* state, BasicBlock* block, bool exact) {
    auto& async = state->mmu.core_handle()->llvm_async;
    if (!async) async = std::make_shared<LLVMAsyncState>();
    if (std::ranges::any_of(async->pending, [&](const PendingBlock& item) {
            return item.block == block && item.revision == block->invalidation_revision;
        }))
        return;
    const auto now = JitClock::now();
    auto existing = std::ranges::find(async->candidates, block, &HotCandidate::block);
    if (existing != async->candidates.end()) {
        if (existing->revision == block->invalidation_revision) {
            RefreshCandidate(*existing, now);
            block->chain.llvm_hot_count = existing->counter = kCandidateCounter;
            return;
        }
        async->candidates.erase(existing);
    }
    if (async->candidates.size() == kMaxCandidates) {
        const auto retry = std::max(kMinCandidateRetryCount, LLVMJitHotThreshold(*state));
        auto weakest = std::ranges::min_element(async->candidates, {}, &HotCandidate::Score);
        // Give blocks that just arrived one window to establish a rate.
        if (weakest->Score() > 1.f || !weakest->sampled_once) {
            block->chain.llvm_hot_count = retry;
            return;
        }
        auto* old = state->mmu.lookup_cached_block(weakest->eip);
        if (old == weakest->block && old->is_valid() && old->invalidation_revision == weakest->revision)
            old->chain.llvm_hot_count = retry;
        async->candidates.erase(weakest);
    }
    block->chain.llvm_hot_count = kCandidateCounter;
    async->candidates.push_back(
        {block, block->start_eip(), block->invalidation_revision, EstimateBenefit(*block, exact), now, now});
    ScheduleCandidates(state, *async, now);
}

#endif

}  // namespace

#if FIBERCPU_GUEST_AARCH64
namespace arm_jit {
bool Enabled(const EmuState& state) {
    const auto mode = RuntimeMode(state);
    return mode == JitMode::Exact || mode == JitMode::Generic || mode == JitMode::Region;
}

std::vector<aarch64::detail::HandlerFunc> Compile(std::span<const Member> members, uint64_t epoch) {
    return Compiler().FindOrCompileArm(members, epoch);
}

unsigned WorkerLimit() { return WorkerCount(); }

bool CanQueue() { return Workers().CanSubmit(); }

void TraceRegion(uint64_t page, size_t members) {
    if (auto* trace = JitSchedulerTrace())
        std::fprintf(trace, "arm_region_install,%llu,%zu\n", static_cast<unsigned long long>(page), members);
}

bool Queue(std::function<void()> work) {
    return Workers().Submit([work = std::move(work)] {
        const uint64_t start = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID);
        work();
        const auto elapsed = clock_gettime_nsec_np(CLOCK_THREAD_CPUTIME_ID) - start;
        if (JitSchedulerTrace()) traced_worker_cpu_ns.fetch_add(elapsed, std::memory_order_relaxed);
        return elapsed;
    });
}
}  // namespace arm_jit
#endif

ScopedLLVMJitExecution::ScopedLLVMJitExecution(const EmuState& state) {
    const auto mode = RuntimeMode(state);
    if (mode != JitMode::Exact && mode != JitMode::Generic && mode != JitMode::Region && mode != JitMode::Profile)
        return;

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

bool LLVMBlockJitSupported() { return pthread_jit_write_protect_supported_np(); }

bool ConfigureLLVMBlockJit(const SharedJitOptions& settings, std::string& error) {
    std::optional<std::string> pipeline;
    if (settings.pipeline) {
        llvm::StringRef input(settings.pipeline->data(), settings.pipeline->size());
        llvm::StringRef level = input;
        level.consume_front("-");
        const bool is_level =
            level == "O0" || level == "O1" || level == "O2" || level == "O3" || level == "Os" || level == "Oz";
        auto parsed = is_level ? ParseLLVMBlockPipeline(level, "") : ParseLLVMBlockPipeline("", input);
        if (!parsed) {
            error = "LlvmPipeline: " + llvm::toString(parsed.takeError());
            return false;
        }
        pipeline = std::move(*parsed);
    }

    auto& config = Configuration();
    std::lock_guard lock(config.mutex);
    if (config.frozen && settings.workers && *settings.workers != config.options.workers) {
        error = "JitWorkers conflicts with the existing process-wide worker count";
        return false;
    }
    if (config.frozen && pipeline && *pipeline != config.options.pipeline) {
        error = "LlvmPipeline conflicts with the existing process-wide LLVM pipeline";
        return false;
    }
    if (config.frozen && settings.fold_operand_loads &&
        *settings.fold_operand_loads != config.options.fold_operand_loads) {
        error = "LlvmFoldOperandLoads conflicts with the existing process-wide LLVM operand folding setting";
        return false;
    }
    // This is the only fallible step after checking the shared configuration.
    if (settings.arena_bytes > ExecutableArena::kMaxLLVMCapacity) {
        error = "JitArenaSize exceeds 2 GiB, the limit for the nearby LLVM code and data ranges";
        return false;
    }
    if (settings.arena_bytes && !ExecutableArena::Shared(settings.arena_bytes)) {
        error = "JitArenaSize conflicts with the existing arena or could not be allocated";
        return false;
    }
    if (settings.workers) config.options.workers = *settings.workers;
    if (pipeline) config.options.pipeline = std::move(*pipeline);
    if (settings.fold_operand_loads) config.options.fold_operand_loads = *settings.fold_operand_loads;
    config.frozen = true;
    return true;
}

bool LLVMPageRegionsEnabled(const EmuState& state) { return RuntimeMode(state) == JitMode::Region; }

#if FIBERCPU_GUEST_I386
bool LLVMWeightedProfileEnabled(const EmuState& state) {
    const auto* options = RunOptions(state);
    return options && options->profile;
}

std::shared_ptr<WeightedProfile> CreateLLVMWeightedProfile(bool same_page) {
    return std::make_shared<WeightedProfile>(same_page);
}

bool CaptureLLVMWeightedProfilePage(EmuState& state, BasicBlock& hot) {
    const auto* options = RunOptions(state);
    if (!options || !options->profile || !options->profile->same_page) return false;

    // Snapshot the membership at the threshold crossing. Recipe construction
    // can fetch bytes and run callbacks, so leave it to the dispatch boundary.
    auto& cache = state.mmu.code_cache();
    auto& pending = cache.pending_page_profile;
    pending.insert_or_assign(&hot, hot.invalidation_revision);
    const auto host = state.mmu.try_get_host_page_base(hot.start_eip());
    if (!host) return true;
    const auto page = cache.page_to_blocks.find(*host);
    if (page == cache.page_to_blocks.end()) return true;
    for (const auto& ref : page->second) {
        const auto& member = cache.block_entries[ref.block_index];
        auto* block = member.block;
        if (!block || !block->is_valid() || !block->has_unfused_handlers || member.cache_key.host_page_base != *host ||
            (member.cache_key.guest_eip >> 12) != (hot.start_eip() >> 12))
            continue;
        pending.insert_or_assign(block, block->invalidation_revision);
    }
    return true;
}

void RetireLLVMWeightedProfile(const mem::CodeCacheState& cache) {
    auto& profile = *cache.run_options->profile;
    std::lock_guard lock(profile.mutex);
    for (const auto* block : cache.all_blocks) {
        auto it = profile.blocks.find(block);
        if (it == profile.blocks.end()) continue;
        AddWeight(profile.retired[it->second], block->exec_count);
        profile.blocks.erase(it);
    }
}

bool WriteWeightedLLVMProfile(WeightedProfile& profile, const char* path, std::string& error) {
    RecipeWeights weights;
    {
        // Block counters and the decoded storage are borrowed only after all
        // active native runs have reached a safe point.
        std::unique_lock execution_lock(execution_gate);
        std::lock_guard profile_lock(profile.mutex);
        weights = profile.retired;
        for (const auto& [block, key] : profile.blocks) AddWeight(weights[key], block->exec_count);
    }
    std::vector<ProfileRecipe> recipes;
    recipes.reserve(weights.size());
    for (const auto& [key, weight] : weights) {
        if (weight || profile.same_page) recipes.push_back({key, std::max(weight, uint64_t{1})});
    }
    std::ranges::sort(recipes, [](const auto& a, const auto& b) {
        return a.weight != b.weight ? a.weight > b.weight : a.key < b.key;
    });
    auto temp = llvm::sys::fs::TempFile::create(llvm::Twine(path) + ".%%%%%%");
    if (!temp) {
        error = llvm::toString(temp.takeError());
        return false;
    }
    bool ok = true;
    {
        llvm::raw_fd_ostream output(temp->FD, false);
        WriteProfileHeader(output, true);
        for (const auto& recipe : recipes) {
            char count[8];
            llvm::support::endian::write64le(count, recipe.weight);
            output.write(count, sizeof(count));
            if (!llvm_block_cache::WriteRecord(output, recipe.key)) {
                error = "could not write a block recipe";
                ok = false;
                break;
            }
        }
        output.flush();
        if (output.has_error()) {
            error = output.error().message();
            output.clear_error();
            ok = false;
        }
    }
    if (!ok) {
        llvm::consumeError(temp->discard());
        return false;
    }
    if (auto failure = temp->keep(path)) {
        error = llvm::toString(std::move(failure));
        llvm::consumeError(temp->discard());
        return false;
    }
    return true;
}

#endif

uint16_t LLVMJitHotThreshold(const EmuState& state) {
    if (RuntimeMode(state) == JitMode::Off) return 0;
    if (const auto* options = RunOptions(state); options && options->hot_threshold) return *options->hot_threshold;
    static const auto threshold = ReadPositiveSetting("FIBERCPU_LLVM_JIT_HOT_THRESHOLD", kDefaultHotThreshold);
    return threshold;
}

#if FIBERCPU_GUEST_I386
void CompileHotLLVMBlock(EmuState* state, BasicBlock* block) {
    if (!block_recipe::CanPromoteBlock(*state, *block)) return;
    const auto mode = RuntimeMode(*state);
    if (mode == JitMode::Off) return;
    const bool exact = mode != JitMode::Generic;
    if ((mode == JitMode::Exact || mode == JitMode::Generic || mode == JitMode::Region) && WorkerCount()) {
        ObserveHotBlock(state, block, exact);
        return;
    }
    if (mode == JitMode::Region) {
        if (auto captured = CapturePage(*state, *block)) {
            if (auto region = captured->Compile(LLVMJitCacheEpoch()); region && PublishRegion(*state, *region)) return;
        }
    }
    block_recipe::Plan plan;
    if (!block_recipe::PlanBlock(*state, *block, exact, plan)) return;
    const auto key = plan.Key(exact);
    if (mode == JitMode::Profile) {
        TrackWeightedBlock(*state, *block, key);
        return;
    }
    if (mode == JitMode::LegacyProfile) {
        RecordBlock(key);
        return;
    }
    if (auto entry = Compiler().FindOrCompile(key, LLVMJitCacheEpoch()))
        block_recipe::InstallBlockHandler(*block, plan.ReplayHandlers(), entry);
}

void PollLLVMBlockCompilations(EmuState* state) {
    CollectPendingPageProfile(*state);
    const auto& async = state->mmu.core_handle()->llvm_async;
    if (!async) return;
    if (async->signals->ready.exchange(false, std::memory_order_acquire)) {
        std::erase_if(async->pending, [&](const PendingBlock& candidate) {
            if (!candidate.result->done.load(std::memory_order_acquire)) return false;
            if (candidate.result->region && PublishRegion(*state, *candidate.result->region)) return true;
            auto* trace = JitSchedulerTrace();
            auto* block = state->mmu.lookup_cached_block(candidate.eip);
            if (block != candidate.block) return true;
            if (block->invalidation_revision != candidate.revision) {
                if (block->entry == block->FirstOp()->handler) block->chain.llvm_hot_count = 1;
            } else if (candidate.result->region && block_recipe::CanPromoteBlock(*state, *block)) {
                block->chain.llvm_hot_count = 1;
            } else if (candidate.result->entry && block_recipe::CanPromoteBlock(*state, *block)) {
                block_recipe::InstallBlockHandler(*block, candidate.result->replay, candidate.result->entry);
                if (trace) {
                    state->mmu.code_cache().has_jit_trace = true;
                    block->chain.jit_execution_profile = true;
                    std::fprintf(trace, "install,%p,%u,%u,%llu\n", static_cast<void*>(block), candidate.eip,
                                 candidate.revision, (unsigned long long)block->exec_count);
                }
            }
            return true;
        });
    }
    if (!async->candidates.empty() && (++async->poll_ticks & 63) == 0)
        ScheduleCandidates(state, *async, JitClock::now());
}

void TraceLLVMJitRetirement(const BasicBlock& block) {
    if (auto* trace = JitSchedulerTrace())
        std::fprintf(trace, "retire,%p,%u,%u,%llu\n", static_cast<const void*>(&block), block.canonical_start_eip(),
                     block.invalidation_revision, (unsigned long long)block.exec_count);
}

void RetireLLVMJitTrace(const mem::CodeCacheState& cache) {
    for (const auto* block : cache.all_blocks) {
        if (block->chain.jit_execution_profile) TraceLLVMJitRetirement(*block);
    }
}

int BuildLinkedLLVMBlockCache(const char* profile_path, const char* cache_path, uint32_t limit, const char* opt_level,
                              const char* pass_pipeline, const char* region_profile) {
    if (!profile_path || !cache_path) return -1;
    auto input = llvm::MemoryBuffer::getFile(profile_path);
    if (!input) return -1;
    std::string_view profile = (*input)->getBuffer();
    bool weighted;
    std::vector<ProfileRecipe> recipes;
    if (!ReadProfileHeader(profile, weighted) || !ReadProfileRecipes(profile, weighted, recipes)) return -1;
    std::vector<std::string> regions;
    if (region_profile) {
        auto trace = llvm::MemoryBuffer::getFile(region_profile);
        if (!trace) return -1;
        const auto fingerprint = llvm::toHex(llvm::ArrayRef<uint8_t>(ProfileFingerprint()));
        auto lines = (*trace)->getBuffer();
        ankerl::unordered_dense::set<std::string> seen;
        while (!lines.empty()) {
            auto [line, rest] = lines.split('\n');
            lines = rest;
            if (!line.consume_front("region_recipe,")) continue;
            const auto [signature, encoded] = line.split(',');
            std::string record;
            if (signature != fingerprint || !llvm::tryGetFromHex(encoded, record)) return -1;
            if (seen.insert(record).second) regions.push_back(std::move(record));
        }
        if (regions.empty()) return -1;
        // The standalone fallback for each region member uses the same recipe.
        ankerl::unordered_dense::set<std::string_view> known;
        for (const auto& recipe : recipes) known.insert(recipe.key);
        std::vector<std::pair<uint32_t, std::string_view>> chunks;
        for (const auto& region : regions) {
            if (!ReadRegionRecord(region, chunks)) return -1;
            for (const auto& [offset, record] : chunks) {
                if (known.insert(record).second) recipes.push_back({record, 1});
            }
        }
    }
    auto pipeline = ParseLLVMBlockPipeline(opt_level ? opt_level : "", pass_pipeline ? pass_pipeline : "");
    if (!pipeline) {
        llvm::logAllUnhandledErrors(pipeline.takeError(), llvm::errs(), "LLVM block cache: ");
        return -1;
    }

    auto temp = llvm::sys::fs::TempFile::create(llvm::Twine(cache_path) + ".%%%%%%");
    if (!temp) {
        llvm::consumeError(temp.takeError());
        return -1;
    }
    int count;
    {
        llvm::raw_fd_ostream output(temp->FD, false);
        count = BlockCompiler::BuildLinkedCache(recipes, regions, output, limit, *pipeline);
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

#endif

uint64_t LLVMJitBlockCount() { return materializations.load(std::memory_order_relaxed); }

uint64_t LLVMJitLiveBlockCount() { return live_blocks.load(std::memory_order_relaxed); }

}  // namespace fiberish
