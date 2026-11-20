#include "superopcodes.h"
#include "instruction_variants.h"
#include "operand_specialization.h"
#if FIBERCPU_DYNAMIC_OPERANDS
#include "dynamic_operand_specialization.h"
#endif

#include <ankerl/unordered_dense.h>
#include <array>
#include <vector>

#include "ops/ops_control_impl.h"
#include "ops/ops_data_mov_impl.h"
#include "ops/ops_groups_impl.h"
#include "ops/ops_sse_fp_impl.h"
#include "ops/ops_sse_mov_impl.h"

namespace fiberish {

#if FIBERCPU_STATIC_SPECIALIZATION
namespace handwritten_superopcodes {

#define DEFINE_HANDWRITTEN_SUPEROPCODE_2(Name, Op0, Op1)                                                               \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        RUN_SUPEROPCODE_OP(Op0, state, op, instr_limit, utlb, branch, flags_cache);                                    \
                                                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        RUN_SUPEROPCODE_OP(Op1, state, second_op, instr_limit, utlb, branch, flags_cache);                             \
                                                                                                                       \
        if (auto* next_op = NextOp(second_op)) {                                                                       \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
    }

#define RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(target, current_op, exit_flow, exit_op)                     \
    do {                                                                                                             \
        auto flow = target(state, current_op, &utlb, GetImm(current_op), GetModRM(current_op), &branch, flags_cache, \
                           FIBERCPU_GPR_CACHE);                                                                      \
        if (flow != LogicFlow::Continue) [[unlikely]] {                                                              \
            exit_flow = flow;                                                                                        \
            exit_op = current_op;                                                                                    \
            goto superopcode_exit;                                                                                   \
        }                                                                                                            \
    } while (false)

#define DEFINE_HANDWRITTEN_SUPEROPCODE_3(Name, Op0, Op1, Op2)                                                          \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                                     \
        DecodedOp* exit_op = op;                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        DecodedOp* third_op = NextOp(second_op);                                                                       \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                                 \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);                          \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);                           \
                                                                                                                       \
        if (auto* next_op = NextOp(third_op)) {                                                                        \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
    superopcode_exit:                                                                                                  \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, utlb_tags, utlb_addend, branch,          \
                                flags_cache);                                                                          \
    }

#define DEFINE_HANDWRITTEN_SUPEROPCODE_4(Name, Op0, Op1, Op2, Op3)                                                     \
    ATTR_PRESERVE_NONE int64_t Name(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,             \
                                    mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend, uint32_t branch, \
                                    uint64_t flags_cache FIBERCPU_PIN_PARAMS) {                                        \
        FIBERCPU_PIN_ASSUME;                                                                                           \
        mem::MicroTLB utlb = mem::DecodeMicroTlbAbi(utlb_tags, utlb_addend);                                           \
        LogicFlow exit_flow = LogicFlow::Continue;                                                                     \
        DecodedOp* exit_op = op;                                                                                       \
        DecodedOp* second_op = NextOp(op);                                                                             \
        DecodedOp* third_op = NextOp(second_op);                                                                       \
        DecodedOp* fourth_op = NextOp(third_op);                                                                       \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op0, op, exit_flow, exit_op);                                 \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op1, second_op, exit_flow, exit_op);                          \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op2, third_op, exit_flow, exit_op);                           \
        RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT(Op3, fourth_op, exit_flow, exit_op);                          \
                                                                                                                       \
        if (auto* next_op = NextOp(fourth_op)) {                                                                       \
            mem::EncodeMicroTlbAbi(utlb, utlb_tags, utlb_addend);                                                      \
            ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb_tags, utlb_addend, branch,         \
                                                  flags_cache FIBERCPU_PIN_RELOAD);                                    \
        }                                                                                                              \
        __builtin_unreachable();                                                                                       \
    superopcode_exit:                                                                                                  \
        HANDLE_SUPEROPCODE_FLOW(exit_flow, state, exit_op, instr_limit, utlb, utlb_tags, utlb_addend, branch,          \
                                flags_cache);                                                                          \
    }

#include "handwritten_fusions.inc"

#undef DEFINE_HANDWRITTEN_SUPEROPCODE_2
#undef DEFINE_HANDWRITTEN_SUPEROPCODE_3
#undef DEFINE_HANDWRITTEN_SUPEROPCODE_4
#undef RUN_HANDWRITTEN_SUPEROPCODE_OP_WITH_UNIFIED_EXIT

#if !FIBERCPU_GENERATED_WITH_HANDWRITTEN || defined(FIBERCPU_VALIDATE_SPECIALIZATION_INDEX)
HandlerFunc FindHandWrittenSuperOpcode(const DecodedInstruction* ops) {
    static const auto filter = [] {
        std::array<uint64_t, 16> bits{};
        for (const auto& fusion : kHandwrittenFusions) {
            const auto key = reinterpret_cast<uintptr_t>(fusion.operations[0]) >> 2;
            bits[(key >> 6) & 15] |= uint64_t{1} << (key & 63);
        }
        return bits;
    }();
    const auto key = reinterpret_cast<uintptr_t>(ops->handler) >> 2;
    if (!(filter[(key >> 6) & 15] & (uint64_t{1} << (key & 63)))) return nullptr;
    for (const auto& fusion : kHandwrittenFusions) {
        unsigned i = 0;
        while (i < fusion.width && ops[i].handler == fusion.operations[i]) ++i;
        if (i == fusion.width) return fusion.handler;
    }
    return nullptr;
}
#endif

#ifdef FIBERCPU_VALIDATE_SPECIALIZATION_INDEX
bool ValidateHandwrittenSuperOpcodes() {
    for (const auto& fusion : kHandwrittenFusions) {
        DecodedInstruction ops[4]{};
        for (unsigned i = 0; i < fusion.width; ++i) ops[i].handler = fusion.operations[i];
        if (FindHandWrittenSuperOpcode(ops) != fusion.handler) return false;
#if FIBERCPU_GENERATED_OWNS_FUSIONS
        if (FindSuperOpcode(ops) == fusion.handler) return false;
#else
        if (FindSuperOpcode(ops) != fusion.handler) return false;
#endif
        ops[fusion.width - 1].handler = nullptr;
        if (FindHandWrittenSuperOpcode(ops) == fusion.handler) return false;
    }
    return true;
}
#endif

}  // namespace handwritten_superopcodes

void RegisterHandwrittenSuperOpcodes(void* context, RegisterHandwrittenCallback callback) {
    // The generated matcher prepends entries to preserve this selection order.
    const auto& fusions = handwritten_superopcodes::kHandwrittenFusions;
    for (size_t i = std::size(fusions); i != 0; --i) {
        const auto& fusion = fusions[i - 1];
        const auto& ops = fusion.operations;
        callback(context, ops[0], ops[1], ops[2], ops[3], fusion.handler, fusion.width);
    }
}
#elif defined(FIBERCPU_VALIDATE_SPECIALIZATION_INDEX)
namespace handwritten_superopcodes {
bool ValidateHandwrittenSuperOpcodes() { return true; }
}  // namespace handwritten_superopcodes
#endif

#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
#define FIBERCPU_TRAINING_FUSION(name, id)                                                                      \
    ATTR_PRESERVE_NONE int64_t name(EmuState*, DecodedOp*, int64_t, mem::MicroTlbAbiWord, mem::MicroTlbAbiWord, \
                                    uint32_t, uint64_t FIBERCPU_PIN_PARAMS);
#include "training_fusion_handlers.inc"
#undef FIBERCPU_TRAINING_FUSION
#endif

uint32_t FindFusionTrainingId(HandlerFunc handler) {
#if FIBERCPU_ENABLE_SUPEROPCODES
    static const auto ids = [] {
        ankerl::unordered_dense::map<HandlerFunc, uint32_t> result;
#if FIBERCPU_STATIC_SPECIALIZATION
        for (const auto& fusion : handwritten_superopcodes::kHandwrittenFusions)
            result.emplace(fusion.handler, fusion.training_id);
#endif
#if FIBERCPU_HAVE_GENERATED_SUPEROPCODES
#define FIBERCPU_TRAINING_FUSION(name, id) result.emplace(name, id);
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
#if FIBERCPU_STATIC_SPECIALIZATION && !FIBERCPU_GENERATED_WITH_HANDWRITTEN
    if (HandlerFunc handwritten = handwritten_superopcodes::FindHandWrittenSuperOpcode(ops)) {
        return handwritten;
    }
#endif

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
        const auto fallback = FindFusionFallbackHandler(CanonicalBoundHandler(ops[i].handler));
        if (fallback == ops[i].handler) continue;
        if (view.empty()) view.assign(ops, ops + count + 1);  // Include the block sentinel.
        view[i].handler = fallback;
    }
    return view;
}
#endif

void ApplySuperOpcodesToBlockOps(DecodedInstruction* ops, uint32_t op_count, bool previously_lowered) {
    if (!ops) return;
#if (FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY) || FIBERCPU_DYNAMIC_OPERANDS
    if (previously_lowered) {
        for (uint32_t i = 0; i < op_count; ++i) {
            auto& op = ops[i];
            op.handler = CanonicalBoundHandler(op.handler);
            if (op.meta.flags.operand_lowered) {
#if FIBERCPU_DYNAMIC_OPERANDS
                op.handler = CanonicalDynamicOperandHandler(op.handler);
#endif
#if FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY
                op.handler = CanonicalOperandHandler(op.handler);
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

#if (FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY) || FIBERCPU_DYNAMIC_OPERANDS
    for (uint32_t i = 0; i < op_count; ++i) {
        auto& op = ops[i];
#if FIBERCPU_ENABLE_SUPEROPCODES
        if (i + 1 < op_count) {
            if (auto handler = FindSuperOpcode(&fusion_ops[i])) op.handler = handler;
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
#if FIBERCPU_LOWER_OPERANDS && !FIBERCPU_OPERAND_FUSIONS_ONLY
            if (auto handler = LowerOperandHandler(&op)) {
                op.handler = handler;
                op.meta.flags.operand_lowered = 1;
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
