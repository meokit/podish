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
static void BuildSpecializationIndex();

struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
        }

        RegisterInstructionVariants();
        BuildSpecializationIndex();
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

static bool MatchesCompiled(const CompiledSpecialization& entry, const DecodedOp* op) {
    if (entry.prefix_mask && (op->prefixes.all & entry.prefix_mask) != entry.prefix_value) return false;
    return !entry.requires_mem ||
           (op->meta.flags.has_mem && (GetExt(op)->data.ea_desc & entry.ea_mask) == entry.ea_value);
}

static std::vector<CompiledSpecialization> g_CompiledSpecializations[1024];
using SpecializationBuckets = std::array<std::vector<const CompiledSpecialization*>, 514>;
static std::unique_ptr<SpecializationBuckets> g_SpecializationIndex[1024];
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

// Registration is complete before this is called. Buckets retain the exact
// sorted order, including ties; remaining prefix and memory constraints are
// checked after lookup. The extra ModRM value represents an absent ModRM.
static void BuildSpecializationIndex() {
    for (size_t opcode = 0; opcode < 1024; ++opcode) {
        const auto& list = g_OpSpecializations[opcode];
        if (list.empty()) continue;
        auto buckets = std::make_unique<SpecializationBuckets>();
        auto& compiled = g_CompiledSpecializations[opcode];
        compiled.reserve(list.size());
        for (const auto& entry : list) {
            const auto& c = entry.criteria;
            CompiledSpecialization item{};
            if (!CompileSpecialization(c, entry.handler, item)) continue;
            compiled.push_back(item);
            const auto* optimized = &compiled.back();
            IndexSpecialization(c, optimized, *buckets);
        }
        g_SpecializationIndex[opcode] = std::move(buckets);
    }
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op) {
    if (handler_index >= 1024 || !g_SpecializationIndex[handler_index]) return nullptr;
    const unsigned encoded = op->meta.flags.has_modrm ? unsigned(op->modrm) + 1 : 0;
    const auto& bucket = (*g_SpecializationIndex[handler_index])[encoded * 2 + op->meta.flags.no_flags];
    for (const auto* entry : bucket) {
        if (!MatchesCompiled(*entry, op)) continue;
        return entry->handler;
    }
    return nullptr;
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
                DecodedOp op{};
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
bool MatchesCompiledForTest(const SpecCriteria& criteria, const DecodedOp* op) {
    CompiledSpecialization entry{};
    return CompileSpecialization(criteria, nullptr, entry) && MatchesCompiled(entry, op);
}
HandlerFunc FindSpecializedHandlerReference(uint16_t opcode, DecodedOp* op) {
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

    return -1;
}

}  // namespace fiberish
