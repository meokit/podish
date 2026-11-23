#include "instruction_variants.h"
#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <array>
#include <bit>
#include <cstdio>
#include <cstdlib>
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
#include "ops/ops_sha_impl.h"
#include "ops/ops_shift_bit_impl.h"
#include "ops/ops_sse_cvt_impl.h"
#include "ops/ops_sse_fp_impl.h"
#include "ops/ops_sse_int_impl.h"
#include "ops/ops_sse_mov_impl.h"
#include "scalar_bindings.h"

namespace fiberish {
#define FIBERCPU_SCALAR(Name, Id) extern const ScalarBindingTable Name##_scalar;
#include "training_scalar_handlers.inc"
#undef FIBERCPU_SCALAR

namespace {
struct Fallback {
    uint16_t opcode;
    HandlerFunc handler;
    const char* logic;
};
#include "instruction_catalog.inc"

struct TrainingOperation {
    uint32_t id;
    HandlerFunc handler;
    const char* name;
    const ScalarBindingTable* scalar;
};
constexpr TrainingOperation training_operations[] = {
#define FIBERCPU_SCALAR(Name, Id) {Id, DispatchWrapper<op::Name>, "op::" #Name, &Name##_scalar},
#include "training_scalar_handlers.inc"
#undef FIBERCPU_SCALAR
};

struct ScalarBinding {
    HandlerFunc handler = nullptr;
    const TrainingOperation* operation = nullptr;
};

constexpr size_t kScalarBindingCount = std::bit_ceil(6 * std::size(training_operations));

size_t ScalarBindingIndex(HandlerFunc handler) {
    const auto address = reinterpret_cast<uintptr_t>(handler) >> 2;
    return (address ^ (address >> 13)) & (kScalarBindingCount - 1);
}

const TrainingOperation* FindScalarBinding(HandlerFunc handler) {
    static const auto bindings = [] {
        std::array<ScalarBinding, kScalarBindingCount> result{};
        for (const auto& op : training_operations) {
            auto index = ScalarBindingIndex(op.handler);
            while (result[index].handler && result[index].handler != op.handler)
                index = (index + 1) & (kScalarBindingCount - 1);
            if (!result[index].handler) result[index] = {op.handler, &op};
        }
        return result;
    }();
    auto index = ScalarBindingIndex(handler);
    while (bindings[index].handler && bindings[index].handler != handler)
        index = (index + 1) & (kScalarBindingCount - 1);
    if (const auto* operation = bindings[index].operation) return operation;
    static const auto executable = [] {
        ankerl::unordered_dense::map<HandlerFunc, const TrainingOperation*> result;
        for (const auto& operation : training_operations) {
            for (auto fallback : operation.scalar->fallback)
                if (fallback) result.emplace(fallback, &operation);
            for (const auto& entry : std::span(operation.scalar->entries, operation.scalar->count))
                result.emplace(entry.handler, &operation);
            for (const auto& entry : operation.scalar->exact)
                result.emplace(entry.handler, &operation);
        }
        return result;
    }();
    const auto match = executable.find(handler);
    return match == executable.end() ? nullptr : match->second;
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
    const auto first = std::ranges::lower_bound(variants, opcode, {}, &DeclaredInstructionVariant::opcode);
    const DeclaredInstructionVariant* best = nullptr;
    for (auto it = first; it != std::end(variants); ++it) {
        const auto& candidate = *it;
        if (candidate.opcode != opcode) break;
        if (!candidate.criteria.Matches(op)) continue;
        const auto score = candidate.criteria.GetScore();
        if (!best || score > best->criteria.GetScore() ||
            (score == best->criteria.GetScore() && candidate.rank < best->rank))
            best = &candidate;
    }
    return best ? best->handler : nullptr;
}

BoundScalarHandler BindScalarHandler(const DecodedInstruction& op) {
    const auto shape = BoundOperandShape::Read(op);
    const auto* binding = FindScalarBinding(op.handler);
    if (!binding) return {nullptr, 0, shape};
    const auto& table = *binding->scalar;
    if (!table.exact.empty()) {
        const auto decoded = DecodedOperandShape::Read(&op);
        const auto selected = std::ranges::lower_bound(table.exact, decoded, {}, &ExactScalarBinding::shape);
        if (selected != table.exact.end() && selected->shape == decoded)
            return {selected->handler, binding->id, shape};
    }
    const uint8_t modrm = shape.modrm < 0xc0 || !table.memory_mask ? shape.modrm & table.memory_mask : shape.modrm;
    const uint16_t key = (uint16_t(shape.slots) << 8) | modrm;
    const auto entries = std::span(table.entries, table.count);
    const auto find = [&](uint16_t lookup) -> HandlerFunc {
        const auto selected = std::ranges::lower_bound(entries, lookup, {}, &ScalarBindingEntry::key);
        return selected != entries.end() && selected->key == lookup ? selected->handler : nullptr;
    };
    assert(shape.slots == 1 || shape.slots == 2);
    HandlerFunc executable = nullptr;
    if (table.policies) {
        uint16_t policy_key = key;
        if (table.policies & kBindWidth) policy_key |= (op.prefixes.flags.opsize ? 1u : 2u) << 10;
        if (table.policies & kBindFlags) policy_key |= (op.meta.flags.no_flags ? 2u : 1u) << 12;
        executable = find(policy_key);
        if (!executable && (table.policies & kRequireWidth))
            executable = find((policy_key & 0xff00u) | kUnboundModRMKey);
    }
    if (!executable) executable = find(key);
    if (!executable) executable = table.fallback[shape.slots - 1];
    assert(executable);
    return {executable, binding->id, shape};
}

HandlerFunc CanonicalBoundHandler(HandlerFunc handler) {
    if (const auto* binding = FindScalarBinding(handler)) return binding->handler;
    return handler;
}

uint32_t FindScalarTrainingId(HandlerFunc handler) {
    if (const auto* binding = FindScalarBinding(handler)) return binding->id;
    return 0;
}

uint32_t TrainingOperationIdAt(uint32_t index) {
    return index < std::size(training_operations) ? training_operations[index].id : 0;
}

const char* FindTrainingOperationName(uint32_t id) {
    const auto it = std::ranges::lower_bound(training_operations, id, {}, &TrainingOperation::id);
    return it != std::end(training_operations) && it->id == id ? it->name : nullptr;
}

}  // namespace fiberish
