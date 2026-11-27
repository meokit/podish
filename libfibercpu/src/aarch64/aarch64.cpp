#include "aarch64.h"

#include <array>
#include <cassert>
#include <limits>
#include <memory>
#include <memory_resource>
#include <mutex>
#include <optional>
#include <type_traits>
#include <unordered_map>
#include <utility>

#include "compare_branch.h"
#include "flag_liveness.h"
#include "lowering.h"

namespace fiberish::aarch64 {
const detail::BasicBlock detail::BasicBlock::Invalid{};

namespace detail {

namespace {

class ScalarHandlerTable {
    static constexpr unsigned PageSize = 256;
    static constexpr unsigned Capacity = std::numeric_limits<BlockHandlerIndex>::max();

public:
    BlockHandlerIndex Intern(ScalarHandlerFunc handler) {
        struct Entry {
            ScalarHandlerFunc handler = nullptr;
            BlockHandlerIndex index = 0;
        };
        static thread_local std::array<Entry, 128> recent{};
        const auto address = reinterpret_cast<uintptr_t>(handler) >> 2;
        auto& entry = recent[(address ^ (address >> 7)) % recent.size()];
        if (entry.handler == handler) return entry.index;

        std::lock_guard lock(mutex);
        if (const auto existing = indices.find(handler); existing != indices.end()) {
            entry = {handler, existing->second};
        } else if (indices.size() < Capacity) {
            const auto index = static_cast<BlockHandlerIndex>(indices.size() + 1);
            auto& page = pages[index / PageSize];
            if (!page) page = std::make_unique<ScalarHandlerFunc[]>(PageSize);
            indices.emplace(handler, index);
            page[index % PageSize] = handler;
            entry = {handler, index};
        } else {
            entry = {handler, 0};
        }
        return entry.index;
    }

    ScalarHandlerFunc Resolve(BlockHandlerIndex index) const {
        // A page and its entry are initialized before the index reaches a
        // block footer. Existing entries are immutable during later inserts.
        return pages[index / PageSize][index % PageSize];
    }

private:
    std::mutex mutex;
    std::unordered_map<ScalarHandlerFunc, BlockHandlerIndex> indices;
    std::array<std::unique_ptr<ScalarHandlerFunc[]>, (Capacity + 1) / PageSize> pages;
};

ScalarHandlerTable scalar_handlers;

}  // namespace

ScalarHandlerFunc ResolveBlockScalarHandler(BlockHandlerIndex index) { return scalar_handlers.Resolve(index); }

DecodedInstruction ReadScalarOp(const BasicBlock& block, const DecodedOp& slot) {
    const auto word = block.Word(slot.GuestIndex());
    const bool only_base_fields = slot.Has(BaseFields);
    auto op = only_base_fields ? BaseInstruction(word) : DecodeFields(word);
    if (only_base_fields) op.kind = static_cast<Kind>(slot.Operands() >> 32);
    op.scalar_handler = block.ScalarBinding(slot.GuestIndex());
    if (!op.scalar_handler) op.scalar_handler = Lower(op).scalar_handler;
    BindBlockOperands(op, block.start_pc() + uint64_t{slot.GuestIndex()} * 4);
    return op;
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_HANDLER_ABI int64_t ResolveBranchTargetSlow(A64_HANDLER_PARAMS) {
    auto& frame = *state.active_run;
    const auto target_pc = frame.exit_target;
    const auto& memory = frame.memory;
    if (instr_limit + frame.budget_reserve >= 0 && memory.lookup_cached_block) {
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
                ATTR_MUSTTAIL return target->header.entry(state, target->FirstOp(), instr_limit - target->inst_count(),
                                                          utlb_tags, utlb_addend, flags_cache A64_HANDLER_PIN_ARGS);
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
    if (current != state.last_block->Sentinel())
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS,
                               {Stop::Branch, Access::Execute, 0, target_pc});
    // At the sentinel the source block has already retired, including its
    // final branch. Resume at the target with the existing budget.
    CommitDispatchState(state, target_pc, flags_cache A64_HANDLER_PIN_ARGS);
    frame.exit_op = current;
    frame.exit = {};
    return instr_limit;
}

#endif
}  // namespace detail

namespace {

using namespace detail;

using Resolver = decltype(Profile::resolve);

struct DecodedOpBinding {
    DecodedInstruction op;
    HandlerFunc handler = nullptr;
    ScalarHandlerFunc fused = nullptr;
    BlockHandlerIndex scalar_index = 0;
    uint8_t ordinal = 0;
    uint8_t fused_length = 1;
    uint8_t flags = 0;
    bool keep_scalar = false;
    bool can_follow = false;
    bool nop = false;

    bool Has(SlotFlag flag) const { return (flags & flag) != 0; }
};

struct CacheEntry {
    DecodedOpBinding slot;
    Resolver resolver = nullptr;
    bool trusted_baking = false;
};

// The cache key is the word read from executable memory, so writes, remaps,
// permission changes and different guest address spaces need no invalidation.
constexpr unsigned CacheBits = 10;
thread_local std::array<CacheEntry, 1 << CacheBits> DecodeCache;

bool IsInteger(Kind kind) { return kind > Kind::Undefined && kind <= Kind::Multiply; }

DecodedOpBinding BindDecodedOp(uint32_t word, const Profile& profile) {
    DecodedOpBinding slot;
    slot.op = DecodeFields(word);
    auto binding = profile.resolve ? profile.resolve(slot.op, false) : HandlerBinding{};
    const auto kind = slot.op.kind;
    const bool custom = binding.scalar_handler != nullptr;
    const bool untrusted = custom && !profile.trusted_baking && !binding.handler;
    slot.nop = !custom && word == NopWord;
    if (!custom) binding = Lower(slot.op);
    slot.op.scalar_handler = binding.scalar_handler;
    slot.handler = binding.handler;
    if (custom && !slot.handler) slot.handler = GetScalarDispatchHandler();
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    assert(slot.handler && "Builtin instructions must provide a tail-dispatch handler");
#endif
    slot.keep_scalar = custom;
    if (custom) slot.scalar_index = scalar_handlers.Intern(binding.scalar_handler);

    const bool branch = kind >= Kind::BranchImmediate && kind <= Kind::TestBranch;
    const bool data_transfer = kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral;
    const bool arithmetic = kind == Kind::ScalarFloat || (kind == Kind::Vector && (word & 0xbe000000) != 0x0c000000);
    slot.can_follow = !untrusted && (IsInteger(kind) || data_transfer || arithmetic || slot.nop || binding.can_follow);
    if (slot.can_follow || (branch && !untrusted)) slot.flags |= RetainsPage;
    if (untrusted) slot.flags |= UntrustedScalar;
    if (branch) slot.flags |= HasBranchLink;
    if (HasExtraOperandSlot(kind)) slot.flags |= HasDataSlot;
    if (kind == Kind::System || kind == Kind::ScalarFloat || kind == Kind::Vector) slot.flags |= BaseFields;
    return slot;
}

const DecodedOpBinding& DecodeOpBinding(uint32_t word, const Profile& profile) {
    const auto index = (word * 0x9e3779b1u) >> (32 - CacheBits);
    auto& entry = DecodeCache[index];
    if (!entry.slot.op.scalar_handler || entry.slot.op.word != word || entry.resolver != profile.resolve ||
        entry.trusted_baking != profile.trusted_baking) {
        entry.slot = BindDecodedOp(word, profile);
        entry.resolver = profile.resolve;
        entry.trusted_baking = profile.trusted_baking;
    }
    return entry.slot;
}

struct LocalBlockCache {
    std::pmr::monotonic_buffer_resource pool;
    std::unordered_map<uint64_t, BasicBlock*> by_pc;
};

struct RunScope {
    static inline thread_local bool active = false;
    State& state;
    detail::RunFrame* saved;
    const BasicBlock* saved_block;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    const mem::SoftTlb* saved_data_tlb;
#endif
    const bool outer = !std::exchange(active, true);

    RunScope(State& state, detail::RunFrame& frame)
        : state(state),
          saved(std::exchange(state.active_run, &frame)),
          saved_block(std::exchange(state.last_block, nullptr)) {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        static const mem::SoftTlb empty_tlb;
        saved_data_tlb = std::exchange(state.data_tlb, frame.memory.data_tlb ? frame.memory.data_tlb : &empty_tlb);
#endif
    }

    ~RunScope() {
        state.active_run = saved;
        state.last_block = saved_block;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        state.data_tlb = saved_data_tlb;
#endif
        if (outer) active = false;
    }
};

const BasicBlock* DecodeBlock(uint64_t pc, const uint8_t* executable, const Profile& profile, const Memory& memory,
                              LocalBlockCache* local) {
    const auto* code = executable + (pc & PageMask);
    const bool managed = memory.lookup_cached_block != nullptr;
    auto* cached = managed ? memory.lookup_cached_block(memory.context, pc, executable) : nullptr;
    if (!managed) {
        const auto it = local->by_pc.find(pc);
        if (it != local->by_pc.end()) cached = it->second;
    }
    if (cached && cached->IsValid() && cached->executable_page == executable) {
        if (managed || MatchesWords(*cached, code)) {
#ifdef ENABLE_TLB_STATS
            if (auto* stats = memory.dispatch_stats) ++stats->block_lookup_hits;
#endif
            return cached;
        }
        cached->Invalidate();
    }

#ifdef ENABLE_TLB_STATS
    if (auto* stats = memory.dispatch_stats) ++stats->block_lookup_misses;
#endif
    const unsigned limit = std::min(BlockMax, (PageBytes - unsigned(pc & PageMask)) / 4);
    std::array<DecodedOpBinding, BlockMax> decoded;
    std::array<uint32_t, BlockMax> words;
    unsigned count = 0;
    unsigned slot_count = 0;
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
        if (!slot.can_follow) break;
    }

    uint32_t dead = 0;
#if !defined(FIBERCPU_AARCH64_DFE) || FIBERCPU_AARCH64_DFE
    uint32_t live = FlagMask;
    for (unsigned i = slot_count; i-- > 0;) {
        const auto& slot = decoded[i];
        if (slot.Has(UntrustedScalar))
            live = FlagMask;
        else if (!slot.nop && OmitNZCV(slot.op, live))
            dead |= uint32_t{1} << i;
    }
#endif
    std::array<uint32_t, BlockMax> instructions;
    for (unsigned i = 0; i < slot_count; ++i) instructions[i] = decoded[i].op.word;
    unsigned following = 0;
    unsigned units = 0;
    uint32_t scalar_mask = 0;
    uint32_t fused_mask = 0;
    std::array<BlockHandlerIndex, BlockMax> fusion_indices;
    bool wide_bindings = false;
    for (unsigned i = slot_count; i-- > 0;) {
        auto& slot = decoded[i];
        if (i + 1 < slot_count && decoded[i + 1].ordinal != slot.ordinal + 1) following = 0;
        const uint32_t dead_from_here = dead >> i;
        HandlerFunc handler = nullptr;
        if (slot.can_follow && following > 1 && profile.resolve_sequence) {
            unsigned length = 0;
            const auto fusion =
                profile.resolve_sequence(instructions.data() + i, following + 1, &length, dead_from_here, handler);
            if (fusion && (profile.trusted_baking || handler || !dead) && length >= 3 && length <= following + 1) {
                slot.fused = fusion;
                slot.fused_length = length;
            }
        }
        if (!slot.fused && slot.can_follow && IsInteger(slot.op.kind) && following > 0 && profile.resolve_pair) {
            handler = nullptr;
            const auto fusion = profile.resolve_pair(instructions[i], instructions[i + 1], dead_from_here, handler);
            if (fusion && (profile.trusted_baking || handler || !dead)) {
                slot.fused = fusion;
                slot.fused_length = 2;
            }
        }
        if (slot.fused) {
            if (!profile.trusted_baking && !handler) slot.flags |= UntrustedFused;
            slot.handler = handler ? handler : GetFusedDispatchHandler();
            if (dead_from_here & ((1u << slot.fused_length) - 1)) slot.flags |= OmitsFlags;
        } else if (dead_from_here & 1) {
            auto binding = profile.resolve ? profile.resolve(slot.op, true) : HandlerBinding{};
            if (binding.scalar_handler && !profile.trusted_baking && !binding.handler)
                binding = {};
            else if (!binding.scalar_handler)
                binding = LowerDeadNZCV(slot.op);
            if (binding.scalar_handler) {
                slot.fused = binding.scalar_handler;
                slot.handler = binding.handler ? binding.handler : GetFusedDispatchHandler();
                slot.flags |= OmitsFlags;
            }
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        } else if (following == 1 && i + 2 == slot_count && slot.can_follow) {
            const auto& branch = decoded[i + 1];
            if (profile.trusted_baking || (!slot.keep_scalar && !branch.keep_scalar)) {
                // Only the hot entry changes. Partial budgets still run the two scalar instructions.
                if (const auto pair = LowerCompareBranch(slot.op, branch.op)) slot.handler = pair;
            }
#endif
        }
        following = slot.Has(UntrustedScalar) ? 0 : slot.can_follow ? following + 1 : 1;
        units += 1 + slot.Has(HasDataSlot);
        if (slot.keep_scalar) {
            scalar_mask |= uint32_t{1} << slot.ordinal;
            wide_bindings |= slot.scalar_index == 0;
        }
        if (slot.fused) {
            fusion_indices[i] = scalar_handlers.Intern(slot.fused);
            wide_bindings |= fusion_indices[i] == 0;
            slot.flags |= Fused;
            fused_mask |= uint32_t{1} << slot.ordinal;
        }
    }

    const auto fusions = std::popcount(fused_mask);
    const auto bindings = std::popcount(scalar_mask) + fusions;
#if FIBERCPU_LLVM_JIT
    const bool retain_region_context = memory.retain_region_context;
#else
    const bool retain_region_context = false;
#endif
    const auto binding_storage = !bindings       ? BlockHandlerStorage::None
                                 : wide_bindings ? BlockHandlerStorage::Pointers
                                                 : BlockHandlerStorage::Indexed;
    const auto size =
        BasicBlock::CalculateSize(units, count, bindings, fusions, binding_storage, retain_region_context);
    auto* storage = memory.allocate_block ? memory.allocate_block(memory.context, size, pc)
                                          : local->pool.allocate(size, alignof(BasicBlock));
    if (!storage) return nullptr;
    auto& block = *new (storage) BasicBlock;
    block.header.chain = BasicBlockChainPrefix::Pack(pc, count, bindings != 0, retain_region_context);
    block.sentinel_slot_index = uint8_t(units);
    block.has_wide_bindings = wide_bindings;
    block.executable_page = executable;

    auto* bytes = reinterpret_cast<std::byte*>(&block + 1);
    for (unsigned i = 0; i < slot_count; ++i) {
        const auto& binding = decoded[i];
        const auto operands =
            binding.Has(BaseFields) ? binding.op.word | (uint64_t(binding.op.kind) << 32) : PackOpOperands(binding.op);
        const DecodedOp slot{
            .handler = binding.handler,
            .payload = DecodedOp::Pack(operands, binding.ordinal, binding.flags),
        };
        if (slot.Has(HasDataSlot)) {
            const auto& op = binding.op;
            const auto primary = HasBlockTarget(op.kind)           ? op.target_pc
                                 : op.kind == Kind::BranchRegister ? pc + slot.GuestIndex() * 4
                                                                   : op.immediate;
            const auto secondary = slot.Has(HasBranchLink)     ? BlockLink(&BasicBlock::Invalid)
                                   : op.kind == Kind::Bitfield ? op.mask
                                                               : 0;
            new (bytes) DecodedOpWithDataSlot{slot, primary, secondary};
            bytes += sizeof(DecodedOpWithDataSlot);
        } else {
            new (bytes) DecodedOp(slot);
            bytes += sizeof(DecodedOp);
        }
    }
    new (bytes) DecodedOp{.handler = GetExitHandlerFallthrough(), .payload = BlockLink(&BasicBlock::Invalid)};
    if (retain_region_context) block.SetRegionContext(nullptr);
    std::memcpy(const_cast<uint32_t*>(block.Words()), words.data(), count * sizeof(uint32_t));
    if (bindings) {
        auto* masks = const_cast<uint32_t*>(block.BindingMasks());
        new (masks) uint32_t(scalar_mask);
        new (masks + 1) uint32_t(fused_mask);
        const auto write_bindings = [&]<typename Entry>(Entry* scalar) {
            auto* fused = scalar + std::popcount(scalar_mask);
            auto* lengths = reinterpret_cast<uint8_t*>(scalar + bindings);
            for (unsigned i = 0; i < slot_count; ++i) {
                const auto& binding = decoded[i];
                if constexpr (std::is_same_v<Entry, BlockHandlerIndex>) {
                    if (binding.keep_scalar) new (scalar++) Entry(binding.scalar_index);
                    if (binding.fused) new (fused++) Entry(fusion_indices[i]);
                } else {
                    if (binding.keep_scalar) new (scalar++) Entry(binding.op.scalar_handler);
                    if (binding.fused) new (fused++) Entry(binding.fused);
                }
                if (binding.fused) new (lengths++) uint8_t(binding.fused_length);
            }
        };
        if (binding_storage == BlockHandlerStorage::Indexed)
            write_bindings(reinterpret_cast<BlockHandlerIndex*>(masks + 2));
        else
            write_bindings(reinterpret_cast<ScalarHandlerFunc*>(masks + 2));
    }
    block.header.entry = block.FirstOp()->handler;
    if (memory.cache_block)
        memory.cache_block(memory.context, &block);
    else
        local->by_pc[pc] = &block;
    return &block;
}

StepResult DispatchSpan(State& state, const Memory& memory, const BasicBlock& block, unsigned count, bool full_block) {
    auto* current = block.FirstOp();
    while (current != block.Sentinel() && current->GuestIndex() < count) {
        const auto scalar = ReadScalarOp(block, *current);
        BasicBlock::Fusion fusion;
        if (current->Has(Fused) && (full_block || !current->Has(OmitsFlags))) {
            fusion = block.FusedBinding(current->GuestIndex());
            if (fusion.length > count - current->GuestIndex()) fusion = {};
        }
        const auto* last = current->LastOp(fusion.length);
        const bool untrusted = current->Has(fusion.handler ? UntrustedFused : UntrustedScalar);
        state.pc = block.start_pc() + uint64_t{current->GuestIndex()} * 4;
        const auto execute = fusion.handler ? fusion.handler : scalar.scalar_handler;
        auto step = CompleteStep(state, last, execute(state, memory, scalar));
        if (untrusted) {
            state.active_run->executable = nullptr;
            if (step.reason == Stop::Continue) {
                state.pc = last->GetStartPc(state);
                step.reason = Stop::Refetch;
            }
        }
        if (step.reason != Stop::Continue) return step;
        current = last->NextOp();
    }
    state.pc = block.start_pc() + uint64_t{count} * 4;
    return {};
}

}  // namespace

DecodedInstruction Decode(uint32_t instruction) {
    auto op = detail::DecodeFields(instruction);
    op.scalar_handler = detail::Lower(op).scalar_handler;
    return op;
}

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
    std::optional<LocalBlockCache> local;
    if (!memory.lookup_cached_block && memory.executable_page) local.emplace();
    while (executed < budget) {
        const auto pc = state.pc;
        if (pc & 3) return {Stop::Alignment, executed, pc, 0, 0, Access::Execute};
        if (pc >= mem::ArmTaskSize) return {Stop::Fault, executed, pc, 0, 0, Access::Execute};

        const BasicBlock* block = nullptr;
        const DecodedOpBinding* single = nullptr;
        DecodedOpBinding local_slot;
        unsigned count = 1;
        bool full_block = false;
        if (scope.outer && memory.executable_page && !profile.record) {
            const auto page = pc & ~PageMask;
            if (!frame.executable || page != mapped_page) {
                frame.executable = memory.executable_page(memory.context, page);
                mapped_page = page;
            }
            if (frame.executable) {
                block = DecodeBlock(pc, frame.executable, profile, memory, local ? &*local : nullptr);
                if (!block) return {Stop::CachePressure, executed, pc, 0, 0, Access::Execute};
                if (previous && previous->IsValid() && memory.lookup_cached_block) *previous_link = BlockLink(block);
                count = block->inst_count();
                if (exact) count = unsigned(std::min<uint64_t>(count, budget - executed));
                full_block = count == block->inst_count();
            }
        }
        previous = nullptr;
        if (!block) {
            uint32_t word;
            if (!detail::ReadMemory(memory, pc, &word, sizeof(word), Access::Execute))
                return {Stop::Fault, executed, pc, 0, 0, Access::Execute};
            if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
            if (scope.outer) {
                single = &DecodeOpBinding(word, profile);
            } else {
                // A memory callback may reenter Run. Preserve the enclosing
                // call's block and decode cache entries while it is suspended.
                local_slot = BindDecodedOp(word, profile);
                single = &local_slot;
            }
        }
        state.last_block = block;
        StepResult step;
        const DecodedOp* stop_op = nullptr;
        unsigned stop_ordinal = 0;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        if (full_block) {
            frame.exit_link = nullptr;
            const auto* slots = block->FirstOp();
#if FIBERCPU_LLVM_JIT
            CountLLVMBlockEntry(state, *block, block->header.chain);
#endif
            const auto batch = int64_t(std::min<uint64_t>(budget - executed, MaxBatch));
            const auto available = batch - frame.budget_reserve - 1;
            const auto remaining = block->header.entry(state, slots, available - count, mem::InvalidMicroTlbAbiTags(),
                                                       0, state.nzcv A64_HANDLER_PIN_INIT);
            executed += uint64_t(available - remaining);
            step = frame.exit;
            stop_op = frame.exit_op;
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
        } else
#endif
        {
            if (block) {
                step = DispatchSpan(state, memory, *block, count, full_block);
            } else {
                step = single->op.scalar_handler(state, memory, single->op);
                if (profile.record && (step.reason == Stop::Continue || RetiresOnExit(step.reason)))
                    profile.record(profile.context, pc, single->op.word,
                                   single->can_follow && step.reason == Stop::Continue);
                if (step.reason == Stop::Continue) state.pc = pc + 4;
            }
            if (step.reason == Stop::Continue) {
                executed += count;
                continue;
            }
            if (block) stop_ordinal = unsigned((state.pc - pc) / 4);
            executed += stop_ordinal + RetiresOnExit(step.reason);
        }
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
                const auto word = stop_op ? state.last_block->Word(stop_op->GuestIndex())
                                  : block ? block->Word(stop_ordinal)
                                          : single->op.word;
                return {step.reason, executed, step.address, word, step.immediate, step.access};
            }
        }
    }
    return {Stop::Budget, executed, state.pc, 0, 0, Access::Execute};
}

}  // namespace fiberish::aarch64
