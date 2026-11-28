#include "aarch64.h"

#include <array>
#include <cassert>
#include <cstring>
#include <limits>
#include <utility>

#if FIBERCPU_AARCH64_UNMANAGED_CACHE
#include <memory_resource>
#include <optional>
#include <unordered_map>
#endif

#include "compare_branch.h"
#include "flag_liveness.h"
#include "lowering.h"

namespace fiberish::aarch64 {
const detail::BasicBlock detail::BasicBlock::Invalid{};

namespace detail {

namespace {

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
StepResult ExecuteMemorySlow(State& state, const Memory& memory, uint32_t word) {
    const auto op = DecodeFields(word);
    switch (op.kind) {
        case Kind::LoadStoreImmediate:
            return LoadStore<Kind::LoadStoreImmediate>(state, memory, op);
        case Kind::LoadStoreRegister:
            return LoadStore<Kind::LoadStoreRegister>(state, memory, op);
        case Kind::LoadStorePair:
            return LoadStore<Kind::LoadStorePair>(state, memory, op);
        case Kind::LoadLiteral:
            return LoadStore<Kind::LoadLiteral>(state, memory, op);
        case Kind::LoadStoreExclusive:
            return ExclusiveMemory(state, memory, op);
        case Kind::LoadStoreAtomic:
            return AtomicMemory(state, memory, op);
        case Kind::System:
            return ZeroCacheLineSlow(state, memory, op);
        case Kind::Vector: {
#if FIBERCPU_AARCH64_MICRO_TLB
            uint64_t utlb_tags = 0;
            uintptr_t utlb_addend = 0;
#endif
            return VectorStructure<false>(state, &memory, op, ScalarRegisters{state}, nullptr A64_UTLB_ARGS);
        }
        default:
            return Undefined(state.pc);
    }
}

}  // namespace

// Memory replay publishes all architectural state before any callback and
// reloads it afterward. Only a callback-free access resumes the span.
#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_HANDLER_ABI int64_t MemoryOpGeneric(A64_HANDLER_PARAMS) {
    CommitDispatchState(state, current->GetStartPc(state), flags_cache A64_HANDLER_PIN_ARGS);
    const auto& memory = state.active_run->memory;
#ifdef ENABLE_TLB_STATS
    if (auto* stats = memory.dispatch_stats) ++stats->memory_replays;
#endif
    const auto word = state.last_block->Word(current->GuestIndex());
    auto step = CompleteStep(state, current, ExecuteMemorySlow(state, memory, word));
    A64_UTLB_INVALIDATE();
    flags_cache = state.nzcv;
    ReloadDispatchPins(state A64_HANDLER_PIN_ARGS);
    if (step.reason != Stop::Continue) {
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, step);
    }
    A64_DISPATCH_NEXT(current->NextOp());
}

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_HANDLER_ABI int64_t ResolveBranchTargetSlow(A64_HANDLER_PARAMS) {
    auto& frame = *state.active_run;
    const auto target_pc = frame.exit_target;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    const auto& memory = frame.memory;
    if (instr_limit + frame.budget_reserve >= 0
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
        && memory.lookup_cached_block
#endif
    ) {
#ifdef ENABLE_TLB_STATS
        auto* stats = memory.dispatch_stats;
#endif
        const auto* target = (target_pc & 3) || target_pc >= mem::ArmTaskSize
                                 ? nullptr
                                 : memory.lookup_cached_block(memory.context, target_pc, nullptr);
        if (target && target->IsValid()) {
            *frame.exit_link = BlockLink(target);
            if (instr_limit >= 0 || instr_limit + frame.budget_reserve + 1 >= target->inst_count()) {
#ifdef ENABLE_TLB_STATS
                if (stats) {
                    ++stats->resolved_links;
                    stats->cross_page_links += ((state.last_block->start_pc() ^ target_pc) & ~PageMask) != 0;
                }
#endif
                frame.exit_link = nullptr;
                state.last_block = target;
#if FIBERCPU_LLVM_JIT
                CountLLVMBlockEntry(state, *target, target->header.chain);
#endif
                ATTR_MUSTTAIL return target->header.entry(state, target->FirstOp(),
                                                          instr_limit - target->inst_count() A64_UTLB_ARGS,
                                                          flags_cache A64_HANDLER_PIN_ARGS);
            }
            // A known target that does not fit is a budget exit.
#ifdef ENABLE_TLB_STATS
            stats = nullptr;
#endif
        }
#ifdef ENABLE_TLB_STATS
        if (stats) ++stats->link_misses;
#endif
    }
#endif
    // The caller has charged only the retired prefix, including the branch.
    CommitDispatchState(state, target_pc, flags_cache A64_HANDLER_PIN_ARGS);
    frame.exit_op = current;
    frame.exit = {};
    return instr_limit;
}

FIBERCPU_AARCH64_HANDLER_ABI int64_t OpExitBlock(A64_HANDLER_PARAMS) {
    const uint64_t target = state.last_block->end_pc();
    A64_RESOLVE_BRANCH_TARGET_INLINE(current->payload, target);
}

FIBERCPU_AARCH64_HANDLER_ABI int64_t OpExitSingleStep(A64_HANDLER_PARAMS) {
    (void)current;
    CommitDispatchState(state, state.last_block->end_pc(), flags_cache A64_HANDLER_PIN_ARGS);
    auto& frame = *state.active_run;
    frame.exit_op = nullptr;
    frame.exit = {};
    return instr_limit;
}

}  // namespace detail

namespace {

using namespace detail;

using Resolver = decltype(Profile::resolve);

constexpr bool UsesRawWordOperands(Kind kind) {
    return kind == Kind::System || kind == Kind::ScalarFloat || kind == Kind::Vector;
}

struct DecodedOpBinding {
    DecodedInstruction op;
    HandlerFunc handler = nullptr;
    uint8_t ordinal = 0;
    uint8_t span = 1;
    uint8_t flags = 0;
    bool can_follow = false;
    bool nop = false;

    bool Has(SlotFlag flag) const { return (flags & flag) != 0; }
};

struct CacheEntry {
    DecodedOpBinding slot;
    Resolver resolver = nullptr;
};

// The cache key is the word read from executable memory, so writes, remaps,
// permission changes and different guest address spaces need no invalidation.
constexpr unsigned CacheBits = 10;
thread_local std::array<CacheEntry, 1 << CacheBits> DecodeCache;

bool IsInteger(Kind kind) { return kind > Kind::Undefined && kind <= Kind::Multiply; }

DecodedOpBinding BindDecodedOp(uint32_t word, const Profile& profile) {
    DecodedOpBinding slot;
    slot.op = DecodeFields(word);
    const auto kind = slot.op.kind;
    const auto custom = profile.resolve ? profile.resolve(slot.op, false) : nullptr;
    slot.nop = !custom && word == NopWord;
    HandlerBinding binding{};
    if (custom) {
        binding.handler = custom;
    } else {
        binding = SelectPinHandler(Lower(slot.op), slot.op);
    }
    slot.handler = binding.handler;
    assert(slot.handler && "Instructions must provide a threaded handler");

    const bool branch = kind >= Kind::BranchImmediate && kind <= Kind::TestBranch;
    const bool data_transfer = kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral;
    const bool arithmetic = kind == Kind::ScalarFloat || (kind == Kind::Vector && (word & 0xbe000000) != 0x0c000000);
    slot.can_follow = !branch && (IsInteger(kind) || data_transfer || arithmetic || slot.nop || binding.can_follow);
    if (slot.can_follow || branch) slot.flags |= RetainsPage;
    if (branch) slot.flags |= HasBranchLink;
    if (HasExtraOperandSlot(kind)) slot.flags |= HasDataSlot;
    return slot;
}

const DecodedOpBinding& DecodeOpBinding(uint32_t word, const Profile& profile) {
    const auto index = (word * 0x9e3779b1u) >> (32 - CacheBits);
    auto& entry = DecodeCache[index];
    if (!entry.slot.handler || entry.slot.op.word != word || entry.resolver != profile.resolve) {
        entry.slot = BindDecodedOp(word, profile);
        entry.resolver = profile.resolve;
    }
    return entry.slot;
}

#if FIBERCPU_AARCH64_UNMANAGED_CACHE
struct LocalBlockCache {
    std::pmr::monotonic_buffer_resource pool;
    std::unordered_map<uint64_t, BasicBlock*> by_pc;
};
#endif

struct RunScope {
    static inline thread_local bool active = false;
    State& state;
    detail::RunFrame* saved;
    const BasicBlock* saved_block;
    const mem::SoftTlb* saved_data_tlb;
    const bool outer = !std::exchange(active, true);

    RunScope(State& state, detail::RunFrame& frame)
        : state(state),
          saved(std::exchange(state.active_run, &frame)),
          saved_block(std::exchange(state.last_block, nullptr)) {
        static const mem::SoftTlb empty_tlb;
        saved_data_tlb = std::exchange(state.data_tlb, frame.memory.data_tlb ? frame.memory.data_tlb : &empty_tlb);
    }

    ~RunScope() {
        state.active_run = saved;
        state.last_block = saved_block;
        state.data_tlb = saved_data_tlb;
        if (outer) active = false;
    }
};

std::byte* WriteDecodedSlot(std::byte* bytes, const DecodedOpBinding& binding, uint64_t block_pc,
                            unsigned block_inst_count) {
    const auto& op = binding.op;
    const auto operands = UsesRawWordOperands(op.kind) ? uint64_t{op.word} : PackOpOperands(op);
    const DecodedOp slot{
        .handler = binding.handler,
        .payload = DecodedOp::Pack(operands, binding.ordinal, binding.flags, binding.span,
                                   IsConditionalBranch(op.kind) ? block_inst_count - binding.ordinal - 1 : 0),
    };
    if (slot.Has(HasDataSlot)) {
        const auto primary = HasBlockTarget(op.kind)           ? op.target_pc
                             : op.kind == Kind::BranchRegister ? block_pc + uint64_t{slot.GuestIndex()} * 4
                                                               : op.immediate;
        const auto secondary = slot.Has(HasBranchLink)     ? BlockLink(&BasicBlock::Invalid)
                               : op.kind == Kind::Bitfield ? op.mask
                                                           : 0;
        new (bytes) DecodedOpWithDataSlot{slot, primary, secondary};
        return bytes + sizeof(DecodedOpWithDataSlot);
    }
    new (bytes) DecodedOp(slot);
    return bytes + sizeof(DecodedOp);
}

const BasicBlock* DecodeBlock(uint64_t pc, const uint8_t* executable, const Profile& profile, const Memory& memory
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
                              ,
                              LocalBlockCache* local
#endif
) {
    const auto* code = executable + (pc & PageMask);
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
    const bool managed = memory.lookup_cached_block != nullptr;
    auto* cached = managed ? memory.lookup_cached_block(memory.context, pc, executable) : nullptr;
    if (!managed) {
        const auto it = local->by_pc.find(pc);
        if (it != local->by_pc.end()) cached = it->second;
    }
#else
    const auto* cached = memory.lookup_cached_block(memory.context, pc, executable);
#endif
    const bool valid = cached && cached->IsValid() && cached->executable_page == executable;
    if (valid
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
        && (managed || MatchesWords(*cached, code))
#endif
    ) {
#ifdef ENABLE_TLB_STATS
        if (auto* stats = memory.dispatch_stats) ++stats->block_lookup_hits;
#endif
        return cached;
    }
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
    if (valid) cached->Invalidate();
#endif

#ifdef ENABLE_TLB_STATS
    if (auto* stats = memory.dispatch_stats) ++stats->block_lookup_misses;
#endif
    const unsigned limit = std::min(BlockMax, (PageBytes - unsigned(pc & PageMask)) / 4);
    std::array<DecodedOpBinding, BlockMax> decoded;
    std::array<uint32_t, BlockMax> words;
    unsigned count = 0;
    unsigned slot_count = 0;
    unsigned fallthrough_limit = 3;
#if FIBERCPU_LLVM_JIT
    // Keep region members short so hot pages can compile during a guest run.
    if (memory.retain_region_context) fallthrough_limit = 1;
#endif
    unsigned continued_branches = 0;
    while (count < limit) {
        uint32_t raw_word;
        std::memcpy(&raw_word, code + count * 4, sizeof(raw_word));
        words[count] = raw_word;
        auto word = raw_word;
        if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
        const auto& binding = DecodeOpBinding(word, profile);
        const auto ordinal = count++;
        if (binding.nop && slot_count) continue;

        auto& slot = decoded[slot_count++];
        slot = binding;
        slot.ordinal = ordinal;
        BindBlockOperands(slot.op, pc + ordinal * 4);
        if (!slot.can_follow) {
            if (!IsConditionalBranch(slot.op.kind) || continued_branches == fallthrough_limit) break;
            ++continued_branches;
        }
    }

    uint32_t dead = 0;
#if !defined(FIBERCPU_AARCH64_DFE) || FIBERCPU_AARCH64_DFE
    uint32_t live = FlagMask;
    for (unsigned i = slot_count; i-- > 0;) {
        const auto& slot = decoded[i];
        if (!slot.nop && OmitNZCV(slot.op, live)) dead |= uint32_t{1} << i;
    }
#endif
    std::array<uint32_t, BlockMax> instructions;
    if (profile.resolve_pair || profile.resolve_sequence) {
        for (unsigned i = 0; i < slot_count; ++i) instructions[i] = CanonicalizeBakedWord(decoded[i].op);
    }
    unsigned following = 0;
    unsigned units = 0;
    for (unsigned i = slot_count; i-- > 0;) {
        auto& slot = decoded[i];
        if (i + 1 < slot_count && decoded[i + 1].ordinal != slot.ordinal + 1) following = 0;
        const uint32_t dead_from_here = dead >> i;
        if (slot.can_follow && following > 1 && profile.resolve_sequence) {
            unsigned length = 0;
            if (const auto fusion =
                    profile.resolve_sequence(instructions.data() + i, following + 1, &length, dead_from_here);
                fusion && length >= 3 && length <= following + 1) {
                slot.handler = fusion;
                slot.span = uint8_t(length);
            }
        }
        if (slot.span == 1 && slot.can_follow && IsInteger(slot.op.kind) && following > 0 && profile.resolve_pair) {
            if (const auto fusion = profile.resolve_pair(instructions[i], instructions[i + 1], dead_from_here)) {
                slot.handler = fusion;
                slot.span = 2;
            }
        }
        if (slot.span == 1) {
            if (dead_from_here & 1) {
                auto handler = profile.resolve ? profile.resolve(slot.op, true) : nullptr;
                if (!handler) handler = SelectPinHandler(LowerDeadNZCV(slot.op), slot.op).handler;
                if (handler) slot.handler = handler;
            } else if (following == 1 && i + 1 < slot_count && slot.can_follow) {
                if (const auto pair = LowerCompareBranch(slot.op, decoded[i + 1].op)) slot.handler = pair;
            }
        }
        following = slot.can_follow ? following + 1 : 1;
        units += 1 + slot.Has(HasDataSlot);
    }

#if FIBERCPU_LLVM_JIT
    const bool retain_region_context = memory.retain_region_context;
#else
    const bool retain_region_context = false;
#endif
    const auto size = BasicBlock::CalculateSize(units, count, retain_region_context);
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
    auto* storage = memory.allocate_block ? memory.allocate_block(memory.context, size, pc)
                                          : local->pool.allocate(size, alignof(BasicBlock));
#else
    auto* storage = memory.allocate_block(memory.context, size, pc);
#endif
    if (!storage) return nullptr;
    auto& block = *new (storage) BasicBlock;
    block.header.chain = BasicBlockChainPrefix::Pack(pc, count, retain_region_context);
    block.sentinel_slot_index = uint8_t(units);
    block.executable_page = executable;

    auto* bytes = reinterpret_cast<std::byte*>(&block + 1);
    for (unsigned i = 0; i < slot_count; ++i) bytes = WriteDecodedSlot(bytes, decoded[i], pc, count);
    new (bytes) DecodedOp{.handler = GetExitHandlerFallthrough(), .payload = BlockLink(&BasicBlock::Invalid)};
    if (retain_region_context) block.SetRegionContext(nullptr);
    std::memcpy(const_cast<uint32_t*>(block.Words()), words.data(), count * sizeof(uint32_t));
    block.header.entry = block.FirstOp()->handler;
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
    if (memory.cache_block)
        memory.cache_block(memory.context, &block);
    else
        local->by_pc[pc] = &block;
#else
    memory.cache_block(memory.context, &block);
#endif
    return &block;
}

struct SingleStepResult {
    StepResult step{};
    unsigned retired = 0;
};

// Build a stack-allocated 1-instruction BasicBlock with zero pool allocation
// and execute it through the unified threaded handler ABI.
SingleStepResult ExecuteSingleInstructionOnStack(State& state, RunFrame& frame, uint64_t pc, uint32_t word,
                                                 const DecodedOpBinding& cached_binding) {
    auto binding = cached_binding;
    binding.ordinal = 0;
    binding.span = 1;
    BindBlockOperands(binding.op, pc);

    const unsigned units = 1u + binding.Has(HasDataSlot);
    alignas(BasicBlock) std::byte storage[BasicBlock::CalculateSize(2, 1, false)];
    auto& block = *new (storage) BasicBlock;
    // Leave Valid clear so nothing can ever link to a stack-allocated block.
    block.header.chain = BasicBlockChainPrefix::Pack(pc, 1, false, false);
    block.sentinel_slot_index = uint8_t(units);

    auto* bytes = WriteDecodedSlot(reinterpret_cast<std::byte*>(&block + 1), binding, pc, 1);
    new (bytes) DecodedOp{.handler = &OpExitSingleStep, .payload = BlockLink(&BasicBlock::Invalid)};
    auto raw_word = word;
    if constexpr (std::endian::native == std::endian::big) raw_word = std::byteswap(raw_word);
    std::memcpy(const_cast<uint32_t*>(block.Words()), &raw_word, sizeof(raw_word));

    const auto* saved_block = std::exchange(state.last_block, &block);
    frame.exit_link = nullptr;
    frame.exit_op = nullptr;
    frame.exit = {};
    // Keep both instr_limit < 0 and instr_limit + budget_reserve < 0 so neither
    // TryChainBlock nor ResolveBranchTargetSlow can chain out of this stack block.
    const int64_t initial_limit = -frame.budget_reserve - 1;
    const int64_t remaining =
        binding.handler(state, block.FirstOp(), initial_limit A64_UTLB_INIT, state.nzcv A64_HANDLER_PIN_INIT);
    state.last_block = saved_block;
    frame.exit_link = nullptr;
    frame.exit_op = nullptr;
    const unsigned retired = unsigned((initial_limit + 1) - remaining);
    return {frame.exit, retired};
}

}  // namespace

DecodedInstruction Decode(uint32_t instruction) { return detail::DecodeFields(instruction); }

RunResult Run(State& state, const Memory& memory, uint64_t budget, const Profile& profile, BudgetMode budget_mode) {
    constexpr int64_t MaxBatch = std::numeric_limits<int64_t>::max() - BlockMax;
    // Reserve room for the signed dispatch arithmetic and the returned
    // lifetime instruction count. Single steps always use exact accounting.
    const bool exact = budget_mode == BudgetMode::Exact || budget == 1 || budget > MaxBatch;
    detail::RunFrame frame{memory, exact ? BlockMax - 1 : 0};
    const RunScope scope(state, frame);
    uint64_t executed = 0;
    uint64_t mapped_page = 0;
    const BasicBlock* previous = nullptr;
    uint64_t* previous_link = nullptr;
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
    std::optional<LocalBlockCache> local;
    if (!memory.lookup_cached_block && memory.executable_page) local.emplace();
#endif
    while (executed < budget) {
        const auto pc = state.pc;
        if (pc & 3) return {Stop::Alignment, executed, pc, 0, 0, Access::Execute};
        if (pc >= mem::ArmTaskSize) return {Stop::Fault, executed, pc, 0, 0, Access::Execute};

        const BasicBlock* block = nullptr;
        unsigned count = 1;
        bool full_block = false;
        if (scope.outer && memory.executable_page && !profile.record) {
#if !FIBERCPU_AARCH64_UNMANAGED_CACHE
            assert(memory.lookup_cached_block && memory.allocate_block && memory.cache_block);
#endif
            const auto page = pc & ~PageMask;
            if (!frame.executable || page != mapped_page) {
                frame.executable = memory.executable_page(memory.context, page);
                mapped_page = page;
            }
            if (frame.executable) {
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
                block = DecodeBlock(pc, frame.executable, profile, memory, local ? &*local : nullptr);
#else
                block = DecodeBlock(pc, frame.executable, profile, memory);
#endif
                if (!block) return {Stop::CachePressure, executed, pc, 0, 0, Access::Execute};
                if (previous && previous->IsValid()
#if FIBERCPU_AARCH64_UNMANAGED_CACHE
                    && memory.lookup_cached_block
#endif
                )
                    *previous_link = BlockLink(block);
                count = block->inst_count();
                if (exact) count = unsigned(std::min<uint64_t>(count, budget - executed));
                full_block = count == block->inst_count();
            }
        }
        previous = nullptr;

        if (full_block) {
            state.last_block = block;
            frame.exit_link = nullptr;
            const auto* slots = block->FirstOp();
#if FIBERCPU_LLVM_JIT
            CountLLVMBlockEntry(state, *block, block->header.chain);
#endif
            const auto batch = int64_t(std::min<uint64_t>(budget - executed, MaxBatch));
            const auto available = batch - frame.budget_reserve - 1;
            const auto remaining =
                block->header.entry(state, slots, available - count A64_UTLB_INIT, state.nzcv A64_HANDLER_PIN_INIT);
            executed += uint64_t(available - remaining);
            const auto step = frame.exit;
            const auto* stop_op = frame.exit_op;
            if (frame.exit_link) {
                const auto* source = state.last_block;
                if (source->IsValid()) {
                    previous = source;
                    previous_link = frame.exit_link;
                    frame.executable = source->executable_page;
                    mapped_page = source->start_pc() & ~PageMask;
                }
            }
#if FIBERCPU_LLVM_JIT
            if (const auto* hot = std::exchange(frame.llvm_hot_block, nullptr))
                memory.observe_hot_block(memory.context, *const_cast<BasicBlock*>(hot));
#endif
            switch (step.reason) {
                case Stop::Continue:
                    break;
                case Stop::Refetch:
                    frame.executable = nullptr;
                    state.pc += 4;
                    break;
                case Stop::Branch:
                    state.pc = step.address;
                    break;
                case Stop::Syscall:
                    state.pc += 4;
                    [[fallthrough]];
                default: {
                    const auto word = state.last_block->Word(stop_op->GuestIndex());
                    return {step.reason, executed, step.address, word, step.immediate, step.access};
                }
            }
        } else if (block) {
            for (unsigned i = 0; i < count; ++i) {
                const auto step_pc = block->start_pc() + uint64_t{i} * 4;
                const auto word = block->Word(i);
                const auto& binding = DecodeOpBinding(word, profile);
                const auto res = ExecuteSingleInstructionOnStack(state, frame, step_pc, word, binding);
                executed += res.retired;
                if (res.step.reason == Stop::Continue) {
                    if (state.pc != step_pc + 4) break;
                    continue;
                }
                if (res.step.reason == Stop::Refetch) {
                    frame.executable = nullptr;
                    state.pc += 4;
                    break;
                }
                if (res.step.reason == Stop::Syscall) state.pc += 4;
                return {res.step.reason, executed, res.step.address, word, res.step.immediate, res.step.access};
            }
        } else {
            uint32_t word;
            if (!detail::ReadMemory(memory, pc, &word, sizeof(word), Access::Execute))
                return {Stop::Fault, executed, pc, 0, 0, Access::Execute};
            if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
            DecodedOpBinding local_slot;
            // A memory callback may reenter Run. Preserve the enclosing
            // call's decode cache entries while it is suspended.
            const auto& binding =
                scope.outer ? DecodeOpBinding(word, profile) : (local_slot = BindDecodedOp(word, profile));
            const auto res = ExecuteSingleInstructionOnStack(state, frame, pc, word, binding);
            executed += res.retired;
            if (profile.record && res.retired)
                profile.record(profile.context, pc, word, binding.can_follow && res.step.reason == Stop::Continue);
            switch (res.step.reason) {
                case Stop::Continue:
                    break;
                case Stop::Refetch:
                    frame.executable = nullptr;
                    state.pc += 4;
                    break;
                case Stop::Syscall:
                    state.pc += 4;
                    [[fallthrough]];
                default:
                    return {res.step.reason, executed, res.step.address, word, res.step.immediate, res.step.access};
            }
        }
    }
    return {Stop::Budget, executed, state.pc, 0, 0, Access::Execute};
}

}  // namespace fiberish::aarch64
