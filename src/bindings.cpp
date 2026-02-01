#include "bindings.h"
#include "state.h"
#include "decoder.h"
#include "mem/mmu.h"
#include "hooks.h"
#include "ops.h"
#include "dispatch.h"
#include <cstring>
#include <cstdio>
#include <csignal>
#include <execinfo.h>
#include <unistd.h>
#include <cstdlib>

using namespace x86emu;

extern "C" {

// ----------------------------------------------------------------------------
// Internal Bridge Callbacks
// ----------------------------------------------------------------------------

static void InternalFaultBridge(void* opaque, uint32_t addr, int is_write) {
    EmuState* state = static_cast<EmuState*>(opaque);
    if (state->fault_handler) {
        state->fault_handler(state, addr, is_write, state->fault_userdata);
    } else {
        // Default behavior if no user handler: Trigger Fault
        state->status = EmuStatus::Fault;
        state->fault_vector = 14; // #PF
    }
}

static void InternalMemHookBridge(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val) {
    EmuState* state = static_cast<EmuState*>(opaque);
    if (state->mem_hook) {
        state->mem_hook(state, addr, size, is_write, val, state->mem_userdata);
    }
}

// Signal Handler for safety
void SignalHandler(int sig) {
    void* array[20];
    size_t size;
    size = backtrace(array, 20);
    fprintf(stderr, "\n[CRASH] Signal %d Caught:\n", sig);
    backtrace_symbols_fd(array, size, STDERR_FILENO);
    _exit(1);
}

static bool g_SignalRegistered = false;

// ----------------------------------------------------------------------------
// Creation / Destruction
// ----------------------------------------------------------------------------

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
    state->ctx.eflags_mask = 0x240DD5; 
    
    // Link pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;
    
    // Link MMU to State Status
    state->mmu.set_status_ptr(&state->status, &state->fault_vector);
    
    state->mmu.set_fault_callback(InternalFaultBridge, state);
    state->mmu.set_mem_hook(InternalMemHookBridge, state);
    
    return state;
}

EmuState* X86_Clone(EmuState* parent, int share_mem) {
    if (!parent) return nullptr;

    EmuState* state = new EmuState();
    
    // 1. Copy Context (Registers, Segments, etc.)
    state->ctx = parent->ctx;
    
    // 2. Memory Handling
    if (share_mem) {
        // Shared Memory (CLONE_VM) -> Threads
        // Copy the shared_ptr, incrementing refcount
        state->mmu = mem::Mmu(parent->mmu.page_dir);
    } else {
        // Independent Memory (Fork)
        // Deep copy the PageDirectory
        auto new_pd = std::make_shared<mem::PageDirectory>(*parent->mmu.page_dir);
        state->mmu = mem::Mmu(std::move(new_pd));
    }

    // 3. Link Internal Pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;
    state->mmu.set_status_ptr(&state->status, &state->fault_vector);
    
    // 4. Set Callbacks (Bridge to same handlers, but new 'state' passed)
    // Note: The UserData is pointing to whatever specific object managed the parent.
    // For Clone, we might need new userdata?
    // In our Go implementation, we use global wrappers or map state->Task.
    // So copying userdata is strictly correct for now (same logic applies).
    state->mmu.set_fault_callback(InternalFaultBridge, state);
    state->mmu.set_mem_hook(InternalMemHookBridge, state);

    // Reuse parent's external handlers & userdata
    state->fault_handler = parent->fault_handler;
    state->fault_userdata = parent->fault_userdata; // This might need care in Go!
    state->mem_hook = parent->mem_hook;
    state->mem_userdata = parent->mem_userdata;

    // Interrupt handlers
    for (int i=0; i<256; ++i) {
        state->interrupt_handlers[i] = parent->interrupt_handlers[i];
        state->interrupt_userdata[i] = parent->interrupt_userdata[i];
    }
    // Re-register hooks logic
    // We need to copy the internal C++ std::function hooks too?
    // 'state->hooks' is copy constructed from parent->hooks via `state->ctx = parent->ctx`? 
    // Wait, ctx copies pointers. state->hooks is a separate object.
    
    // Let's copy hooks explicitly
    state->hooks = parent->hooks; 
    
    // Explicitly copy segment bases (though ctx assignment should have done it, being safe)
    for (int i=0; i<6; ++i) {
        state->ctx.seg_base[i] = parent->ctx.seg_base[i];
    }

    return state;
}

void X86_Destroy(EmuState* state) {
    if (state) delete state;
}

// ----------------------------------------------------------------------------
// Register Access
// ----------------------------------------------------------------------------

uint32_t X86_RegRead(EmuState* state, int reg_index) {
    if (reg_index >= 0 && reg_index < 8) {
        return state->ctx.regs[reg_index];
    }
    return 0;
}

void X86_RegWrite(EmuState* state, int reg_index, uint32_t val) {
    if (reg_index >= 0 && reg_index < 8) {
        state->ctx.regs[reg_index] = val;
    }
}

uint32_t X86_GetEIP(EmuState* state) {
    return state->ctx.eip;
}

void X86_SetEIP(EmuState* state, uint32_t eip) {
    state->ctx.eip = eip;
}

uint32_t X86_GetEFLAGS(EmuState* state) {
    return state->ctx.eflags;
}

void X86_SetEFLAGS(EmuState* state, uint32_t val) {
    state->ctx.eflags = val;
}

// ----------------------------------------------------------------------------
// XMM Access
// ----------------------------------------------------------------------------

void X86_ReadXMM(EmuState* state, int idx, uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        std::memcpy(val, &state->ctx.xmm[idx], 16);
    }
}

void X86_WriteXMM(EmuState* state, int idx, const uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        std::memcpy(&state->ctx.xmm[idx], val, 16);
    }
}

// ----------------------------------------------------------------------------
// Segment Base Access
// ----------------------------------------------------------------------------

uint32_t X86_SegBaseRead(EmuState* state, int seg_index) {
    if (seg_index >= 0 && seg_index < 6) {
        return state->ctx.seg_base[seg_index];
    }
    return 0;
}

void X86_SegBaseWrite(EmuState* state, int seg_index, uint32_t base) {
    if (seg_index >= 0 && seg_index < 6) {
        state->ctx.seg_base[seg_index] = base;
    }
}

// ----------------------------------------------------------------------------
// Memory Access
// ----------------------------------------------------------------------------

void X86_MemMap(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) {
    state->mmu.mmap(addr, size, perms);
}

void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        state->mmu.write<uint8_t>(addr + i, data[i]);
    }
}

void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        val[i] = state->mmu.read<uint8_t>(addr + i);
    }
}

// ----------------------------------------------------------------------------
// Execution
// ----------------------------------------------------------------------------

void X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts) {
    state->status = EmuStatus::Running;
    uint64_t inst_count = 0;
    
    while (state->status == EmuStatus::Running) {
        uint32_t eip = state->ctx.eip;
        
        if (end_eip != 0 && eip >= end_eip) {
            state->status = EmuStatus::Stopped;
            break;
        }
        if (max_insts != 0 && inst_count >= max_insts) {
            state->status = EmuStatus::Stopped;
            break;
        }
        
        auto it = state->block_cache.find(eip);
        if (it == state->block_cache.end()) {
            BasicBlock block;
            uint64_t remaining_insts = 0;
            if (max_insts > 0) remaining_insts = max_insts - inst_count;
            
            if (!DecodeBlock(state, eip, end_eip, remaining_insts, &block)) {
                state->status = EmuStatus::Fault;
                break;
            }
            auto res = state->block_cache.insert({eip, std::move(block)});
            it = res.first;
        }
        
        BasicBlock& block = it->second;
        if (!block.ops.empty()) {
            DecodedOp* head = &block.ops[0];
            HandlerFunc h = nullptr;
            if (head->handler_index < 1024) h = g_Handlers[head->handler_index];
            
            if (h) {
                h(state, head); 
                if (block.ops.size() > 0)
                    inst_count += (block.ops.size() - 1);
            } else {
                OpUd2(state, head);
            }
        }
    }
}

void X86_EmuStop(EmuState* state) {
    if (state) state->status = EmuStatus::Stopped;
}

void X86_EmuFault(EmuState* state) {
    if (state) state->status = EmuStatus::Fault;
}

int X86_Step(EmuState* state) {
    state->status = EmuStatus::Running;

    uint8_t buf[16];
    for (int i=0; i<16; ++i) {
        buf[i] = state->mmu.read<uint8_t>(state->ctx.eip + i);
        if (state->status != EmuStatus::Running) return (int)state->status;
    }
    
    DecodedOp op;
    if (!DecodeInstruction(buf, &op)) {
        std::memset(&op, 0, sizeof(op));
        op.length = 1;
        op.handler_index = 0x10B; 
    }
    op.meta.flags.is_last = 1; 

    HandlerFunc h = nullptr;
    if (op.handler_index < 1024) {
        h = g_Handlers[op.handler_index];
    }

    if (h) {
         uint32_t old_eip = state->ctx.eip;
         h(state, &op);
         // Advance EIP if handler didn't change it AND no fault occurred.
         // 'Stopped' usually means syscall handled, so we MUST advance.
         if (state->status != EmuStatus::Fault && state->ctx.eip == old_eip) {
             state->ctx.eip += op.length;
         }
    } else {
         if (!state->hooks.on_invalid_opcode(state)) {
             state->status = EmuStatus::Fault;
             state->fault_vector = 6;
         }
    }
    
    return (int)state->status;
}

int X86_GetStatus(EmuState* state) {
    return (int)state->status;
}

// ----------------------------------------------------------------------------
// Callbacks
// ----------------------------------------------------------------------------

void X86_SetFaultCallback(EmuState* state, FaultHandler handler, void* userdata) {
    state->fault_handler = handler;
    state->fault_userdata = userdata;
}

void X86_SetMemHook(EmuState* state, MemHook hook, void* userdata) {
    state->mem_hook = hook;
    state->mem_userdata = userdata;
}

void X86_SetInterruptHook(EmuState* state, uint8_t vector, InterruptHandler hook, void* userdata) {
    state->interrupt_handlers[vector] = hook;
    state->interrupt_userdata[vector] = userdata;
    
    state->hooks.set_interrupt_hook(vector, [vector](EmuState* s, uint8_t v) {
        if (s->interrupt_handlers[vector]) {
            bool handled = s->interrupt_handlers[vector](s, (uint32_t)v, s->interrupt_userdata[vector]) != 0;
            if (handled) {
                s->status = EmuStatus::Stopped;
            }
            return handled;
        }
        return false;
    });
}

// ----------------------------------------------------------------------------
// Diagnostics
// ----------------------------------------------------------------------------

int32_t X86_GetFaultVector(EmuState* state) {
    if (state->status != EmuStatus::Fault) return -1;
    return (int32_t)state->fault_vector;
}

} // extern "C"