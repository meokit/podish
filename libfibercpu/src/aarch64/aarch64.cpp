#include "aarch64.h"

#include <array>
#include <memory_resource>
#include <optional>
#include <unordered_map>
#include <utility>

#include "flag_liveness.h"
#include "lowering.h"

namespace fiberish::aarch64 {
const detail::Block detail::Block::Invalid{};
#if FIBERCPU_AARCH64_TAIL_DISPATCH
namespace detail {

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::noinline]]
#endif
FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainDirect(A64_THREAD_PARAMS) {
    const auto target_pc = current->op.pc_target;
    A64_THREAD_LINK(current + 1, 0, target_pc);
}

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::cold, gnu::noinline]]
#endif
FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainSlow(A64_THREAD_PARAMS) {
    auto& frame = *state.active_run;
    const auto target_pc = frame.exit_target;
    const auto& memory = frame.memory;
    if (remaining_after_block && memory.find_block) {
#ifdef ENABLE_TLB_STATS
        auto* stats = memory.dispatch_stats;
#endif
        const auto* target = (target_pc & 3) ? nullptr : memory.find_block(memory.context, target_pc, nullptr);
        if (target && target->IsValid()) {
            const auto* sentinel = current + current->remaining;
            sentinel->links[frame.exit_edge] = target;
            if (remaining_after_block >= target->Count()) {
#ifdef ENABLE_TLB_STATS
                if (stats) {
                    ++stats->resolved_links;
                    stats->cross_page_links += ((sentinel->owner->pc ^ target_pc) & ~PageMask) != 0;
                }
#endif
                frame.exit_edge = 2;
                ATTR_MUSTTAIL return target->header.entry(state, target->Slots(),
                                                          remaining_after_block - target->Count(), data_tlb, tlb_tags,
                                                          tlb_addend, nzcv A64_THREAD_PIN_PASS);
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
    const auto step = current->remaining ? StepResult{Stop::Branch, Access::Execute, 0, target_pc} : StepResult{};
    return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS, step);
}

}  // namespace detail
#endif
namespace {

using namespace detail;

using Slot = DispatchSlot;

using Resolver = decltype(Profile::resolve);

struct CacheEntry {
    Slot slot;
    Resolver resolver = nullptr;
    bool trusted_baking = false;
};

// The cache key is the word read from executable memory, so writes, remaps,
// permission changes and different guest address spaces need no invalidation.
constexpr unsigned CacheBits = 10;
thread_local std::array<CacheEntry, 1 << CacheBits> DecodeCache;

bool IsInteger(Kind kind) { return kind > Kind::Undefined && kind <= Kind::Multiply; }

Slot ResolveSlot(uint32_t word, const Profile& profile) {
    Slot slot;
    slot.op = DecodeFields(word);
    auto binding = profile.resolve ? profile.resolve(slot.op, false) : HandlerBinding{};
    slot.untrusted_scalar = !profile.trusted_baking && binding.scalar && !binding.threaded;
    if (!binding.scalar) binding = Lower(slot.op);
    slot.op.handler = binding.scalar;
    slot.thread = binding.threaded;
    const auto kind = slot.op.kind;
    const bool integer = IsInteger(kind);
    const bool branch = kind >= Kind::BranchImmediate && kind <= Kind::TestBranch;
    const bool data_transfer = kind >= Kind::LoadStoreImmediate && kind <= Kind::LoadLiteral;
    const bool arithmetic = kind == Kind::ScalarFloat || (kind == Kind::Vector && (word & 0xbe000000) != 0x0c000000);
    slot.can_follow = !slot.untrusted_scalar && (integer || data_transfer || arithmetic);
    slot.retains_page = slot.can_follow || (branch && !slot.untrusted_scalar);
    if (!slot.thread) slot.thread = DynamicThread();
    return slot;
}

const Slot& DecodeSlot(uint32_t word, const Profile& profile) {
    const auto index = (word * 0x9e3779b1u) >> (32 - CacheBits);
    auto& entry = DecodeCache[index];
    if (!entry.slot.op.handler || entry.slot.op.word != word || entry.resolver != profile.resolve ||
        entry.trusted_baking != profile.trusted_baking) {
        entry.slot = ResolveSlot(word, profile);
        entry.resolver = profile.resolve;
        entry.trusted_baking = profile.trusted_baking;
    }
    return entry.slot;
}

struct LocalBlocks {
    std::pmr::monotonic_buffer_resource pool;
    std::unordered_map<uint64_t, Block*> by_pc;
};

struct RunScope {
    static inline thread_local bool active = false;
    State& state;
    detail::RunFrame* saved;
    const bool outer = !std::exchange(active, true);

    RunScope(State& state, detail::RunFrame& frame) : state(state), saved(std::exchange(state.active_run, &frame)) {}

    ~RunScope() {
        state.active_run = saved;
        if (outer) active = false;
    }
};

const Block* DecodeBlock(uint64_t pc, const uint8_t* executable, const Profile& profile, const Memory& memory,
                         LocalBlocks* local) {
    const auto* code = executable + (pc & PageMask);
    const bool managed = memory.find_block != nullptr;
    auto* cached = managed ? memory.find_block(memory.context, pc, executable) : nullptr;
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
    std::array<Slot, BlockMax> decoded;
    std::array<uint32_t, BlockMax> words;
    unsigned count = 0;
    while (count < limit) {
        uint32_t word;
        std::memcpy(&word, code + count * 4, sizeof(word));
        words[count] = word;
        if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
        auto& slot = decoded[count];
        slot = DecodeSlot(word, profile);
        slot.pc = pc + count * 4;
        BindBlockOperands(slot.op, slot.pc);
        ++count;
        if (!slot.can_follow) break;
    }

    const auto size = Block::Size(count, !managed);
    auto* storage = memory.allocate_block ? memory.allocate_block(memory.context, size, pc)
                                          : local->pool.allocate(size, alignof(Block));
    if (!storage) return nullptr;
    auto& block = *new (storage) Block;
    block.pc = pc;
    block.header.chain = BlockHeader::Valid | (uint64_t{count} << 32) | static_cast<uint32_t>(pc);
    block.executable_page = executable;
    auto* slots = block.Slots();
    std::uninitialized_copy_n(decoded.data(), count, slots);
    if (!managed) std::memcpy(block.Words(), words.data(), count * sizeof(uint32_t));
    uint32_t dead = 0;
#if !defined(FIBERCPU_AARCH64_DFE) || FIBERCPU_AARCH64_DFE
    uint32_t live = FlagMask;
    for (unsigned i = count; i-- > 0;) {
        const auto& slot = slots[i];
        if (slot.untrusted_scalar)
            live = FlagMask;
        else if (OmitNZCV(slot.op, live))
            dead |= uint32_t{1} << i;
    }
#endif
    std::array<uint32_t, BlockMax> instructions;
    for (unsigned i = 0; i < count; ++i) instructions[i] = slots[i].op.word;
    unsigned following = 0;
    for (unsigned i = count; i-- > 0;) {
        auto& slot = slots[i];
        slot.remaining = count - i;
        const uint32_t dead_from_here = dead >> i;
        ThreadHandler threaded = nullptr;
        if (slot.can_follow && following > 1 && profile.resolve_sequence) {
            unsigned length = 0;
            const auto fusion =
                profile.resolve_sequence(instructions.data() + i, following + 1, &length, dead_from_here, threaded);
            if (fusion && (profile.trusted_baking || threaded || !dead) && length >= 3 && length <= following + 1) {
                slot.fused = fusion;
                slot.fused_length = length;
            }
        }
        if (!slot.fused && slot.can_follow && IsInteger(slot.op.kind) && following > 0 && profile.resolve_pair) {
            threaded = nullptr;
            const auto fusion = profile.resolve_pair(instructions[i], instructions[i + 1], dead_from_here, threaded);
            if (fusion && (profile.trusted_baking || threaded || !dead)) {
                slot.fused = fusion;
                slot.fused_length = 2;
            }
        }
        if (slot.fused) {
            slot.untrusted_fused = !profile.trusted_baking && !threaded;
            slot.thread = threaded ? threaded : FusedThread();
            slot.omits_flags = (dead_from_here & ((1u << slot.fused_length) - 1)) != 0;
        } else if (dead_from_here & 1) {
            auto binding = profile.resolve ? profile.resolve(slot.op, true) : HandlerBinding{};
            if (binding.scalar && !profile.trusted_baking && !binding.threaded)
                binding = {};
            else if (!binding.scalar)
                binding = LowerDeadNZCV(slot.op);
            if (binding.scalar) {
                // Preserve op.handler for a budget cut before the overwriting instruction.
                slot.fused = binding.scalar;
                slot.thread = binding.threaded ? binding.threaded : FusedThread();
                slot.omits_flags = true;
            }
        }
        following = slot.untrusted_scalar ? 0 : slot.can_follow ? following + 1 : 1;
    }
    auto& sentinel = *new (slots + count) Slot;
    sentinel.thread = EndBlockThread();
    sentinel.pc = pc + count * 4;
    sentinel.owner = &block;
    sentinel.links[0] = &Block::Invalid;
    sentinel.links[1] = &Block::Invalid;
    sentinel.remaining = 0;
    block.header.entry = slots->thread;
    if (memory.cache_block)
        memory.cache_block(memory.context, &block);
    else
        local->by_pc[pc] = &block;
    return &block;
}

StepResult DispatchSpan(State& state, const Memory& memory, const Slot* current, const Slot* end, bool full_block) {
    while (current != end) {
        const bool use_bound =
            current->fused && current->fused_length <= end - current && (full_block || !current->omits_flags);
        const unsigned length = use_bound ? current->fused_length : 1;
        state.pc = current->pc;
        const auto execute = use_bound ? current->fused : current->op.handler;
        const auto* last = current + length - 1;
        auto step = CompleteStep(state, last, execute(state, memory, current->op));
        if (use_bound ? current->untrusted_fused : current->untrusted_scalar) {
            state.active_run->executable = nullptr;
            if (step.reason == Stop::Continue) {
                state.pc = last->pc;
                step.reason = Stop::Refetch;
            }
        }
        if (step.reason != Stop::Continue) return step;
        current += length;
    }
    state.pc = end->pc;
    return {};
}

}  // namespace

DecodedInstruction Decode(uint32_t instruction) {
    auto op = detail::DecodeFields(instruction);
    op.handler = detail::Lower(op).scalar;
    return op;
}

RunResult Run(State& state, const Memory& memory, uint64_t budget, const Profile& profile) {
    detail::RunFrame frame{memory};
    const RunScope scope(state, frame);
    uint64_t executed = 0;
    uint64_t mapped_page = 0;
    const Block* previous = nullptr;
    unsigned previous_edge = 0;
    std::optional<LocalBlocks> local;
    if (!memory.find_block && memory.executable_page) local.emplace();
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    static const mem::SoftTlb empty_tlb;
    const auto* data_tlb = memory.data_tlb ? memory.data_tlb : &empty_tlb;
#endif
    while (executed < budget) {
        const auto pc = state.pc;
        if (pc & 3) return {Stop::Alignment, executed, pc, 0, 0, Access::Execute};

        const Slot* slots = nullptr;
        Slot local_slot;
        unsigned count = 1;
        bool full_block = false;
        if (scope.outer && memory.executable_page && !profile.record) {
            const auto page = pc & ~PageMask;
            if (!frame.executable || page != mapped_page) {
                frame.executable = memory.executable_page(memory.context, page);
                mapped_page = page;
            }
            if (frame.executable) {
                const auto* block = DecodeBlock(pc, frame.executable, profile, memory, local ? &*local : nullptr);
                if (!block) return {Stop::CachePressure, executed, pc, 0, 0, Access::Execute};
                if (previous && previous->IsValid() && memory.find_block)
                    previous->Sentinel().links[previous_edge] = block;
                slots = block->Slots();
                count = unsigned(std::min<uint64_t>(block->Count(), budget - executed));
                full_block = count == block->Count();
            }
        }
        previous = nullptr;
        if (!slots) {
            uint32_t word;
            if (!detail::ReadMemory(memory, pc, &word, sizeof(word), Access::Execute))
                return {Stop::Fault, executed, pc, 0, 0, Access::Execute};
            if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
            if (scope.outer) {
                slots = &DecodeSlot(word, profile);
            } else {
                // A memory callback may reenter Run. Preserve the enclosing
                // call's block and decode cache entries while it is suspended.
                local_slot = ResolveSlot(word, profile);
                slots = &local_slot;
            }
        }
        if (profile.record) profile.record(profile.context, slots->op);
        StepResult step;
        const Slot* stopping_slot;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        if (full_block) {
            frame.exit_edge = 2;
            const auto remaining = slots->thread(state, slots, budget - executed - count, data_tlb,
                                                 mem::InvalidMicroTlbAbiTags(), 0, state.nzcv
#if FIBERCPU_AARCH64_PIN_REG >= 0
                                                 ,
                                                 state.x[FIBERCPU_AARCH64_PIN_REG]
#endif
            );
            executed = budget - remaining;
            step = frame.exit;
            stopping_slot = frame.exit_slot;
            const auto* source = (stopping_slot + stopping_slot->remaining)->owner;
            if (frame.exit_edge < 2 && source->IsValid()) {
                previous = source;
                previous_edge = frame.exit_edge;
                frame.executable = source->executable_page;
                mapped_page = source->pc & ~PageMask;
            }
        } else
#endif
        {
            if (frame.executable) {
                step = DispatchSpan(state, memory, slots, slots + count, full_block);
            } else {
                step = slots->op.handler(state, memory, slots->op);
                if (step.reason == Stop::Continue) state.pc = pc + 4;
            }
            if (step.reason == Stop::Continue) {
                executed += count;
                continue;
            }
            const auto before_stop = (state.pc - pc) / 4;
            executed += before_stop;
            stopping_slot = slots + before_stop;
            executed += RetiresOnExit(step.reason);
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
            default:
                return {step.reason, executed, step.address, stopping_slot->op.word, step.immediate, step.access};
        }
    }
    return {Stop::Budget, budget, state.pc, 0, 0, Access::Execute};
}

}  // namespace fiberish::aarch64
