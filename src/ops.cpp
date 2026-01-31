#include "ops.h"
#include "dispatch.h"

namespace x86emu {

// Sentinel Handler
ATTR_PRESERVE_NONE
void OpExitBlock(EmuState* state, DecodedOp* op) {
    // End of Threaded Dispatch Chain.
    // Returns to X86_Run loop.
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
