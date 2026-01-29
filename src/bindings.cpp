#include "decoder.h"
#include "mmu.h"
#include "hooks.h"
#include "ops.h"
#include "state.h"
#include <cstring>
#include <cstdio>

// C-Exported Interface for Python ctypes
extern "C" {

using namespace x86emu;

// EmuState Definition is in state.h


void X86_DebugStructSizes() {
    // Debug helper
}

// Internal bridge callback
void BridgeFaultCallback(void* opaque, uint32_t addr, int is_write) {
    // Here opaque is Context*. Or we can pass EmuState*.
    // If we want to notify Python, we need a function pointer stored in EmuState.
    // For now, let's just log or set a simple flag in Context if we had one.
    printf("[Bindings] Bridge Callback! Fault at 0x%08X Write=%d\n", addr, is_write);
}

// Create Simulator Instance
EmuState* X86_Create() {
    EmuState* state = new EmuState();
    // Link pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;
    // Defaults
    state->ctx.eip = 0;
    std::memset(&state->ctx.regs, 0, sizeof(state->ctx.regs));
    
    // Setup generic fault printer for now, or allow python to set it
    return state;
}

// Set Fault Callback
// py_func: void(*)(uint32_t addr, int is_write)
using PyFaultHandler = void(*)(uint32_t addr, int is_write);
PyFaultHandler global_py_callback = nullptr;

void BridgeToPython(void* opaque, uint32_t addr, int is_write) {
    if (global_py_callback) {
        global_py_callback(addr, is_write);
    }
}

void X86_SetFaultCallback(EmuState* state, PyFaultHandler handler) {
    printf("[Bindings] Setting Fault Callback %p\n", (void*)handler);
    global_py_callback = handler;
    state->mmu.set_fault_callback(BridgeToPython, state);
}

// Decode Binding
void X86_Decode(const uint8_t* bytes, DecodedOp* op_out) {
    if (!bytes || !op_out) return;
    DecodeInstruction(bytes, op_out);
}

// Destroy Simulator Instance
void X86_Destroy(EmuState* state) {
    if (state) delete state;
}

// Access Context
Context* X86_GetContext(EmuState* state) {
    return &state->ctx;
}

// Map Memory
void X86_MemMap(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) {
    state->mmu.mmap(addr, size, perms);
}

// Write Memory (Bytes)
void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        state->mmu.write<uint8_t>(addr + i, data[i]);
    }
}

// Read Memory (Bytes)
void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        val[i] = state->mmu.read<uint8_t>(addr + i);
    }
}

// Run Code (Block Based)
extern "C"
void X86_Run(EmuState* state) {
    state->status = EmuStatus::Running;
    
    while (state->status == EmuStatus::Running) {
        uint32_t eip = state->ctx.eip;
        
        // 1. Lookup Block
        auto it = state->block_cache.find(eip);
        if (it == state->block_cache.end()) {
            // Decode new block
            BasicBlock block;
            if (!DecodeBlock(state, eip, &block)) {
                // Decode failed (invalid instruction or unmapped)
                printf("[Run] Decode Failed at %08X\n", eip);
                state->status = EmuStatus::Fault;
                break;
            }
            // Insert
            auto res = state->block_cache.insert({eip, std::move(block)});
            it = res.first;
        }
        
        // 2. Execute Block (Threaded Dispatch)
        // Invokes the first handler, which tail-calls the rest.
        BasicBlock& block = it->second;
        if (!block.ops.empty()) {
            DecodedOp* head = &block.ops[0];
            if (head->handler) {
                // Head handler will update EIP and dispatch next
                head->handler(state, head); 
            } else {
                 printf("[Run] Null Handler at %08X\n", eip);
                 state->status = EmuStatus::Fault;
            }
        }
        
        // Loop will continue if status is Running (JMP/RET/End of Block)
        // If End of Block (is_last), Wrapper returns here.
        // We look up new EIP in next iteration.
    }
}

// Step (Placeholder for now)
void X86_Step(EmuState* state) {
    // Basic Fetch-Decode-Execute Loop Mock
    // 1. Fetch (Read at EIP)
    uint8_t buf[16];
    for (int i=0; i<16; ++i) {
        buf[i] = state->mmu.read<uint8_t>(state->ctx.eip + i);
    }
    printf("[Sim] Fetch done.\n");
    
    // 2. Decode
    DecodedOp op;
    DecodeInstruction(buf, &op);
    
    // 3. Increment EIP (Before Execute to allow jumps to overwrite)
    state->ctx.eip += op.length;

    // 4. Execute
    if (op.handler_index < 1024) {
        HandlerFunc h = g_Handlers[op.handler_index];
        if (h) {
             h(state, &op);
        } else {
             // Nullptr -> Invalid/Unimplemented
             OpNotImplemented(state, &op);
        }
    } else {
        OpNotImplemented(state, &op);
    }


}

} // extern "C"
