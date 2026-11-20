#include "superopcodes.h"
#include "instruction_variants.h"
#include "operand_specialization.h"
#if FIBERCPU_DYNAMIC_OPERANDS
#include "dynamic_operand_specialization.h"
#endif

#include <ankerl/unordered_dense.h>
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

void ApplySuperOpcodesToBlockOps(DecodedInstruction* ops, uint32_t op_count, bool previously_lowered) {
    if (!ops) return;
#if FIBERCPU_LOWER_OPERANDS || FIBERCPU_DYNAMIC_OPERANDS
    if (previously_lowered) {
        for (uint32_t i = 0; i < op_count; ++i) {
            auto& op = ops[i];
            op.handler = CanonicalBoundHandler(op.handler);
            if (op.meta.flags.operand_lowered) {
#if FIBERCPU_DYNAMIC_OPERANDS
                op.handler = CanonicalDynamicOperandHandler(op.handler);
#endif
#if FIBERCPU_LOWER_OPERANDS
                // Concatenation can change the sequence and its last op.
                // Restore the scalar head so the new block can match it again.
                op.handler = UnfuseOperandHandler(op.handler);
#endif
                op.meta.flags.operand_lowered = 0;
            }
        }
    }
#endif

#if FIBERCPU_ENABLE_SUPEROPCODES
    const auto view = BuildFusionLookupView(ops, op_count);
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
        if (op.len) {
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
        if (auto handler = FindSuperOpcode(&fusion_ops[i])) ops[i].handler = handler;
    }
#endif
}

}  // namespace fiberish
