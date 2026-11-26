#include "jit/llvm_arm_jit.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <memory>
#include <optional>

#include "aarch64/decoder.h"
#include "aarch64/dispatch.h"
#include "aarch64/operation_binding.h"
#include "jit/llvm_arm_ir.h"
#include "jit/llvm_async_signals.h"
#include "jit/llvm_block_jit.h"
#include "state.h"

namespace fiberish::aarch64::detail {

extern "C" FIBERCPU_AARCH64_HANDLER_ABI uint64_t FiberArmJitRetry(A64_HANDLER_PARAMS) {
    if (current->OwningBlock()->IsValid()) {
        instr_limit -= current->Sentinel()->GuestIndex() - current->GuestIndex();
        ATTR_MUSTTAIL return current->handler(A64_HANDLER_ARGS);
    }
    ATTR_MUSTTAIL return FiberArmJitLeave(A64_HANDLER_ARGS);
}

extern "C" FIBERCPU_AARCH64_HANDLER_ABI uint64_t FiberArmJitRefetch(A64_HANDLER_PARAMS) {
    auto& frame = *state.active_run;
    frame.exit_edge = 2;
    if (current->OwningBlock()->IsValid()) {
        instr_limit -= current->Sentinel()->GuestIndex() - current->GuestIndex();
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, {Stop::Refetch});
    }

    // The instruction already ran. Refetch after it even if its slot is
    // unavailable (for example, after discarding an invalid source block).
    CommitDispatchState(state, state.pc, flags_cache A64_HANDLER_PIN_ARGS);
    frame.exit_op = current;
    frame.exit = {Stop::Refetch};
    return instr_limit - 1;
}

extern "C" FIBERCPU_AARCH64_HANDLER_ABI uint64_t FiberArmJitLeave(A64_HANDLER_PARAMS) {
    CommitDispatchState(state, state.pc, flags_cache A64_HANDLER_PIN_ARGS);
    auto& frame = *state.active_run;
    frame.exit_op = current;
    frame.exit = {Stop::Branch, Access::Execute, 0, state.pc};
    frame.exit_edge = 2;
    return instr_limit;
}

}  // namespace fiberish::aarch64::detail

namespace fiberish {
namespace {

using ArmBlock = aarch64::detail::BasicBlock;
using ArmHandler = aarch64::detail::HandlerFunc;
using namespace aarch64;
using namespace aarch64::detail;

constexpr size_t kMaxRegionMembers = PageRegion::MaxMembers;
constexpr size_t kMaxRegionInstructions = 192;
using JitClock = std::chrono::steady_clock;
using namespace std::chrono_literals;
constexpr size_t kMaxCandidates = 128;
constexpr uint16_t kCandidateCounter = UINT16_MAX;
constexpr uint16_t kMinCandidateRetryCount = 1024;
constexpr auto kSampleInterval = 2ms;
constexpr auto kCandidateExpiry = 60ms;

using ArmRegionStamp = mem::Mmu::ArmPageRegionStamp;
using ArmRegionEntry = mem::CodeCacheState::ArmBlocks::RegionEntry;

struct CapturedBlock {
    ArmBlock* block;
    uint64_t pc;
    uint16_t revision;
};

CapturedBlock CaptureBlock(ArmBlock& block) { return {&block, block.start_pc(), block.invalidation_revision}; }

struct CapturedRegion {
    ArmRegionStamp stamp;
    std::vector<ArmRegionEntry> entries;
    std::vector<arm_jit::Member> members;
};

struct ArmCompilation {
    uint64_t epoch;
    uint64_t generation;
    uintptr_t mmu_identity;
    uint64_t page;
    const uint8_t* backing;
    CapturedBlock root;
    arm_jit::Member input;
    std::optional<CapturedRegion> region;
    ArmHandler entry = nullptr;
    std::atomic<bool> done{false};
};

struct RegionCandidate {
    ArmBlock* block;
    std::array<uint16_t, 2> targets{UINT16_MAX, UINT16_MAX};
};

RegionCandidate RegionEdges(ArmBlock& block) {
    RegionCandidate candidate{&block};
    const auto last = DecodeFields(block.Word(block.inst_count() - 1));
    const auto binding = BindOperation(last);
    if (binding.refetch || binding.branch == BranchForm::Indirect) return candidate;
    const auto page = block.start_pc() & ~PageMask;
    const auto end = block.start_pc() + 4 * block.inst_count();
    const auto local = [page](uint64_t pc) {
        return pc - page <= PageMask ? uint16_t(pc - page) : uint16_t{UINT16_MAX};
    };
    if (binding.branch != BranchForm::None) candidate.targets[0] = local(end - 4 + last.immediate);
    if (binding.branch != BranchForm::Direct) candidate.targets[1] = local(end);
    return candidate;
}

arm_jit::Member CaptureMember(const ArmBlock& block) {
    arm_jit::Member member{uint16_t(block.start_pc() & PageMask), {}};
    member.steps.reserve(block.inst_count());
    const auto* op = block.FirstOp();
    for (unsigned i = 0; i < block.inst_count(); ++i) {
        const auto word = block.Word(i);
        if (op->GuestIndex() == i) {
            member.steps.push_back({word, uint8_t(1 + op->Has(HasDataSlot))});
            op = op->NextOp();
        } else {
            member.steps.push_back({word, 0});
        }
    }
    return member;
}

std::optional<CapturedRegion> CapturePage(const EmuState& engine, ArmBlock& root) {
    if (!LLVMPageRegionsEnabled(engine) || !root.HasRegionContext()) return std::nullopt;
    const auto page = root.start_pc() & ~PageMask;
    const auto stamp = engine.mmu.arm_page_region_stamp(page);
    if (!stamp || stamp->backing != reinterpret_cast<uintptr_t>(root.executable_page)) return std::nullopt;
    const auto& cache = *engine.mmu.code_cache().arm_blocks;
    if (cache.regions.contains(page)) return std::nullopt;
    const auto host = cache.pages.find(stamp->backing);
    if (host == cache.pages.end()) return std::nullopt;

    std::vector<RegionCandidate> available;
    for (auto* block : host->second)
        if (block->IsValid() && block->HasRegionContext() && (block->start_pc() & ~PageMask) == page)
            available.push_back(RegionEdges(*block));

    const auto address = [](const RegionCandidate& item) { return item.block->start_pc(); };
    const auto has_edge = [](const RegionCandidate& source, const RegionCandidate& target) {
        return std::ranges::find(source.targets, target.block->start_pc() & PageMask) != source.targets.end();
    };
    std::ranges::sort(available, {}, address);
    std::vector<RegionCandidate> selected{RegionEdges(root)};
    size_t instructions = root.inst_count();
    for (size_t i = 0; i < selected.size() && selected.size() < kMaxRegionMembers; ++i) {
        const auto source = selected[i];
        for (const auto& candidate : available) {
            if (selected.size() == kMaxRegionMembers) break;
            if (instructions + candidate.block->inst_count() > kMaxRegionInstructions ||
                std::ranges::find(selected, candidate.block, &RegionCandidate::block) != selected.end())
                continue;
            if (!has_edge(source, candidate) && !has_edge(candidate, source)) continue;
            selected.push_back(candidate);
            instructions += candidate.block->inst_count();
        }
    }
    if (selected.size() < 2) return std::nullopt;
    std::ranges::sort(selected, {}, address);

    CapturedRegion region{*stamp, {}, {}};
    for (const auto& candidate : selected) {
        auto* block = candidate.block;
        region.entries.push_back({block, block->start_pc(), block->invalidation_revision, nullptr});
        region.members.push_back(CaptureMember(*block));
    }
    return region;
}

void Capture(ArmCompilation& job, const EmuState& engine, ArmBlock& root) {
    job.epoch = LLVMJitCacheEpoch();
    job.generation = engine.mmu.code_cache().generation;
    job.mmu_identity = engine.mmu.core_handle()->identity;
    job.page = root.start_pc() & ~PageMask;
    job.backing = root.executable_page;
    job.root = CaptureBlock(root);
    job.input = CaptureMember(root);
    job.region = CapturePage(engine, root);
}

ArmHandler CompileBlock(const arm_jit::Member& member, uint64_t epoch) {
    const auto entries = arm_jit::Compile(std::span(&member, 1), epoch);
    return entries.empty() ? nullptr : entries.front();
}

bool CompileRegion(CapturedRegion& region, uint64_t epoch) {
    size_t count = 0;
    for (size_t i = 0; i < region.members.size(); ++i) {
        if (!std::ranges::all_of(region.members[i].steps,
                                 [](const auto& step) { return arm_jit::CanLower(step.word); }))
            continue;
        if (count != i) {
            region.members[count] = std::move(region.members[i]);
            region.entries[count] = region.entries[i];
        }
        ++count;
    }
    if (count < 2) return false;
    region.members.resize(count);
    region.entries.resize(count);
    const auto entries = arm_jit::Compile(region.members, epoch);
    if (entries.size() != count) return false;
    for (size_t i = 0; i < count; ++i) region.entries[i].entry = entries[i];
    return true;
}

void Compile(ArmCompilation& job) {
    if (job.region && CompileRegion(*job.region, job.epoch)) return;
    job.region.reset();
    job.entry = CompileBlock(job.input, job.epoch);
}

bool IsCurrent(const EmuState& engine, const CapturedBlock& member, const uint8_t* backing) {
    auto* block = engine.mmu.lookup_arm_block(member.pc, backing);
    return block == member.block && block->IsValid() && block->invalidation_revision == member.revision;
}

bool CanPromote(const ArmBlock& block) { return block.header.entry == block.FirstOp()->handler; }

void Publish(EmuState& engine, const ArmCompilation& job) {
    if (job.epoch != LLVMJitCacheEpoch() || job.generation != engine.mmu.code_cache().generation ||
        job.mmu_identity != engine.mmu.core_handle()->identity || engine.mmu.code_cache_flush_pending())
        return;
    if (job.region && engine.mmu.publish_arm_page_region(job.region->stamp, job.region->entries)) {
        arm_jit::TraceRegion(job.page, job.region->entries.size());
        return;
    }
    if (IsCurrent(engine, job.root, job.backing) && CanPromote(*job.root.block) &&
        reinterpret_cast<const uint8_t*>(engine.mmu.try_arm_executable_page(job.page)) == job.backing) {
        if (job.entry) {
            job.root.block->header.chain.llvm_hot_count = 0;
            job.root.block->header.entry = job.entry;
        } else if (job.region) {
            job.root.block->header.chain.llvm_hot_count = 1;
        }
    }
}

struct HotCandidate {
    CapturedBlock target;
    const uint8_t* backing;
    float benefit_per_cost;
    JitClock::time_point sampled;
    JitClock::time_point last_active;
    uint16_t counter = kCandidateCounter;
    float rate = 1;
    bool sampled_once = false;

    float Score() const { return rate * benefit_per_cost; }

    void Sample(JitClock::time_point now) {
        const auto current = target.block->header.chain.llvm_hot_count;
        const unsigned visits = counter >= current ? unsigned(counter) - current : 0;
        counter = current;
        if (visits) last_active = now;
        const auto window = std::max(1ms, std::chrono::duration_cast<std::chrono::milliseconds>(now - sampled));
        rate = 0.5f * (rate + float(visits) / window.count());
        sampled = now;
        sampled_once = true;
    }
};

float EstimateBenefit(const ArmBlock& block) {
    unsigned covered = 0;
    unsigned memory_ops = 0;
    for (; covered < block.inst_count(); ++covered) {
        const auto op = DecodeFields(block.Word(covered));
        if (BindOperation(op).id == OperationId::Unsupported) break;
        memory_ops += (op.kind >= Kind::LoadStoreImmediate && op.kind <= Kind::LoadStoreAtomic);
    }
    unsigned dispatches = 0;
    const auto* op = block.FirstOp();
    while (op->GuestIndex() < covered) {
        unsigned span = 1;
        if (op->Has(Fused)) span = block.FusedBinding(op->GuestIndex()).length;
        const auto* last = op->LastOp(span);
        if (last->GuestIndex() >= covered) break;
        ++dispatches;
        op = last->NextOp();
    }
    const unsigned saved = dispatches - (dispatches && covered != block.inst_count());
    return float(1 + 4 * saved + covered / 4) / (8 + covered + 2 * memory_ops);
}

uint16_t RetryCount(const EmuState& engine) { return std::max(kMinCandidateRetryCount, LLVMJitHotThreshold(engine)); }

}  // namespace

struct LLVMArmJobs {
    std::shared_ptr<AsyncSignals> signals = std::make_shared<AsyncSignals>();
    std::vector<std::shared_ptr<ArmCompilation>> pending;
    std::vector<HotCandidate> candidates;
    JitClock::time_point next_sample{};

    ~LLVMArmJobs() {
        pending.clear();
        signals->Close();
    }
};

namespace {

bool QueueBlock(LLVMArmJobs& jobs, const EmuState& engine, ArmBlock& block) {
    auto job = std::make_shared<ArmCompilation>();
    Capture(*job, engine, block);
    const auto work = [result = std::weak_ptr(job), signals = jobs.signals] {
        if (!signals->Begin()) return;
        if (auto output = result.lock()) {
            Compile(*output);
            output->done.store(true, std::memory_order_release);
            signals->ready.store(true, std::memory_order_release);
        }
        signals->End();
    };
    if (!arm_jit::Queue(work)) return false;
    jobs.pending.push_back(std::move(job));
    block.header.chain.llvm_hot_count = 0;
    return true;
}

bool PagePending(const LLVMArmJobs& jobs, const ArmBlock& block) {
    return std::ranges::any_of(jobs.pending, [&](const auto& job) {
        return job->backing == block.executable_page && job->page == (block.start_pc() & ~PageMask);
    });
}

void ScheduleCandidates(EmuState& engine, LLVMArmJobs& jobs, JitClock::time_point now) {
    if (jobs.candidates.empty() || now < jobs.next_sample || engine.mmu.code_cache_flush_pending()) return;
    jobs.next_sample = now + kSampleInterval;
    const auto retry = RetryCount(engine);
    std::erase_if(jobs.candidates, [&](HotCandidate& candidate) {
        if (!IsCurrent(engine, candidate.target, candidate.backing) || !CanPromote(*candidate.target.block))
            return true;
        candidate.Sample(now);
        if (now - candidate.last_active < kCandidateExpiry) return false;
        candidate.target.block->header.chain.llvm_hot_count = retry;
        return true;
    });

    const auto region = LLVMPageRegionsEnabled(engine);
    while (!jobs.candidates.empty() && jobs.pending.size() < arm_jit::WorkerLimit() && arm_jit::CanQueue()) {
        auto best = jobs.candidates.end();
        for (auto it = jobs.candidates.begin(); it != jobs.candidates.end(); ++it) {
            if (region && PagePending(jobs, *it->target.block)) continue;
            if (best == jobs.candidates.end() || it->Score() > best->Score()) best = it;
        }
        if (best == jobs.candidates.end() || !QueueBlock(jobs, engine, *best->target.block)) break;
        jobs.candidates.erase(best);
    }
}

}  // namespace

void PollLLVMArmCompilations(EmuState& engine) {
    auto& jobs = engine.mmu.code_cache().llvm_arm_jobs;
    if (!jobs) return;
    if (jobs->signals->ready.exchange(false, std::memory_order_acquire)) {
        std::erase_if(jobs->pending, [&](const auto& job) {
            if (!job->done.load(std::memory_order_acquire)) return false;
            Publish(engine, *job);
            return true;
        });
    }
    if (!jobs->candidates.empty()) ScheduleCandidates(engine, *jobs, JitClock::now());
}

void ObserveLLVMArmHotBlock(EmuState& engine, ArmBlock& block) {
    if (!IsCurrent(engine, CaptureBlock(block), block.executable_page) || !CanPromote(block)) return;
    if (engine.mmu.code_cache_flush_pending()) {
        block.header.chain.llvm_hot_count = RetryCount(engine);
        return;
    }
    if (!arm_jit::WorkerLimit()) {
        ArmCompilation job;
        Capture(job, engine, block);
        Compile(job);
        Publish(engine, job);
        return;
    }

    auto& jobs = engine.mmu.code_cache().llvm_arm_jobs;
    if (!jobs) jobs = std::make_shared<LLVMArmJobs>();
    if (std::ranges::any_of(jobs->pending, [&](const auto& job) { return job->root.block == &block; })) return;
    const auto now = JitClock::now();
    const auto existing =
        std::ranges::find_if(jobs->candidates, [&](const auto& item) { return item.target.block == &block; });
    if (existing != jobs->candidates.end()) {
        existing->Sample(now);
        block.header.chain.llvm_hot_count = existing->counter = kCandidateCounter;
    } else {
        if (jobs->candidates.size() == kMaxCandidates) {
            const auto retry = RetryCount(engine);
            auto weakest = std::ranges::min_element(jobs->candidates, {}, &HotCandidate::Score);
            if (weakest->Score() > 1.f || !weakest->sampled_once) {
                block.header.chain.llvm_hot_count = retry;
                ScheduleCandidates(engine, *jobs, now);
                return;
            }
            if (IsCurrent(engine, weakest->target, weakest->backing) && CanPromote(*weakest->target.block))
                weakest->target.block->header.chain.llvm_hot_count = retry;
            jobs->candidates.erase(weakest);
        }
        block.header.chain.llvm_hot_count = kCandidateCounter;
        jobs->candidates.push_back({CaptureBlock(block), block.executable_page, EstimateBenefit(block), now, now});
    }
    PollLLVMArmCompilations(engine);
}

void InitializeLLVMArmBlock(EmuState& engine, ArmBlock& block) {
    if (arm_jit::Enabled(engine) && arm_jit::CanLower(block.Word(0)))
        block.header.chain.llvm_hot_count = LLVMJitHotThreshold(engine);
}

}  // namespace fiberish
