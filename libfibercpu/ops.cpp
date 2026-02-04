#include "ops.h"
#include "dispatch.h"

namespace x86emu {

// Sentinel Handler
ATTR_PRESERVE_NONE
int64_t OpExitBlock(EmuState* state, DecodedOp* op, int64_t instr_limit) {
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
                HandlerFunc h = g_Handlers[next_head->handler_index];
                if (h) {
                    ATTR_MUSTTAIL return h(state, next_head, instr_limit);
                }
            }
        }
    }
    // Returns to X86_Run loop.
    return instr_limit;
}

// Global dispatch table
// This is initialized by HandlerInit static constructor below
HandlerFunc g_Handlers[1024] = {nullptr};

// Static initialization of dispatch table
struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i=0; i<1024; ++i) g_Handlers[i] = nullptr;
        
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
        
        // 3. Set Sentinel (1023)
        // Must match DecodeBlock sentinel index
        g_Handlers[1023] = OpExitBlock;
    }
};

// Static instance to trigger initialization
static HandlerInit _init;

} // namespace x86emu
