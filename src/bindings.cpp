#include "decoder.h"
#include "mmu.h"
#include "hooks.h"
#include "ops.h"
#include "state.h"
#include <cstring>
#include <cstdio>
#include "dispatch.h"

// C-Exported Interface for Python ctypes
extern "C" {

using namespace x86emu;

// EmuState Definition is in state.h


void X86_DebugStructSizes() {
    // Debug helper
}

#include <csignal>
#include <execinfo.h>
#include <unistd.h>
#include <cstdlib>

void SignalHandler(int sig) {
    void* array[20];
    size_t size;

    // get void*'s for all entries on the stack
    size = backtrace(array, 20);

    // print out all the frames to stderr
    fprintf(stderr, "\n[CRASH] Signal %d Caught:\n", sig);
    backtrace_symbols_fd(array, size, STDERR_FILENO);
    
    // Exit
    _exit(1);
}

static bool g_SignalRegistered = false;

// Internal bridge callback
void BridgeFaultCallback(void* opaque, uint32_t addr, int is_write) {
    // Here opaque is Context*. Or we can pass EmuState*.
    // If we want to notify Python, we need a function pointer stored in EmuState.
    // For now, let's just log or set a simple flag in Context if we had one.
    printf("[Bindings] Bridge Callback! Fault at 0x%08X Write=%d\n", addr, is_write);
}

// Create Simulator Instance
EmuState* X86_Create() {
    if (!g_SignalRegistered) {
        signal(SIGSEGV, SignalHandler);
        signal(SIGILL, SignalHandler);
        signal(SIGBUS, SignalHandler);
        g_SignalRegistered = true;
    }
    EmuState* state = new EmuState();
    // Zero entire context first
    std::memset(&state->ctx, 0, sizeof(state->ctx));
    
    // Set default EFLAGS and Mask
    state->ctx.eflags = 0x202; // IF=1, Reserved=1
    state->ctx.eflags_mask = 0x240DD5; // User-mode mask: CF,PF,AF,ZF,SF,TF,DF,OF,AC,ID. (Protect IF, IOPL, NT, RF, VM)
    
    // Link pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;
    
    // Link MMU to State Status
    state->mmu.set_status_ptr(&state->status);
    
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

using PyMemHook = void(*)(uint32_t addr, uint32_t size, int is_write, uint64_t val);
PyMemHook global_mem_hook = nullptr;

void BridgeMemHook(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val) {
    if (global_mem_hook) {
        global_mem_hook(addr, size, is_write, val);
    }
}

void X86_SetMemHook(EmuState* state, PyMemHook hook) {
    global_mem_hook = hook;
    state->mmu.set_mem_hook(BridgeMemHook, state);
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
            HandlerFunc h = nullptr;
            if (head->handler_index < 1024) h = g_Handlers[head->handler_index];
            
            if (h) {
                // Head handler will update EIP and dispatch next
                h(state, head); 
            } else {
                // Opcode mapping failed or out of bounds
                OpUd2(state, head); // Trigger #UD
            }
        }
        
        // Loop will continue if status is Running (JMP/RET/End of Block)
        // If End of Block (is_last), Wrapper returns here.
        // We look up new EIP in next iteration.
    }
}

// Stop Execution
void X86_EmuStop(EmuState* state) {
    if (state) state->status = EmuStatus::Stopped;
}

// Step (Placeholder for now)
int X86_Step(EmuState* state) {
    state->status = EmuStatus::Running;
    // Basic Fetch-Decode-Execute Loop Mock
    // 1. Fetch (Read at EIP)
    uint8_t buf[16];
    for (int i=0; i<16; ++i) {
        buf[i] = state->mmu.read<uint8_t>(state->ctx.eip + i);
    }
    // printf("[Sim] Fetch done.\n");
    
    // 2. Decode
    DecodedOp op;
    if (!DecodeInstruction(buf, &op)) {
        // Decode Failed
        // printf("[Sim] Decode Failed at %08X\n", state->ctx.eip);
        std::memset(&op, 0, sizeof(op));
        op.length = 1;
        op.length = 1;
        op.handler_index = 0x10B; // UD2
    }
    op.meta.flags.is_last = 1; // Critical for DispatchWrapper
    
    // 3. Increment EIP (Handled by DispatchWrapper)
    // state->ctx.eip += op.length;

    // 4. Execute
    HandlerFunc h = nullptr;
    if (op.handler_index < 1024) {
        h = g_Handlers[op.handler_index];
    }

    // printf("[Sim] Step EIP=%08X Idx=%04X Handler=%p\n", state->ctx.eip, op.handler_index, (void*)h);

    if (h) {
         uint32_t old_eip = state->ctx.eip;
         h(state, &op);
         // If handler didn't jump (overwriting EIP), advance by instruction length
         if (state->ctx.eip == old_eip) {
             state->ctx.eip += op.length;
         }
    } else {
         // Nullptr -> Invalid/Unimplemented -> Trigger #UD
         if (!state->hooks.on_invalid_opcode(state)) {
             state->status = EmuStatus::Fault;
             state->fault_vector = 6; // #UD
         }
    }
    
    return (int)state->status;
}


// Interrupt Hook
using PyInterruptHook = int(*)(uint32_t vector);

void X86_SetInterruptHook(EmuState* state, uint8_t vector, PyInterruptHook hook) {
    // Register lambda that calls the Python hook
    // Note: 'hook' is a function pointer (trampoline generated by cppyy)
    state->hooks.set_interrupt_hook(vector, [hook](EmuState* s, uint8_t v) {
        return hook((uint32_t)v) != 0;
    });
}
} // extern "C"
