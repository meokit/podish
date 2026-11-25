#include "aarch64.h"

#include <array>
#include <chrono>
#include <utility>

#include "flag_liveness.h"
#include "lowering.h"

namespace fiberish::aarch64 {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
namespace detail {

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__) || defined(__clang__)
[[gnu::noinline]]
#endif
FIBERCPU_AARCH64_THREAD_ABI uint64_t ChainDirect(A64_THREAD_PARAMS) {
    const auto target_pc = current->op.pc_target;
    A64_THREAD_LINK(current + 1, target_pc);
    return FinishThread(state, current, remaining_after_block, nzcv A64_THREAD_PIN_PASS,
                        {Stop::Branch, Access::Execute, 0, target_pc});
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

constexpr unsigned BlockCacheBits = 8;

struct BlockSet {
    std::array<Block, 2> ways;
    unsigned newest = 0;
};

thread_local std::array<BlockSet, 1 << BlockCacheBits> BlockCache;

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

const Block& DecodeBlock(uint64_t pc, const uint8_t* executable, const Profile& profile, const Block* previous) {
    const auto* code = executable + (pc & PageMask);
    // Links share the source block's resolvers and only live for the target
    // slot's current occupant. Its executable bytes still get checked.
    if (previous) {
        for (const auto& link : previous->successors) {
            const auto* target = link.target;
            if (target && target->pc == pc && target->replacement == link.replacement &&
                target->executable_page == executable && MatchesWords(*target, code))
                return *target;
        }
    }
    const auto previous_replacement = previous ? previous->replacement : 0;
    const auto remember = [&](const Block& target) -> const Block& {
        // The fallback lookup may have replaced the predecessor itself.
        if (previous && previous->replacement == previous_replacement) {
            auto& links = previous->successors;
            links[1] = links[0];
            links[0] = {&target, target.replacement};
        }
        return target;
    };

    const auto index = ((pc >> 2) * 0x9e3779b97f4a7c15ULL) >> (64 - BlockCacheBits);
    auto& set = BlockCache[index];
    const auto matches = [&](const Block& block) {
        return block.count && block.pc == pc && block.executable_page == executable &&
               block.resolver == profile.resolve && block.pair_resolver == profile.resolve_pair &&
               block.sequence_resolver == profile.resolve_sequence && block.trusted_baking == profile.trusted_baking &&
               MatchesWords(block, code);
    };
    if (matches(set.ways[set.newest])) return remember(set.ways[set.newest]);
    set.newest ^= 1;
    auto& block = set.ways[set.newest];
    if (matches(block)) return remember(block);

    ++block.replacement;
    block.successors = {};
    block.pc = pc;
    block.executable_page = executable;
    block.resolver = profile.resolve;
    block.pair_resolver = profile.resolve_pair;
    block.sequence_resolver = profile.resolve_sequence;
    block.trusted_baking = profile.trusted_baking;
    const unsigned limit = std::min(BlockMax, (PageBytes - unsigned(pc & (PageBytes - 1))) / 4);
    unsigned count = 0;
    while (count < limit) {
        uint32_t word;
        std::memcpy(&word, code + count * 4, sizeof(word));
        block.words[count] = word;
        if constexpr (std::endian::native == std::endian::big) word = std::byteswap(word);
        const auto& slot = DecodeSlot(word, profile);
        block.slots[count] = slot;
        block.slots[count].pc = pc + count * 4;
        BindBlockOperands(block.slots[count].op, block.slots[count].pc);
        ++count;
        // Slow memory accesses request a refetch. Architectural branches and
        // operations with unconditional callbacks always end a block.
        if (!slot.can_follow) break;
    }
    uint32_t dead = 0;
#if !defined(FIBERCPU_AARCH64_DFE) || FIBERCPU_AARCH64_DFE
    uint32_t live = FlagMask;
    for (unsigned i = count; i-- > 0;) {
        const auto& slot = block.slots[i];
        if (slot.untrusted_scalar)
            live = FlagMask;
        else if (OmitNZCV(slot.op, live))
            dead |= uint32_t{1} << i;
    }
#endif
    std::array<uint32_t, BlockMax> instructions;
    for (unsigned i = 0; i < count; ++i) instructions[i] = block.slots[i].op.word;
    unsigned following = 0;
    for (unsigned i = count; i-- > 0;) {
        auto& slot = block.slots[i];
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
    auto& sentinel = block.slots[count];
    sentinel.thread = EndBlockThread();
    sentinel.pc = pc + count * 4;
    sentinel.owner = &block;
    sentinel.remaining = 0;
    block.count = count;
    return remember(block);
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
                previous = nullptr;
                frame.executable = memory.executable_page(memory.context, page);
                mapped_page = page;
            }
            if (frame.executable) {
                const auto& block = DecodeBlock(pc, frame.executable, profile, previous);
                previous = &block;
                slots = block.slots.data();
                count = unsigned(std::min<uint64_t>(block.count, budget - executed));
                full_block = count == block.count;
            }
        }
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
            const auto remaining = slots->thread(state, slots, budget - executed - count, memory.read_tlb, state.nzcv
#if FIBERCPU_AARCH64_PIN_REG >= 0
                                                 ,
                                                 state.x[FIBERCPU_AARCH64_PIN_REG]
#endif
            );
            executed = budget - remaining;
            step = frame.exit;
            stopping_slot = frame.exit_slot;
            previous = (stopping_slot + stopping_slot->remaining)->owner;
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
