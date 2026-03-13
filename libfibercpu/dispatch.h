#pragma once
#include <limits>
#include "decoder.h"
#include "logger.h"
#include "specialization.h"
#include "state.h"

namespace fiberish {

// External reference to handlers
extern void* g_HandlerBase;
extern HandlerFunc g_Handlers[1024];

extern ATTR_PRESERVE_NONE int64_t MemoryOpRestart(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                  mem::MicroTLB utlb, uint32_t branch);
extern ATTR_PRESERVE_NONE int64_t MemoryOpRetry(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                mem::MicroTLB utlb, uint32_t branch);

static inline ATTR_PRESERVE_NONE int64_t TryChainToBranch(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                          int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch) {
    const uint32_t target_eip = branch != std::numeric_limits<uint32_t>::max() ? branch : op->next_eip;
    state->ctx.eip = target_eip;
    state->mem_op.emplace<0>();

    if (instr_limit <= 0) return instr_limit;

    BasicBlock* next_block = nullptr;
    if (GetExtKind(op) == ExtKind::Link) {
        next_block = GetNextBlock(op);
    } else if (GetExtKind(op) == ExtKind::ControlFlow) {
        next_block = GetCachedTarget(op);
    }

    if (!next_block || !next_block->is_valid || next_block->start_eip != target_eip) {
        auto it = state->block_cache.find(target_eip);
        next_block = it == state->block_cache.end() ? nullptr : it->second;
        if (next_block && next_block->is_valid && next_block->start_eip == target_eip &&
            GetExtKind(op) == ExtKind::ControlFlow) {
            SetCachedTarget(op, next_block);
        }
    }
    if (!next_block || !next_block->is_valid || next_block->start_eip != target_eip) return instr_limit;

    instr_limit -= next_block->inst_count;
    state->last_block = next_block;
    next_block->exec_count++;
    DecodedOp* next_head = next_block->FirstOp();
    if (next_block->entry != nullptr) {
        ATTR_MUSTTAIL return next_block->entry(state, next_head, instr_limit, utlb,
                                               std::numeric_limits<uint32_t>::max());
    }
    return instr_limit;
}

template <LogicFunc Target>
ATTR_PRESERVE_NONE int64_t DispatchWrapper(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                           mem::MicroTLB utlb, uint32_t branch) {
    // Prefetch further ops
    PREFETCH(reinterpret_cast<const std::byte*>(op) + 128);
    PREFETCH(reinterpret_cast<const std::byte*>(op) + 256);

    // Execute Logic
    auto flow = Target(state, op, &utlb, GetImm(op), &branch);

    switch (flow) {
        case LogicFlow::Continue:
            // Direct Relative Dispatch
            // Note: We don't check for 0 here for speed, assuming well-formed blocks
            // (sentinel always valid)
            if (auto* next_op = NextOp(op)) {
                ATTR_MUSTTAIL return next_op->handler(state, next_op, instr_limit, utlb, branch);
            }
            __builtin_unreachable();
        case LogicFlow::ExitOnCurrentEIP:
            if (!state->eip_dirty) state->sync_eip_to_op_start(op);
            return instr_limit;
        case LogicFlow::ExitOnNextEIP:
            if (!state->eip_dirty) state->sync_eip_to_op_end(op);
            return instr_limit;
        case LogicFlow::ExitWithoutSyncEIP:
            return instr_limit;
        case LogicFlow::RestartMemoryOp:
            ATTR_MUSTTAIL return MemoryOpRestart(state, op, instr_limit, utlb, branch);
        case LogicFlow::RetryMemoryOp:
            ATTR_MUSTTAIL return MemoryOpRetry(state, op, instr_limit, utlb, branch);
        case LogicFlow::ExitToBranch:
            ATTR_MUSTTAIL return TryChainToBranch(state, op, instr_limit, utlb, branch);
        default:
            return instr_limit;
    }
}

// Registration Helper
// We use a struct to infer the function pointer type for registration
template <LogicFunc Target>
struct DispatchRegistrar {
    static void Register(int idx) { g_Handlers[idx] = DispatchWrapper<Target>; }

    static void RegisterNF(int idx) {
        SpecCriteria criteria;
        criteria.no_flags = true;
        // Don't set masks, so it matches any ModRM/Prefix unless overridden?
        // Wait, standard specialized handler matches narrowly.
        // But here we want to match broadly (any modrm) BUT with no_flags=true.
        // SpecCriteria defaults mask to 0 (wildcard). So this registers a generic NoFlags handler for this opcode.
        RegisterSpecializedHandler(idx, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }

    // Specialization Registration
    static void RegisterSpecialized(int opcode, SpecCriteria criteria) {
        // We register the Wrapper as the handler, because that is what the dispatch loop calls.
        // The wrapper internally calls Target().
        // LogicFunc (the raw logic) is NOT what is stored in the offset, the wrapper is.
        // Wait, SpecializedEntry stores LogicFunc? No, it should store HandlerFunc.
        // Let's cast DispatchWrapper<Target> to HandlerFunc (which it is compatible with).
        RegisterSpecializedHandler(opcode, criteria, (HandlerFunc)DispatchWrapper<Target>);
    }
};

// Simplified Macro for ops registration
// Replaces the old g_Handlers[idx] = DispatchWrapper<Func>;
// Usage: DispatchRegistrar<Func>::Register(idx);

}  // namespace fiberish
