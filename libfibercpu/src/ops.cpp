#include "ops.h"
#include <algorithm>
#include <atomic>
#include <chrono>
#include <shared_mutex>
#include "dispatch.h"
#include "instruction_variants.h"
#include "ops/ops_control_impl.h"

namespace fiberish {

extern "C" uint64_t FiberReadRealtimeTsc(const EmuState* state) {
    const auto elapsed =
        std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now() - state->tsc_start_time)
            .count();
    __extension__ unsigned __int128 ticks = static_cast<unsigned __int128>(elapsed) * state->tsc_frequency;
    return state->tsc_offset + static_cast<uint64_t>(ticks / 1000000000ULL);
}

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
            // Replay can serve a straddle from two warm pages before a
            // blocking write flushes the first page's entry.
            if (is_write ? state->mmu.try_cross_page_dword_tlb<true>(addr, value)
                         : state->mmu.try_cross_page_dword_tlb<false>(addr, value)) {
                return false;
            }
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
                                           uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    FIBERCPU_PIN_COMMIT(state);
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
        ATTR_MUSTTAIL return op->handler(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                         flags_cache FIBERCPU_PIN_RESUME);
    }

    state->clear_pending_mem_op();
    DecodedOp* next_op = NextOp(op);
    ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,
                                          flags_cache FIBERCPU_PIN_RESUME);
}

template ATTR_PRESERVE_NONE int64_t MemoryOpGeneric<true>(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                          int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                          mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                          uint64_t flags_cache FIBERCPU_PIN_PARAMS);
template ATTR_PRESERVE_NONE int64_t MemoryOpGeneric<false>(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                           int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                           mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                           uint64_t flags_cache FIBERCPU_PIN_PARAMS);

ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                           uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    ATTR_MUSTTAIL return MemoryOpGeneric<true>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                               flags_cache FIBERCPU_PIN_RELOAD);
}

ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                         mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                         uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    ATTR_MUSTTAIL return MemoryOpGeneric<false>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                flags_cache FIBERCPU_PIN_RELOAD);
}

static FORCE_INLINE ATTR_PRESERVE_NONE int64_t ChainToKnownBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                                 int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                                 mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                                 uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    (void)op;
    (void)branch;
    BasicBlock* next_block = state->last_block;
    instr_limit -= next_block->inst_count();
    DecodedOp* next_head = next_block->FirstOp();
    CountLLVMBlockEntry(state, next_block, next_block->chain);
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    next_block->exec_count++;
#endif
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    state->current_block_head = next_head;
#endif
    __builtin_assume(next_block->entry != nullptr);
    ATTR_MUSTTAIL return next_block->entry(state, next_head, instr_limit, utlb_tags, utlb_addend,
                                           std::numeric_limits<uint32_t>::max(), flags_cache FIBERCPU_PIN_RELOAD);
}

template <ExtKind Kind>
static ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowImpl(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                              mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                              uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    const uint32_t target_eip = branch;
    BasicBlock* next_block = state->mmu.lookup_cached_block(target_eip);
    if (!next_block) next_block = state->mmu.invalid_code_block();

    if (!next_block->MatchesChainTarget(target_eip)) {
        FIBERCPU_PIN_COMMIT(state);
        CommitFlagsCache(state, flags_cache);
        state->ctx.eip = target_eip;
        return instr_limit;
    }

    if constexpr (Kind == ExtKind::Link) {
        state->last_block->SetFallthroughLink(next_block);
    } else if (auto** cached = state->last_block->BranchLink(op)) {
        *cached = next_block;
    }

    state->last_block = next_block;
    ATTR_MUSTTAIL return ChainToKnownBlock(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                           flags_cache FIBERCPU_PIN_RELOAD);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowLink(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                       int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                       mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                       uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    ATTR_MUSTTAIL return ResolveBranchTargetSlowImpl<ExtKind::Link>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                    branch, flags_cache FIBERCPU_PIN_RELOAD);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTargetSlowControlFlow(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                              int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                              mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                              uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    ATTR_MUSTTAIL return ResolveBranchTargetSlowImpl<ExtKind::ControlFlow>(
        state, op, instr_limit, utlb_tags, utlb_addend, branch, flags_cache FIBERCPU_PIN_RELOAD);
}

ATTR_PRESERVE_NONE int64_t ResolveBranchTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                               mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                               uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    FIBERCPU_PIN_ASSUME;
    ATTR_MUSTTAIL return ResolveBranchTargetInline<ExtKind::ControlFlow>(state, op, instr_limit, utlb_tags, utlb_addend,
                                                                         branch, flags_cache FIBERCPU_PIN_RELOAD);
}

// Scalar handlers use physical next-slot dispatch. The data slot only forwards the
// threaded state, and logical traversals skip it.
ATTR_PRESERVE_NONE int64_t ForwardMemoryImmediateSlot(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                      int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                      mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                      uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    auto* next = op + 1;
    ATTR_MUSTTAIL return next->handler(state, next, instr_limit, utlb_tags, utlb_addend, branch,
                                       flags_cache FIBERCPU_PIN_ARGS);
}

// Sentinel Handlers
template <int I, bool UseBranch>
ATTR_PRESERVE_NONE int64_t OpExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                       mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                       uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS) {
    ATTR_MUSTTAIL return OpExitBlockImpl<UseBranch>(state, op, instr_limit, utlb_tags, utlb_addend, branch,
                                                    flags_cache FIBERCPU_PIN_RELOAD);
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
static void BuildSpecializationIndex();
static std::atomic<bool> g_SpecializationReady{false};

struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
        }

        RegisterInstructionVariants();
        BuildSpecializationIndex();
        g_SpecializationReady.store(true, std::memory_order_release);
    }
};

// Specialization Registry
// Array of vectors for O(1) opcode lookup
static std::vector<SpecializedEntry> g_OpSpecializations[1024];

// Construct the registry before registration populates it. Both dispatch and
// specialization tables are immutable after library initialization.
struct CompiledSpecialization {
    HandlerFunc handler;
    uint32_t ea_mask;
    uint32_t ea_value;
    uint16_t prefix_mask;
    uint16_t prefix_value;
    bool requires_mem;
};
// Pack the decoded-memory fields once; values outside the constrained field
// bits cannot match the reference predicate and are discarded.
static bool CompileSpecialization(const SpecCriteria& c, HandlerFunc handler, CompiledSpecialization& out) {
    const uint32_t base = c.base_offset_mask & 0x3f;
    const uint32_t index = c.index_offset_mask & 0x3f;
    const uint32_t scale = c.scale_mask & 0x03;
    const uint32_t segment = c.segment_mask & 0x07;
    if ((c.base_offset_mask && (c.base_offset_val & ~base)) || (c.index_offset_mask && (c.index_offset_val & ~index)) ||
        (c.scale_mask && (c.scale_val & ~scale)) || (c.segment_mask && (c.segment_val & ~segment)))
        return false;
    const uint32_t mask = (base << memdesc::kBaseShift) | (index << memdesc::kIndexShift) |
                          (scale << memdesc::kScaleShift) | (segment << memdesc::kSegmentShift);
    const uint32_t value = ((c.base_offset_mask ? c.base_offset_val : 0u) << memdesc::kBaseShift) |
                           ((c.index_offset_mask ? c.index_offset_val : 0u) << memdesc::kIndexShift) |
                           ((c.scale_mask ? c.scale_val : 0u) << memdesc::kScaleShift) |
                           ((c.segment_mask ? c.segment_val : 0u) << memdesc::kSegmentShift);
    out = {handler, mask, value, c.prefix_mask, c.prefix_val, c.RequiresMemShape() || c.memory_only};
    return true;
}

static bool MatchesCompiled(const CompiledSpecialization& entry, const DecodedInstruction* op) {
    if (entry.prefix_mask && (op->prefixes.all & entry.prefix_mask) != entry.prefix_value) return false;
    return !entry.requires_mem ||
           (op->meta.flags.has_mem && (GetExt(op)->data.ea_desc & entry.ea_mask) == entry.ea_value);
}

static std::vector<CompiledSpecialization> g_CompiledSpecializations[1024];
using SpecializationBuckets = std::array<std::vector<const CompiledSpecialization*>, 514>;
static std::unique_ptr<SpecializationBuckets> g_SpecializationIndex[1024];
struct RuntimeSpecializations {
    std::vector<SpecializedEntry> entries;
    std::vector<CompiledSpecialization> compiled;
    SpecializationBuckets buckets;
};
static std::array<std::unique_ptr<RuntimeSpecializations>, 1024> g_RuntimeSpecializations;
static std::shared_mutex g_RuntimeSpecializationMutex;
static std::atomic<bool> g_HasRuntimeSpecializations{false};
static HandlerInit _init;

static void SortSpecializations(std::vector<SpecializedEntry>& list) {
    std::sort(list.begin(), list.end(), [](const SpecializedEntry& a, const SpecializedEntry& b) {
        int a_score = a.criteria.GetScore();
        int b_score = b.criteria.GetScore();
        return a_score != b_score ? a_score > b_score : a.priority < b.priority;
    });
}

static void IndexSpecialization(const SpecCriteria& c, const CompiledSpecialization* optimized,
                                SpecializationBuckets& buckets) {
    for (unsigned encoded = 0; encoded <= 256; ++encoded) {
        const bool has_modrm = encoded != 0;
        const unsigned modrm = has_modrm ? encoded - 1 : 0;
        if (!has_modrm && (c.mod_mask || c.reg_mask || c.rm_mask)) continue;
        if (has_modrm && ((c.mod_mask && (((modrm >> 6) & 3) & c.mod_mask) != c.mod_val) ||
                          (c.reg_mask && (((modrm >> 3) & 7) & c.reg_mask) != c.reg_val) ||
                          (c.rm_mask && ((modrm & 7) & c.rm_mask) != c.rm_val)))
            continue;
        if (c.any_flags) {
            buckets[encoded * 2].push_back(optimized);
            buckets[encoded * 2 + 1].push_back(optimized);
        } else {
            buckets[encoded * 2 + c.no_flags].push_back(optimized);
        }
    }
}

static void CompileSpecializationList(const std::vector<SpecializedEntry>& entries,
                                      std::vector<CompiledSpecialization>& compiled, SpecializationBuckets& buckets) {
    compiled.reserve(entries.size());
    for (const auto& entry : entries) {
        CompiledSpecialization item{};
        if (!CompileSpecialization(entry.criteria, entry.handler, item)) continue;
        compiled.push_back(item);
        IndexSpecialization(entry.criteria, &compiled.back(), buckets);
    }
}

void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler, int priority) {
    if (opcode >= 1024 || !handler) return;
    if (!g_SpecializationReady.load(std::memory_order_acquire)) {
        auto& list = g_OpSpecializations[opcode];
        list.push_back({opcode, criteria, handler, priority});
        SortSpecializations(list);
        return;
    }
    std::unique_lock lock(g_RuntimeSpecializationMutex);
    auto& current = g_RuntimeSpecializations[opcode];
    auto updated = std::make_unique<RuntimeSpecializations>();
    updated->entries = current ? current->entries : g_OpSpecializations[opcode];
    updated->entries.push_back({opcode, criteria, handler, priority});
    SortSpecializations(updated->entries);
    CompileSpecializationList(updated->entries, updated->compiled, updated->buckets);
    current = std::move(updated);
    g_HasRuntimeSpecializations.store(true, std::memory_order_release);
}

// Registration is complete before this is called. Buckets retain the exact
// sorted order, including ties; remaining prefix and memory constraints are
// checked after lookup. The extra ModRM value represents an absent ModRM.
static void BuildSpecializationIndex() {
    for (size_t opcode = 0; opcode < 1024; ++opcode) {
        const auto& list = g_OpSpecializations[opcode];
        if (list.empty()) continue;
        auto buckets = std::make_unique<SpecializationBuckets>();
        auto& compiled = g_CompiledSpecializations[opcode];
        CompileSpecializationList(list, compiled, *buckets);
        g_SpecializationIndex[opcode] = std::move(buckets);
    }
}

static HandlerFunc LookupSpecialization(const SpecializationBuckets& buckets, const DecodedInstruction* op) {
    const unsigned encoded = op->meta.flags.has_modrm ? unsigned(op->modrm) + 1 : 0;
    const auto& bucket = buckets[encoded * 2 + op->meta.flags.no_flags];
    for (const auto* entry : bucket) {
        if (!MatchesCompiled(*entry, op)) continue;
        return entry->handler;
    }
    return nullptr;
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedInstruction* op) {
    if (handler_index >= 1024) return nullptr;
    if (g_HasRuntimeSpecializations.load(std::memory_order_acquire)) {
        std::shared_lock lock(g_RuntimeSpecializationMutex);
        if (const auto& index = g_RuntimeSpecializations[handler_index])
            return LookupSpecialization(index->buckets, op);
    }
    const auto& index = g_SpecializationIndex[handler_index];
    return index ? LookupSpecialization(*index, op) : nullptr;
}

#ifdef FIBERCPU_VALIDATE_SPECIALIZATION_INDEX
bool ValidateSpecializationWildcardIndex() {
    const HandlerFunc handlers[] = {g_Handlers[0x90], g_Handlers[0xF4], g_Handlers[0xCD]};
    SpecCriteria rules[3]{};
    rules[0].any_flags = true;
    rules[0].memory_only = true;
    rules[1].no_flags = true;
    rules[2].any_flags = true;
    CompiledSpecialization compiled[3]{};
    SpecializationBuckets buckets;
    for (unsigned i = 0; i < 3; ++i) {
        if (!CompileSpecialization(rules[i], handlers[i], compiled[i])) return false;
        IndexSpecialization(rules[i], &compiled[i], buckets);
    }
    for (unsigned encoded = 0; encoded <= 256; ++encoded) {
        for (unsigned flags = 0; flags < 2; ++flags) {
            for (unsigned memory = 0; memory < 2; ++memory) {
                DecodedInstruction op{};
                op.meta.flags.has_modrm = encoded != 0;
                op.modrm = encoded ? encoded - 1 : 0;
                op.meta.flags.no_flags = flags;
                op.meta.flags.has_mem = memory;
                HandlerFunc expected = nullptr;
                for (unsigned i = 0; i < 3; ++i)
                    if (rules[i].Matches(&op)) {
                        expected = handlers[i];
                        break;
                    }
                HandlerFunc actual = nullptr;
                for (const auto* entry : buckets[encoded * 2 + flags])
                    if (MatchesCompiled(*entry, &op)) {
                        actual = entry->handler;
                        break;
                    }
                if (actual != expected) return false;
            }
        }
    }
    return true;
}
bool MatchesCompiledForTest(const SpecCriteria& criteria, const DecodedInstruction* op) {
    CompiledSpecialization entry{};
    return CompileSpecialization(criteria, nullptr, entry) && MatchesCompiled(entry, op);
}
HandlerFunc FindSpecializedHandlerReference(uint16_t opcode, DecodedInstruction* op) {
    if (opcode >= 1024) return nullptr;
    for (const auto& entry : g_OpSpecializations[opcode])
        if (entry.criteria.Matches(op)) return entry.handler;
    return nullptr;
}
#endif

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

    return FindGenericOpcodeIndex(handler);
}

}  // namespace fiberish
