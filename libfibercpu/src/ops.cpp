#include "ops.h"
#include <algorithm>
#include "dispatch.h"
#include "instruction_variants.h"

namespace fiberish {

template <typename T>
static inline bool ExecuteMemOp(EmuState* state, DecodedOp* op, mem::MicroTLB& utlb, uint32_t addr, T* value,
                                bool is_write) {
    if (!is_write) {
        auto res = state->mmu.read<T>(addr, &utlb, op);
        if (!res) {
            return true;
        } else {
            *value = *res;
        }
    } else {
        if (!state->mmu.write<T>(addr, *value, &utlb, op)) return true;
    }

    return false;
}

static inline bool PerformMemOp(EmuState* state, DecodedOp* op, mem::MicroTLB& utlb, uint32_t addr, uint32_t size,
                                std::byte* value, bool is_write) {
    struct TSize10 {
        std::byte data[10];
    };
    struct TSize16 {
        std::byte data[16];
    };

    switch (size) {
        case 1:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<uint8_t*>(value), is_write);
        case 2:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<uint16_t*>(value), is_write);
        case 4:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<uint32_t*>(value), is_write);
        case 8:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<uint64_t*>(value), is_write);
        case 10:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<TSize10*>(value),
                                is_write);  // 80-bit extended precision
        case 16:
            return ExecuteMemOp(state, op, utlb, addr, reinterpret_cast<TSize16*>(value), is_write);
        default:
            __builtin_unreachable();
    }
}

template <bool restart>
ATTR_PRESERVE_NONE int64_t MemoryOpGeneric(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                           uint32_t branch, uint64_t flags_cache) {
    mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);
    // An architectural alignment fault has already completed fault setup and
    // must not restart the same instruction or consume an older pending read.
    if (state->status != EmuStatus::Running) {
        CommitFlagsCache(state, flags_cache);
        state->clear_pending_mem_op();
        return instr_limit;
    }
    if (state->mem_op_type == PendingMemOp::Read) {
        auto& mem_op = state->mem_op.read;
        const bool fault = PerformMemOp(state, op, utlb, mem_op.addr, mem_op.size, mem_op.data.data(), false);
        if (!fault) {
            mem_op.done = true;
        } else {
            // Flags produced by earlier instructions in this block are still
            // only in the local cache. Commit them before leaving, otherwise the
            // resumed execution sees stale flags (SMC interception exits here).
            CommitFlagsCache(state, flags_cache);
            state->clear_pending_mem_op();
            return instr_limit;
        }
    } else if (state->mem_op_type == PendingMemOp::Write) {
        auto& mem_op = state->mem_op.write;
        const bool fault = PerformMemOp(state, op, utlb, mem_op.addr, mem_op.size, mem_op.data.data(), true);
        if (!fault) {
            mem_op.done = true;
        } else {
            CommitFlagsCache(state, flags_cache);
            state->clear_pending_mem_op();
            return instr_limit;
        }
    }

    mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);
    if constexpr (restart) {
        // Don't clear mem_op, it's pending for restart
        ATTR_MUSTTAIL return op->handler(state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache);
    }

    state->clear_pending_mem_op();
    DecodedOp* next_op = NextOp(op);
    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache);
}

template ATTR_PRESERVE_NONE int64_t MemoryOpGeneric<true>(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                          int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                          mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                          uint64_t flags_cache);
template ATTR_PRESERVE_NONE int64_t MemoryOpGeneric<false>(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                           int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                           mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                           uint64_t flags_cache);

ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                           uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return MemoryOpGeneric<true>(state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                         mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                         uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return MemoryOpGeneric<false>(state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache);
}

static FORCE_INLINE ATTR_PRESERVE_NONE int64_t ChainToKnownBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                 int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                                 mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                                 uint64_t flags_cache) {
    (void)op;
    (void)branch;
    BasicBlock* next_block = state->last_block;
    instr_limit -= next_block->inst_count();
    DecodedOp* next_head = next_block->FirstOp();
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    next_block->exec_count++;
#endif
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = next_head;
#endif
    __builtin_assume(next_block->entry != nullptr);
    ATTR_MUSTTAIL return next_block->entry(state, next_head, instr_limit, utlb_tags, utlb_addend,
                                           std::numeric_limits<uint32_t>::max(), flags_cache);
}

template <ExtKind Kind>
static ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowImpl(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                              mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                              uint64_t flags_cache) {
    const uint32_t target_eip = branch;
    BasicBlock* next_block = state->mmu.lookup_cached_block(target_eip);
    if (!next_block) next_block = state->mmu.invalid_code_block();

    if (!next_block->MatchesChainTarget(target_eip)) {
        CommitFlagsCache(state, flags_cache);
        state->ctx.eip = target_eip;
        return instr_limit;
    }

    if constexpr (Kind == ExtKind::Link) {
        SetNextBlock(op, next_block);
    } else {
        SetCachedTarget(op, next_block);
    }

    state->last_block = next_block;
    ATTR_MUSTTAIL return ChainToKnownBlock(state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowLink(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                       int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                       mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                       uint64_t flags_cache) {
    ATTR_MUSTTAIL return ResolveBranchTargetSlowImpl<ExtKind::Link>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                    branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowControlFlow(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                              mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                              uint64_t flags_cache) {
    ATTR_MUSTTAIL return ResolveBranchTargetSlowImpl<ExtKind::ControlFlow>(state, op, instr_limit, utlb_tags,
                                                                           utlb_addend, branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                               mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                               uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                         branch, flags_cache);
}

// Sentinel Handlers
template <int I, bool UseBranch>
ATTR_PRESERVE_NONE int64_t OpExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                       mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                       uint32_t branch, uint64_t flags_cache) {
    RecordBlockHandlersUntil(state, op);
    if constexpr (UseBranch) {
        if (branch == std::numeric_limits<uint32_t>::max()) __builtin_unreachable();
        ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::Link>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                      branch, flags_cache);
    } else {
        ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::Link>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                      op->next_eip, flags_cache);
    }
}

// Instantiate variants to reduce BTB pressure
template <size_t... Is>
static constexpr std::array<HandlerFunc, sizeof...(Is)> MakeExitHandlersFallthrough(std::index_sequence<Is...>) {
    return {OpExitBlock<static_cast<int>(Is), false>...};
}

template <size_t... Is>
static constexpr std::array<HandlerFunc, sizeof...(Is)> MakeExitHandlersBranch(std::index_sequence<Is...>) {
    return {OpExitBlock<static_cast<int>(Is), true>...};
}

std::array<HandlerFunc, kExitHandlerReplicaCount> g_ExitHandlersFallthrough =
    MakeExitHandlersFallthrough(std::make_index_sequence<kExitHandlerReplicaCount>{});
std::array<HandlerFunc, kExitHandlerReplicaCount> g_ExitHandlersBranch =
    MakeExitHandlersBranch(std::make_index_sequence<kExitHandlerReplicaCount>{});

HandlerFunc g_Handlers[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
        }

        RegisterInstructionVariants();
    }
};

// Specialization Registry
// Array of vectors for O(1) opcode lookup
static std::vector<SpecializedEntry> g_OpSpecializations[1024];

// Construct the registry before registration populates it. Both dispatch and
// specialization tables are immutable after library initialization.
static HandlerInit _init;

void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler, int priority) {
    if (opcode >= 1024) return;
    auto& list = g_OpSpecializations[opcode];
    list.push_back({opcode, criteria, handler, priority});

    // Sort by specificity (Descending Score)
    // Most specific first
    std::sort(list.begin(), list.end(), [](const SpecializedEntry& a, const SpecializedEntry& b) {
        int a_score = a.criteria.GetScore();
        int b_score = b.criteria.GetScore();
        return a_score != b_score ? a_score > b_score : a.priority < b.priority;
    });
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op) {
    if (handler_index >= 1024) return nullptr;

    // Deliberately uncached: decoded blocks already retain the selected handler,
    // and a process/thread cache would retain guest-derived data across engines.
    const auto& list = g_OpSpecializations[handler_index];
    HandlerFunc found = nullptr;

    for (const auto& entry : list) {
        // Opcode check implicit by array index
        if (!entry.criteria.Matches(op)) continue;

        found = entry.handler;
        break;  // Found the most specific match due to sorting
    }

    return found;
}

int FindOpcodeIndexForHandler(HandlerFunc handler) {
    if (!handler) return -1;

    for (int opcode = 0; opcode < 1024; ++opcode) {
        if (g_Handlers[opcode] == handler) {
            return opcode;
        }
    }

    for (int opcode = 0; opcode < 1024; ++opcode) {
        const auto& list = g_OpSpecializations[opcode];
        for (const auto& entry : list) {
            if (entry.handler == handler) {
                return opcode;
            }
        }
    }

    return -1;
}

}  // namespace fiberish
