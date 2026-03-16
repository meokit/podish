#include "ops.h"
#include <algorithm>
#include "dispatch.h"
#include "ops/ops_mmx.h"

namespace fiberish {

#ifdef FIBERCPU_ENABLE_JCC_PROFILE
static FORCE_INLINE void RecordConditionalBranchCacheResult(EmuState* state, const DecodedOp* op, bool cache_hit) {
    if (!op->meta.flags.is_conditional_branch) return;
    auto& counters = state->jcc_profile_counts[reinterpret_cast<uintptr_t>(op->handler)];
    if (cache_hit) {
        counters.cache_hit++;
    } else {
        counters.cache_miss++;
    }
}
#else
static FORCE_INLINE void RecordConditionalBranchCacheResult(EmuState*, const DecodedOp*, bool) {}
#endif

template <bool restart>
ATTR_PRESERVE_NONE int64_t MemoryOpGeneric(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    bool fault = false;

    auto execute_mem_op = [&]<typename T>(uint32_t addr, T* value, bool is_write) {
        if (!is_write) {
            auto res = state->mmu.read<T>(addr, &utlb, op);
            if (!res) {
                fault = true;
            } else {
                *value = *res;
            }
        } else {
            if (!state->mmu.write<T>(addr, *value, &utlb, op)) fault = true;
        }
    };

    auto perform = [&](uint32_t addr, uint32_t size, std::byte* value, bool is_write) {
        struct TSize10 {
            std::byte data[10];
        };
        struct TSize16 {
            std::byte data[16];
        };

        switch (size) {
            case 1:
                execute_mem_op(addr, reinterpret_cast<uint8_t*>(value), is_write);
                break;
            case 2:
                execute_mem_op(addr, reinterpret_cast<uint16_t*>(value), is_write);
                break;
            case 4:
                execute_mem_op(addr, reinterpret_cast<uint32_t*>(value), is_write);
                break;
            case 8:
                execute_mem_op(addr, reinterpret_cast<uint64_t*>(value), is_write);
                break;
            case 10:
                execute_mem_op(addr, reinterpret_cast<TSize10*>(value), is_write);
                break;  // 80-bit extended precision
            case 16:
                execute_mem_op(addr, reinterpret_cast<TSize16*>(value), is_write);
                break;
            default:
                __builtin_unreachable();
        }
    };

    std::visit(
        [&](auto&& arg) {
            using T = std::decay_t<decltype(arg)>;
            if constexpr (std::is_same_v<T, std::monostate>) {
                // No-op
            } else if constexpr (std::is_same_v<T, MemReadOperation>) {
                perform(arg.addr, arg.size, arg.data.data(), false);
                if (!fault) {
                    arg.done = true;
                }
            } else if constexpr (std::is_same_v<T, MemWriteOperation>) {
                perform(arg.addr, arg.size, arg.data.data(), true);
                if (!fault) {
                    arg.done = true;
                }
            }
        },
        state->mem_op);

    // Handle fault or restart
    if (fault) {
        // Break calling chain
        // Note: eip will be synced on read or write
        state->mem_op.emplace<0>();
        return instr_limit;
    }

    if constexpr (restart) {
        // Don't clear mem_op, it's pending for restart
        ATTR_MUSTTAIL return op->handler(state, op, instr_limit, utlb, branch, flags_cache);
    }

    state->mem_op.emplace<0>();
    DecodedOp* next_op = NextOp(op);
    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return MemoryOpGeneric<true>(state, op, instr_limit, utlb, branch, flags_cache);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                         mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return MemoryOpGeneric<false>(state, op, instr_limit, utlb, branch, flags_cache);
}

static ATTR_PRESERVE_NONE int64_t ChainToKnownBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                    int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                    uint64_t flags_cache) {
    BasicBlock* next_block = state->last_block;
    instr_limit -= next_block->inst_count;
    DecodedOp* next_head = next_block->FirstOp();
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    next_block->exec_count++;
    state->current_block_head = next_head;
#endif
    if (next_block->entry != nullptr) {
        ATTR_MUSTTAIL return next_block->entry(state, next_head, instr_limit, utlb,
                                               std::numeric_limits<uint32_t>::max(), flags_cache);
    }
    CommitFlagsCache(state, flags_cache);
    state->ctx.eip = branch != std::numeric_limits<uint32_t>::max() ? branch : op->next_eip;
    return instr_limit;
}

template <ExtKind Kind>
static ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowImpl(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                              uint64_t flags_cache) {
    const uint32_t target_eip = branch;

    RecordConditionalBranchCacheResult(state, op, false);
    auto it = state->block_cache.find(target_eip);
    BasicBlock* next_block = it == state->block_cache.end() ? &state->dummy_invalid_block : it->second;

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
    ATTR_MUSTTAIL return ChainToKnownBlock(state, op, instr_limit, utlb, branch, flags_cache);
}

template <ExtKind Kind>
static FORCE_INLINE ATTR_PRESERVE_NONE int64_t ResolveBranchTargetImpl(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                       int64_t instr_limit, mem::MicroTLB utlb,
                                                                       uint32_t target_eip, uint64_t flags_cache) {
    state->mem_op.emplace<0>();
    if (instr_limit <= 0) {
        CommitFlagsCache(state, flags_cache);
        state->ctx.eip = target_eip;
        return instr_limit;
    }

    BasicBlock* next_block;
    if constexpr (Kind == ExtKind::Link) {
        next_block = GetNextBlock(op);
    } else {
        static_assert(Kind == ExtKind::ControlFlow);
        next_block = GetCachedTarget(op);
    }

    // Decode pre-fills control-flow caches with dummy_invalid_block, so cached pointers are never null here.
    if (next_block->MatchesChainTarget(target_eip)) [[likely]] {
        RecordConditionalBranchCacheResult(state, op, true);
        state->last_block = next_block;
        ATTR_MUSTTAIL return ChainToKnownBlock(state, op, instr_limit, utlb, target_eip, flags_cache);
    }

    ATTR_MUSTTAIL return ResolveBranchTargetSlowImpl<Kind>(state, op, instr_limit, utlb, target_eip, flags_cache);
}

ATTR_PRESERVE_NONE int64_t ResolveSentinelTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    const uint32_t target_eip = branch != std::numeric_limits<uint32_t>::max() ? branch : op->next_eip;
    ATTR_MUSTTAIL return ResolveBranchTargetImpl<ExtKind::Link>(state, op, instr_limit, utlb, target_eip, flags_cache);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                               mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return ResolveBranchTargetImpl<ExtKind::ControlFlow>(state, op, instr_limit, utlb, branch,
                                                                       flags_cache);
}

// Sentinel Handler
template <int I>
ATTR_PRESERVE_NONE int64_t OpExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                       mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache) {
    ATTR_MUSTTAIL return ExitBlock(state, op, instr_limit, utlb, branch, flags_cache);
}

// Instantiate variants to reduce BTB pressure
#define INSTANTIATE_EXIT(i) OpExitBlock<i>
HandlerFunc g_ExitHandlers[32] = {
    INSTANTIATE_EXIT(0),  INSTANTIATE_EXIT(1),  INSTANTIATE_EXIT(2),  INSTANTIATE_EXIT(3),  INSTANTIATE_EXIT(4),
    INSTANTIATE_EXIT(5),  INSTANTIATE_EXIT(6),  INSTANTIATE_EXIT(7),  INSTANTIATE_EXIT(8),  INSTANTIATE_EXIT(9),
    INSTANTIATE_EXIT(10), INSTANTIATE_EXIT(11), INSTANTIATE_EXIT(12), INSTANTIATE_EXIT(13), INSTANTIATE_EXIT(14),
    INSTANTIATE_EXIT(15), INSTANTIATE_EXIT(16), INSTANTIATE_EXIT(17), INSTANTIATE_EXIT(18), INSTANTIATE_EXIT(19),
    INSTANTIATE_EXIT(20), INSTANTIATE_EXIT(21), INSTANTIATE_EXIT(22), INSTANTIATE_EXIT(23), INSTANTIATE_EXIT(24),
    INSTANTIATE_EXIT(25), INSTANTIATE_EXIT(26), INSTANTIATE_EXIT(27), INSTANTIATE_EXIT(28), INSTANTIATE_EXIT(29),
    INSTANTIATE_EXIT(30), INSTANTIATE_EXIT(31)};

HandlerFunc g_Handlers[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
        }

        // 2. Register all modular operations
        RegisterAluOps();
        RegisterCompareOps();
        RegisterControlOps();
        RegisterDataMovOps();
        RegisterDoubleShiftOps();
        RegisterFpuOps();
        RegisterGroupOps();
        RegisterMmxOps();
        RegisterMulDivOps();
        RegisterShiftBitOps();
        RegisterSseCvtOps();
        RegisterSseFpOps();
        RegisterSseIntOps();
        RegisterSseMovOps();
    }
};

// Static instance to trigger initialization
static HandlerInit _init;

// Specialization Registry
// Array of vectors for O(1) opcode lookup
static std::vector<SpecializedEntry> g_OpSpecializations[1024];
// Lookup Cache to accelerate finding specialized handlers
// Thread-local for lock-free access
static thread_local ankerl::unordered_dense::map<uint64_t, HandlerFunc> g_SpecCache;

void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler) {
    if (opcode >= 1024) return;
    auto& list = g_OpSpecializations[opcode];
    list.push_back({opcode, criteria, handler});

    // Sort by specificity (Descending Score)
    // Most specific first
    std::sort(list.begin(), list.end(), [](const SpecializedEntry& a, const SpecializedEntry& b) {
        int a_score = a.criteria.GetScore();
        int b_score = b.criteria.GetScore();
        return a_score > b_score;
    });

    // Clear cache on registration?
    // Since cache is thread_local and registration happens at startup (main thread),
    // clearing main thread's cache is fine but others start empty anyway.
    // We can skip explicit clear here as it's unlikely to have stale entries during static init.
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op) {
    if (handler_index >= 1024) return nullptr;

    // Construct Cache Key
    // Layout:
    // [0-9]   Opcode (10 bits)
    // [10-17] ModRM (8 bits)
    // [18-33] Prefixes (16 bits)
    // [34]    HasModRM (1 bit)
    // [35]    NoFlags (1 bit)
    uint64_t key = handler_index;

    if (op->meta.flags.has_modrm) {
        key |= ((uint64_t)op->modrm << 10);
        key |= (1ULL << 34);
    }
    key |= ((uint64_t)op->prefixes.all << 18);
    if (op->meta.flags.no_flags) key |= (1ULL << 35);

    // Check Cache
    auto it = g_SpecCache.find(key);
    if (it != g_SpecCache.end()) return it->second;

    // Lookup
    const auto& list = g_OpSpecializations[handler_index];
    HandlerFunc found = nullptr;

    for (const auto& entry : list) {
        // Opcode check implicit by array index

        // 1. Prefix Check always
        if (entry.criteria.prefix_mask) {
            if ((op->prefixes.all & entry.criteria.prefix_mask) != entry.criteria.prefix_val) continue;
        }

        // 2. ModRM Check
        if (op->meta.flags.has_modrm) {
            // Standard check
            if (!entry.criteria.Matches(op->modrm, op->prefixes.all, op->meta.flags.no_flags)) continue;
        } else {
            // No ModRM in instruction.
            // If criteria REQUIRES ModRM specific values (mask != 0), then it's a mismatch.
            if (entry.criteria.mod_mask || entry.criteria.reg_mask || entry.criteria.rm_mask) continue;

            // Still check no_flags even without ModRM
            if (entry.criteria.no_flags != op->meta.flags.no_flags) continue;
        }

        found = entry.handler;
        break;  // Found the most specific match due to sorting
    }

    // Update Cache
    g_SpecCache[key] = found;
    return found;
}

}  // namespace fiberish
