#include "ops.h"
#include "dispatch.h"

namespace x86emu {

// Sentinel Handler
template<int I>
ATTR_PRESERVE_NONE
int64_t OpExitBlock(EmuState* state, DecodedOp* op, int64_t instr_limit, mem::MicroTLB utlb) {
    // End of Threaded Dispatch Chain.
    if (op->next_block) {
        // Basic Block Chaining
        if (op->next_block->start_eip == state->ctx.eip) {
            // Check instruction limit before chaining
            if (instr_limit > 0) {
                // Subtract the NEXT block's size from the limit
                instr_limit -= op->next_block->inst_count;

                state->last_block = op->next_block;
                DecodedOp* next_head = &op->next_block->ops[0];

                // Direct Relative Dispatch
                int32_t offset = next_head->handler_offset;
                if (offset != 0) {
                    HandlerFunc h = (HandlerFunc)((intptr_t)g_HandlerBase + offset);
                    ATTR_MUSTTAIL return h(state, next_head, instr_limit, utlb);
                }
            }
        }
    }
    // Returns to X86_Run loop.
    return instr_limit;
}

// Instantiate 16 variants to reduce BTB pressure
#define INSTANTIATE_EXIT(i) OpExitBlock<i>
HandlerFunc g_ExitHandlers[16] = {
    INSTANTIATE_EXIT(0),  INSTANTIATE_EXIT(1),  INSTANTIATE_EXIT(2),  INSTANTIATE_EXIT(3),
    INSTANTIATE_EXIT(4),  INSTANTIATE_EXIT(5),  INSTANTIATE_EXIT(6),  INSTANTIATE_EXIT(7),
    INSTANTIATE_EXIT(8),  INSTANTIATE_EXIT(9),  INSTANTIATE_EXIT(10), INSTANTIATE_EXIT(11),
    INSTANTIATE_EXIT(12), INSTANTIATE_EXIT(13), INSTANTIATE_EXIT(14), INSTANTIATE_EXIT(15)
};

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

}  // namespace x86emu
