#include "instruction_variants.h"
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include "instruction_catalog_hash.h"
#include "ops/ops_alu_impl.h"
#include "ops/ops_compare_impl.h"
#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_double_shift_impl.h"
#include "ops/ops_fpu_impl.h"
#include "ops/ops_groups_impl.h"
#include "ops/ops_mmx_impl.h"
#include "ops/ops_muldiv_impl.h"
#include "ops/ops_shift_bit_impl.h"
#include "ops/ops_sse_cvt_impl.h"
#include "ops/ops_sse_fp_impl.h"
#include "ops/ops_sse_int_impl.h"
#include "ops/ops_sse_mov_impl.h"

namespace fiberish {
namespace {
struct Fallback {
    uint16_t opcode;
    HandlerFunc handler;
    const char* logic;
};
using FusionAlias = std::pair<HandlerFunc, HandlerFunc>;
#include "instruction_catalog.inc"

struct TrainingOperation {
    uint32_t id;
    HandlerFunc handler;
    const char* name;
    HandlerFunc slots[2];
};
constexpr TrainingOperation training_operations[] = {
#include "training_scalar_handlers.inc"
};

const auto& ScalarBindings() {
    static const auto bindings = [] {
        ankerl::unordered_dense::map<HandlerFunc, const TrainingOperation*> result;
        result.reserve(3 * std::size(training_operations));
        for (const auto& op : training_operations) {
            result.try_emplace(op.handler, &op);
            result.try_emplace(op.slots[0], &op);
            result.try_emplace(op.slots[1], &op);
        }
        return result;
    }();
    return bindings;
}

std::vector<X86_InstructionVariant>& MutableCatalog() {
    // Also safe during dispatch table initialization across translation units.
    static std::vector<X86_InstructionVariant> entries;
    return entries;
}
uint64_t VariantKey(uint16_t opcode, const char* logic) {
    // Stable identity, independent of symbol addresses and registration order.
    uint64_t h = 14695981039346656037ULL;
    for (auto v : {static_cast<uint8_t>(opcode), static_cast<uint8_t>(opcode >> 8)}) h = (h ^ v) * 1099511628211ULL;
    for (; *logic; ++logic) h = (h ^ static_cast<uint8_t>(*logic)) * 1099511628211ULL;
    return h;
}
void AppendMetadata(uint16_t opcode, HandlerFunc handler, const char* logic, const char* family, const char* parent,
                    bool bake, bool fallback, int rank, SpecCriteria c = {}) {
    X86_InstructionVariant v{};
    v.schema_version = 1;
    v.opcode = opcode;
    v.handler = reinterpret_cast<void*>(handler);
    v.logic_name = logic;
    v.family_name = family;
    v.fusion_parent = parent;
    v.variant_key = VariantKey(opcode, logic);
    v.bake_with_parent = bake;
    v.is_fallback = fallback;
    v.priority = rank;
    const uint32_t constraints[] = {c.mod_mask,
                                    c.mod_val,
                                    c.reg_mask,
                                    c.reg_val,
                                    c.rm_mask,
                                    c.rm_val,
                                    c.prefix_mask,
                                    c.prefix_val,
                                    uint32_t(c.no_flags),
                                    c.base_offset_mask,
                                    c.base_offset_val,
                                    c.index_offset_mask,
                                    c.index_offset_val,
                                    c.scale_mask,
                                    c.scale_val,
                                    c.segment_mask,
                                    c.segment_val,
                                    uint32_t(c.any_flags),
                                    uint32_t(c.memory_only)};
    for (size_t i = 0; i < 19; ++i) v.constraints[i] = constraints[i];
    // Include normalized constraints in the key: one logic entry can have several selectors.
    for (auto value : constraints) v.variant_key = (v.variant_key ^ value) * 1099511628211ULL;
    MutableCatalog().push_back(v);
}
}  // namespace

const char* InstructionCatalogHash() { return FIBERCPU_INSTRUCTION_CATALOG_SHA256; }

const std::vector<X86_InstructionVariant>& InstructionVariantCatalog() { return MutableCatalog(); }

void RegisterInstructionVariant(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler, const char* logic,
                                const char* family, const char* parent, bool bake, int rank) {
    RegisterSpecializedHandler(opcode, criteria, handler, rank);
    AppendMetadata(opcode, handler, logic, family, parent, bake, false, rank, criteria);
}
void RegisterInstructionFallback(uint16_t opcode, HandlerFunc handler, const char* logic) {
    g_Handlers[opcode] = handler;
    AppendMetadata(opcode, handler, logic, logic, logic, false, true, 0);
}

void RegisterInstructionVariants() {
    for (const auto& f : fallbacks) RegisterInstructionFallback(f.opcode, f.handler, f.logic);
    for (const auto& v : variants)
        RegisterInstructionVariant(v.opcode, v.criteria, v.handler, v.logic, v.family, v.parent, v.bake, v.rank);
}

HandlerFunc FindGenericInstructionHandler(uint16_t opcode, const DecodedInstruction* op) {
    const auto first = std::ranges::lower_bound(semantic_variant_indices, opcode, {},
                                                [](uint16_t index) { return variants[index].opcode; });
    const DeclaredInstructionVariant* best = nullptr;
    for (auto it = first; it != std::end(semantic_variant_indices); ++it) {
        const auto& candidate = variants[*it];
        if (candidate.opcode != opcode) break;
        if (!candidate.criteria.Matches(op)) continue;
        const auto score = candidate.criteria.GetScore();
        if (!best || score > best->criteria.GetScore() ||
            (score == best->criteria.GetScore() && candidate.rank < best->rank))
            best = &candidate;
    }
    if (best) return best->handler;
    const auto fallback = std::ranges::lower_bound(generic_fallbacks, opcode, {}, &Fallback::opcode);
    return fallback != std::end(generic_fallbacks) && fallback->opcode == opcode ? fallback->handler : nullptr;
}

int FindGenericOpcodeIndex(HandlerFunc handler) {
    for (const auto& fallback : generic_fallbacks) {
        if (fallback.handler == handler) return fallback.opcode;
    }
    return -1;
}

BoundScalarHandler BindScalarHandler(const DecodedInstruction& op) {
    const auto shape = BoundOperandShape::Read(op);
    const auto& bindings = ScalarBindings();
    const auto it = bindings.find(op.handler);
    if (it == bindings.end()) return {nullptr, 0, shape};
    return {it->second->slots[shape.slots - 1], it->second->id, shape};
}

HandlerFunc CanonicalBoundHandler(HandlerFunc handler) {
    const auto& bindings = ScalarBindings();
    const auto it = bindings.find(handler);
    return it == bindings.end() ? handler : it->second->handler;
}

uint32_t FindScalarTrainingId(HandlerFunc handler) {
    const auto& bindings = ScalarBindings();
    const auto it = bindings.find(handler);
    return it == bindings.end() ? 0 : it->second->id;
}

uint32_t TrainingOperationIdAt(uint32_t index) {
    return index < std::size(training_operations) ? training_operations[index].id : 0;
}

const char* FindTrainingOperationName(uint32_t id) {
    const auto it = std::ranges::lower_bound(training_operations, id, {}, &TrainingOperation::id);
    return it != std::end(training_operations) && it->id == id ? it->name : nullptr;
}

HandlerFunc FindFusionFallbackHandler(HandlerFunc handler) {
    if constexpr (std::size(fusion_aliases) == 1) return handler;
    static const ankerl::unordered_dense::map<HandlerFunc, HandlerFunc> aliases(std::begin(fusion_aliases),
                                                                                std::end(fusion_aliases) - 1);
    if (const auto it = aliases.find(handler); it != aliases.end()) return it->second;
    return handler;
}
}  // namespace fiberish
