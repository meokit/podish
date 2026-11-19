#include "instruction_variants.h"
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
    struct Fallback {
        uint16_t opcode;
        HandlerFunc handler;
        const char* logic;
    };
#include "instruction_catalog.inc"
    for (const auto& f : fallbacks) RegisterInstructionFallback(f.opcode, f.handler, f.logic);
    for (const auto& v : variants)
        RegisterInstructionVariant(v.opcode, v.criteria, v.handler, v.logic, v.family, v.parent, v.bake, v.rank);
}
}  // namespace fiberish
