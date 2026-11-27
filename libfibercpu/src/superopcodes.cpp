#include "superopcodes.h"
#include "instruction_variants.h"
#include "operand_specialization.h"
#if FIBERCPU_DYNAMIC_OPERANDS
#include "dynamic_operand_specialization.h"
#endif

#include <ankerl/unordered_dense.h>
#include <algorithm>
#include <vector>

namespace fiberish {

#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
#define FIBERCPU_TRAINING_FUSION(name, id, width) extern const std::array<HandlerFunc, 1u << (width)> name##_handlers;
#include "training_fusion_handlers.inc"
#undef FIBERCPU_TRAINING_FUSION
#endif

uint32_t FindFusionTrainingId(HandlerFunc handler) {
#if FIBERCPU_ENABLE_SUPEROPCODES
    static const auto ids = [] {
        ankerl::unordered_dense::map<HandlerFunc, uint32_t> result;
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
#define FIBERCPU_TRAINING_FUSION(name, id, width) \
    for (auto variant : name##_handlers)          \
        if (variant) result.emplace(variant, id);
#include "training_fusion_handlers.inc"
#undef FIBERCPU_TRAINING_FUSION
#endif
        return result;
    }();
    const auto it = ids.find(handler);
    return it == ids.end() ? 0 : it->second;
#else
    (void)handler;
    return 0;
#endif
}

HandlerFunc FindSuperOpcode(const DecodedInstruction* ops) {
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
    return GeneratedFindSuperOpcode(ops);
#else
    (void)ops;
    return nullptr;
#endif
}

#if FIBERCPU_ENABLE_SUPEROPCODES
static std::vector<DecodedInstruction> BuildFusionLookupView(const DecodedInstruction* ops, uint32_t count) {
    // Normalize the entire view so the matcher also sees overlapping base fusions.
    // Keep real slots intact for execution and memory-op replay.
    std::vector<DecodedInstruction> view;
    for (uint32_t i = 0; i < count; ++i) {
        const auto fallback = CanonicalBoundHandler(ops[i].handler);
        if (fallback == ops[i].handler) continue;
        if (view.empty()) view.assign(ops, ops + count + 1);  // Include the block sentinel.
        view[i].handler = fallback;
    }
    return view;
}
#endif

void ApplySuperOpcodesToBlockOps(DecodedInstruction* ops, uint32_t op_count,
                                 [[maybe_unused]] std::span<const ScalarBindingIndex> binding_indices) {
    if (!ops) return;
#if FIBERCPU_ENABLE_SUPEROPCODES
    assert(binding_indices.empty() || binding_indices.size() == op_count);
    // The generated indices identify canonical handlers selected during fresh decode.
    const bool known_canonical =
        !binding_indices.empty() &&
        std::ranges::find(binding_indices, ScalarBindingIndex::Unknown) == binding_indices.end();
    const auto view = known_canonical ? std::vector<DecodedInstruction>{} : BuildFusionLookupView(ops, op_count);
    const auto* fusion_ops = view.empty() ? ops : view.data();
#endif

#if FIBERCPU_LOWER_OPERANDS || FIBERCPU_DYNAMIC_OPERANDS
    for (uint32_t i = 0; i < op_count; ++i) {
        auto& op = ops[i];
#if FIBERCPU_ENABLE_SUPEROPCODES
        if (i + 1 < op_count) {
            if (auto handler = FindSuperOpcode(&fusion_ops[i])) {
                op.handler = handler;
                op.meta.flags.operand_lowered = 1;
            }
        }
#endif
        // Fusion matching only looks forward, so lowering this entry cannot
        // hide the canonical identities needed at the next offset.
        if (op.len && !IsLinkOnlyReturn(op.meta.all)) {
#if FIBERCPU_DYNAMIC_OPERANDS
            auto original = op.handler;
#if FIBERCPU_LOWER_OPERANDS
            original = CanonicalOperandHandler(original);
#endif
            if (auto handler = LowerDynamicOperandHandler(&op, original, op_count - i)) {
                op.handler = handler;
                op.meta.flags.operand_lowered = 1;
                continue;
            }
#endif
        }
    }
#elif FIBERCPU_ENABLE_SUPEROPCODES
    for (uint32_t i = 0; i + 1 < op_count; ++i) {
        if (auto handler = FindSuperOpcode(&fusion_ops[i])) {
            ops[i].handler = handler;
            ops[i].meta.flags.operand_lowered = 1;
        }
    }
#endif
}

}  // namespace fiberish
