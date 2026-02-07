#include "ops.h"
#include "dispatch.h"

namespace fiberish {

// Sentinel Handler
template <int I>
ATTR_PRESERVE_NONE int64_t OpExitBlock(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                       mem::MicroTLB utlb) {
    // Basic Block Chaining
    // Optim: If next_block is dummy, is_valid is false, so we skip.
    // If next_block is real but invalidated, is_valid is false, so we skip.
    if (op->next_block->is_valid && op->next_block->start_eip == state->ctx.eip) {
        // Check instruction limit before chaining
        if (instr_limit > 0) {
            // Subtract the NEXT block's size from the limit
            instr_limit -= op->next_block->inst_count;

            state->last_block = op->next_block;
            // ops is now flexible array member, essentially ops[0]
            DecodedOp* next_head = &op->next_block->ops[0];

            // Direct Relative Dispatch
            auto handler = next_head->handler;
            if (handler != nullptr) {
                ATTR_MUSTTAIL return handler(state, next_head, instr_limit, utlb);
            }
        }
    }
    // Returns to X86_Run loop.
    return instr_limit;
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

// Global dispatch table
// This is initialized by HandlerInit static constructor below
// Anchor variable to calculate offsets against
static uint8_t g_HandlerBaseMarker = 0;
void* g_HandlerBase = (void*)&g_HandlerBaseMarker;

HandlerFunc g_Handlers[1024] = {nullptr};
HandlerFunc g_Handlers_NF[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i = 0; i < 1024; ++i) {
            g_Handlers[i] = nullptr;
            g_Handlers_NF[i] = nullptr;
        }

        // 2. Register all modular operations
        RegisterAluOps();
        RegisterCompareOps();
        RegisterControlOps();
        RegisterDataMovOps();
        RegisterDoubleShiftOps();
        RegisterFpuOps();
        RegisterGroupOps();
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
static std::vector<SpecializedEntry> g_SpecializedRegistry;

void RegisterSpecializedHandler(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler) {
    g_SpecializedRegistry.push_back({opcode, criteria, handler});
}

HandlerFunc FindSpecializedHandler(uint16_t handler_index, DecodedOp* op) {
    for (const auto& entry : g_SpecializedRegistry) {
        if (entry.opcode == handler_index) {
            // Check ModRM constraints
            // If op doesn't have modrm but criteria requires it -> fail?
            // The criteria.Matches takes modrm uint8.
            // DecodedOp has modrm field always, valid if flags.has_modrm is true.
            // If specialized entry requires modrm (mask != 0) and op doesn't have it, we should probably fail?
            // SpecCriteria::Matches logic: checks masks. If mask is 0, it matches anything (including garbage if not
            // present). Usually we specialize precisely.

            if (op->meta.flags.has_modrm) {
                if (entry.criteria.Matches(op->modrm, op->prefixes.all)) return entry.handler;
            } else {
                // If op has no ModRM...
                // But wait, if criteria has a prefix constraint but NO modrm constraint, we should check it.
                // My logic above was simplistic.
                // Let's create a dummy modrm=0 if not present, but ensure mask checks fail if they were set?
                // Actually, if has_modrm is false, modrm field is undefined/garbage (or 0).

                // Better logic:
                // 1. Prefix Check always
                if (entry.criteria.prefix_mask) {
                    if ((op->prefixes.all & entry.criteria.prefix_mask) != entry.criteria.prefix_val) continue;
                }

                // 2. ModRM Check
                if (op->meta.flags.has_modrm) {
                    // Standard check
                    if (entry.criteria.mod_mask || entry.criteria.reg_mask || entry.criteria.rm_mask) {
                        if (!entry.criteria.Matches(op->modrm, op->prefixes.all)) continue;
                    }
                } else {
                    // No ModRM in instruction.
                    // If criteria REQUIRES ModRM specific values (mask != 0), then it's a mismatch.
                    if (entry.criteria.mod_mask || entry.criteria.reg_mask || entry.criteria.rm_mask) continue;
                }

                return entry.handler;
            }
        }
    }
    return nullptr;
}

}  // namespace fiberish
