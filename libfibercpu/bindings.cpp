#include "bindings.h"
#include <execinfo.h>
#include <unistd.h>
#include <algorithm>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include "decoder.h"
#include "dispatch.h"
#include "hooks.h"
#include "mem/mmu.h"
#include "ops.h"
#include "state.h"

using namespace fiberish;
using MicroTLB = mem::MicroTLB;

extern "C" {

// ----------------------------------------------------------------------------
// Internal Bridge Callbacks
// ----------------------------------------------------------------------------

static void InternalFaultBridge(void* opaque, uint32_t addr, int is_write) {
    EmuState* state = static_cast<EmuState*>(opaque);
    state->fault_vector = 14;  // #PF
    state->fault_addr = addr;
    if (state->fault_handler) {
        state->fault_handler(state, addr, is_write, state->fault_userdata);
    } else {
        // Default behavior if no user handler: Trigger Fault
        state->status = EmuStatus::Fault;
    }
}

static void InternalMemHookBridge(void* opaque, uint32_t addr, uint32_t size, int is_write, uint64_t val) {
    EmuState* state = static_cast<EmuState*>(opaque);
    if (state->mem_hook) {
        state->mem_hook(state, addr, size, is_write, val, state->mem_userdata);
    }
}

// Invalidate all blocks on a specific page
static void X86_InvalidatePage(EmuState* state, uint32_t page_addr) {
    uint32_t page_idx = page_addr >> 12;
    auto it = state->page_to_blocks.find(page_idx);
    if (it != state->page_to_blocks.end()) {
        // Remove all referenced blocks from the cache
        for (uint32_t eip : it->second) {
            auto block_it = state->block_cache.find(eip);
            if (block_it != state->block_cache.end()) {
                block_it->second->Invalidate();
                state->block_cache.erase(block_it);
            }
        }
        // Crucial: Remove the entire mapping for this page to prevent
        // the vector from growing indefinitely with stale EIPs.
        state->page_to_blocks.erase(it);
    }
}

static void InternalSmcBridge(void* opaque, uint32_t addr) {
    EmuState* state = static_cast<EmuState*>(opaque);
    // Invalidate the page containing 'addr'
    X86_InvalidatePage(state, addr);
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
    state->ctx.eflags = 0x202;  // IF=1, Reserved=1
    state->ctx.eflags_mask = 0x240DD5;

    // Default FPU State
    state->ctx.fpu_cw = 0x037F;
    // Hooks initialized by default constructor of HookManager

    // Initialize Dummy Invalid Block
    // Allocate enough for 0 ops (just sizeof(BasicBlock)) as it has flexible array member ops[1].
    // Wait, ops[1] means size is sizeof(BasicBlock).
    // We don't need extra ops for the dummy block.
    void* mem = state->block_pool.allocate(sizeof(BasicBlock));
    state->dummy_invalid_block = new (mem) BasicBlock;
    state->dummy_invalid_block->is_valid = false;
    state->dummy_invalid_block->inst_count = 0;
    // We should safely initialize ops[0] just in case?
    // No, it won't be accessed if is_valid check fails or if we don't chain to it.
    // But OpExitBlock checks dummy->is_valid (false) and doesn't execute.
    state->ctx.fpu_sw = 0x0000;
    state->ctx.fpu_tw = 0xFFFF;
    state->ctx.fpu_top = 0;

    // Link pointers
    state->ctx.mmu = &state->mmu;
    state->ctx.hooks = &state->hooks;

    // Link MMU to State Status
    state->mmu.set_status_ptr(&state->status, &state->fault_vector);

    state->mmu.set_fault_callback(InternalFaultBridge, state);
    state->mmu.set_smc_callback(InternalSmcBridge, state);

    state->tsc_start_time = std::chrono::steady_clock::now();

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
    // state->mmu.set_mem_hook(InternalMemHookBridge, state);
    state->mmu.set_smc_callback(InternalSmcBridge, state);

    // Reuse parent's external handlers & userdata
    state->fault_handler = parent->fault_handler;
    state->fault_userdata = parent->fault_userdata;  // This might need care in Go!
    state->mem_hook = parent->mem_hook;
    state->mem_userdata = parent->mem_userdata;

    if (state->mem_hook) {
        state->mmu.set_mem_hook(InternalMemHookBridge, state);
    }

    // Interrupt handlers
    for (int i = 0; i < 256; ++i) {
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
    for (int i = 0; i < 6; ++i) {
        state->ctx.seg_base[i] = parent->ctx.seg_base[i];
    }

    // Initialize Dummy Invalid Block (same as X86_Create)
    // This is CRITICAL for OpExitBlock which assumes next_block is never nullptr
    void* mem = state->block_pool.allocate(sizeof(BasicBlock));
    state->dummy_invalid_block = new (mem) BasicBlock;
    state->dummy_invalid_block->is_valid = false;
    state->dummy_invalid_block->inst_count = 0;

    return state;
}

void X86_Destroy(EmuState* state) {
    if (state) {
        // Monotonic buffer resource will automatically release all memory
        // allocated from it (BasicBlocks and their vectors) when 'state' is deleted.
        delete state;
    }
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

uint32_t X86_GetEIP(EmuState* state) { return state->ctx.eip; }

void X86_SetEIP(EmuState* state, uint32_t eip) { state->ctx.eip = eip; }

uint32_t X86_GetEFLAGS(EmuState* state) { return state->ctx.eflags; }

void X86_SetEFLAGS(EmuState* state, uint32_t val) { state->ctx.eflags = val; }

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
// FPU Access
// ----------------------------------------------------------------------------

uint16_t X86_GetFCW(EmuState* state) { return state->ctx.fpu_cw; }
void X86_SetFCW(EmuState* state, uint16_t val) {
    state->ctx.fpu_cw = val;
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);
}
uint16_t X86_GetFSW(EmuState* state) { return state->ctx.fpu_sw; }
void X86_SetFSW(EmuState* state, uint16_t val) {
    state->ctx.fpu_sw = val;
    state->ctx.fpu_top = (val >> 11) & 7;
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);
}
uint16_t X86_GetFTW(EmuState* state) { return state->ctx.fpu_tw; }
void X86_SetFTW(EmuState* state, uint16_t val) { state->ctx.fpu_tw = val; }

void X86_ReadFPUReg(EmuState* state, int idx, uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        int phys_idx = (state->ctx.fpu_top + idx) & 7;
        std::memcpy(val, &state->ctx.fpu_regs[phys_idx], 10);
    }
}

void X86_WriteFPUReg(EmuState* state, int idx, const uint8_t* val) {
    if (idx >= 0 && idx < 8 && val) {
        int phys_idx = (state->ctx.fpu_top + idx) & 7;
        std::memcpy(&state->ctx.fpu_regs[phys_idx], val, 10);
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

void X86_MemMap(EmuState* state, uint32_t addr, uint32_t size, uint8_t perms) { state->mmu.mmap(addr, size, perms); }

void X86_MemUnmap(EmuState* state, uint32_t addr, uint32_t size) {
    state->mmu.munmap(addr, size);

    // Also invalidate JIT cache for this range (code may have been translated from these pages)
    uint32_t start_page = addr >> 12;
    uint32_t end_page = (addr + size + 0xFFF) >> 12;
    for (uint32_t p = start_page; p < end_page; ++p) {
        auto it = state->page_to_blocks.find(p);
        if (it != state->page_to_blocks.end()) {
            for (uint32_t block_eip : it->second) {
                // Remove from block cache if it exists
                auto block_it = state->block_cache.find(block_eip);
                if (block_it != state->block_cache.end()) {
                    block_it->second->Invalidate();
                    state->block_cache.erase(block_it);
                }
            }
            state->page_to_blocks.erase(it);
        }
    }
}

void X86_MemWrite(EmuState* state, uint32_t addr, const uint8_t* data, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        mem::MicroTLB utlb;
        state->mmu.write<uint8_t>(addr + i, data[i], &utlb);
    }
}

void X86_MemRead(EmuState* state, uint32_t addr, uint8_t* val, uint32_t size) {
    for (uint32_t i = 0; i < size; ++i) {
        mem::MicroTLB utlb;
        val[i] = state->mmu.read<uint8_t>(addr + i, &utlb);
    }
}

int X86_MemIsDirty(EmuState* state, uint32_t addr) {
    mem::Property p = state->mmu.get_property(addr);
    return mem::has_property(p, mem::Property::Dirty) ? 1 : 0;
}

// ----------------------------------------------------------------------------
// Execution
// ----------------------------------------------------------------------------

void X86_Run(EmuState* state, uint32_t end_eip, uint64_t max_insts) {
    state->status = EmuStatus::Running;
    uint64_t total_run_insts = 0;

    // Reset chaining state for this run
    state->last_block = nullptr;

    // Sync FPU state before starting
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);

    while (state->status == EmuStatus::Running) {
        uint32_t eip = state->ctx.eip;

        if (end_eip != 0 && eip == end_eip) {
            state->status = EmuStatus::Stopped;
            break;
        }
        if (max_insts != 0 && total_run_insts >= max_insts) {
            state->status = EmuStatus::Stopped;
            break;
        }

        auto it = state->block_cache.find(eip);
        if (it == state->block_cache.end()) {
            // Allocate and Decode block
            // DecodeBlock now handles allocation via PMR pool with flexible array member usage.
            BasicBlock* new_block = DecodeBlock(state, eip, end_eip, 0);

            if (!new_block) {
                state->status = EmuStatus::Fault;
                break;
            }

            // Insert into cache
            auto res = state->block_cache.insert({eip, new_block});
            // If failed to insert (should not happen), use what we have
            if (!res.second) {
                // Theoretically memory leak in pool if we cared about reclaiming this single block...
                // But monotonic pool doesn't support dealloc.
                new_block = res.first->second;
            } else {
                // Register Block for Page Invalidation (SMC)
                uint32_t page_addr = eip & 0xFFFFF000;
                state->page_to_blocks[page_addr].push_back(eip);

                // If block crosses page, register for the second page too
                // Note: new_block->end_eip might be past the page boundary
                // We only check start/end pages.
                // Our decoder limits block to not cross 2 pages so max 2 pages involved.
                // end_eip is exclusive.
                if (((new_block->end_eip - 1) & 0xFFFFF000) != page_addr) {
                    uint32_t page2 = (new_block->end_eip - 1) & 0xFFFFF000;
                    state->page_to_blocks[page2].push_back(eip);
                }
            }
            it = state->block_cache.find(eip);
        }

        // Get raw pointer
        BasicBlock* block_ptr = it->second;

        // Skip invalid blocks (shouldn't happen if we erase them on invalidation,
        // but safe to check if we change logic)
        if (!block_ptr->is_valid) {
            // Re-decode? Or Fault?
            // If it's in cache but invalid, it means we messed up invalidation logic (didn't erase).
            // Let's treat it as a miss and re-decode.
            state->block_cache.erase(it);
            continue;
        }

        // Link previous block to this one for chaining
        if (state->last_block && state->last_block->inst_count > 0) {
            // Access the Sentinel Op (always at index inst_count)
            // Because ops contains [Inst 0, Inst 1, ..., Inst N-1, Sentinel]
            // inst_count = N. So ops[N] is the Sentinel.
            state->last_block->ops[state->last_block->inst_count].next_block = block_ptr;
        }
        state->last_block = block_ptr;
        if (block_ptr->inst_count > 0) {
            DecodedOp* head = &block_ptr->ops[0];
            HandlerFunc h = nullptr;
            int32_t offset = head->handler_offset;
            if (offset != 0) {
                h = (HandlerFunc)((intptr_t)g_HandlerBase + offset);
            }

            if (h) {
                int64_t batch_limit = 1000;
                if (max_insts != 0) {
                    uint64_t remaining_budget = max_insts - total_run_insts;
                    if (remaining_budget < (uint64_t)batch_limit) {
                        batch_limit = (int64_t)remaining_budget;
                    }
                }

                int64_t initial_batch_limit = batch_limit;
                // Subtract the FIRST block's size from the limit
                batch_limit -= block_ptr->inst_count;

                // h will return the remaining budget
                MicroTLB utlb;
                int64_t remaining = h(state, head, batch_limit, utlb);
                total_run_insts += (initial_batch_limit - remaining);
            } else {
                OpUd2(state, head);
                break;
            }
        }
    }

    // Sync FPU state back
    f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);
}

void X86_EmuStop(EmuState* state) {
    if (state) state->status = EmuStatus::Stopped;
}

void X86_EmuFault(EmuState* state) {
    if (state) state->status = EmuStatus::Fault;
}

void X86_EmuYield(EmuState* state) {
    if (state) state->status = EmuStatus::Yield;
}

int X86_Step(EmuState* state) {
    state->status = EmuStatus::Running;

    // Sync FPU state before starting
    f80_sync_to_soft(state->ctx.fpu_cw, state->ctx.fpu_sw);

    uint8_t buf[16];
    for (int i = 0; i < 16; ++i) {
        mem::MicroTLB utlb;
        buf[i] = state->mmu.read<uint8_t>(state->ctx.eip + i, &utlb);
        if (state->status != EmuStatus::Running) {
            f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);
            return (int)state->status;
        }
    }

    DecodedOp ops[2];
    std::memset(ops, 0, sizeof(ops));

    if (!DecodeInstruction(buf, &ops[0])) {
        ops[0].length = 1;
        // 0x10B = UD2
        HandlerFunc ud2 = g_Handlers[0x10B];
        ops[0].handler_offset = (int32_t)((intptr_t)ud2 - (intptr_t)g_HandlerBase);
    }

    // Sentinel
    HandlerFunc exit_h = g_ExitHandlers[0];
    ops[1].handler_offset = (int32_t)((intptr_t)exit_h - (intptr_t)g_HandlerBase);
    ops[1].next_block = state->dummy_invalid_block;

    // Run first op
    HandlerFunc h = nullptr;
    int32_t offset = ops[0].handler_offset;
    if (offset != 0) {
        h = (HandlerFunc)((intptr_t)g_HandlerBase + offset);
    }

    if (h) {
        uint32_t old_eip = state->ctx.eip;
        MicroTLB utlb;
        h(state, &ops[0], 0, utlb);  // Limit 0 ensures it returns after 1 inst + sentinel

        // Advance EIP if handler didn't change it AND no fault occurred.
        if (state->status != EmuStatus::Fault && state->ctx.eip == old_eip) {
            state->ctx.eip += ops[0].length;
        }
    } else {
        if (!state->hooks.on_invalid_opcode(state)) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 6;
        }
    }

    // Sync FPU state back
    f80_sync_from_soft(&state->ctx.fpu_cw, &state->ctx.fpu_sw);

    return (int)state->status;
}

int X86_GetStatus(EmuState* state) { return (int)state->status; }

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

    if (hook) {
        state->mmu.set_mem_hook(InternalMemHookBridge, state);
    } else {
        state->mmu.set_mem_hook(nullptr, nullptr);
    }
}

void X86_SetInterruptHook(EmuState* state, uint8_t vector, InterruptHandler hook, void* userdata) {
    state->interrupt_handlers[vector] = hook;
    state->interrupt_userdata[vector] = userdata;

    state->hooks.set_interrupt_hook(vector, [vector](EmuState* s, uint8_t v) {
        if (s->interrupt_handlers[vector]) {
            bool handled = s->interrupt_handlers[vector](s, (uint32_t)v, s->interrupt_userdata[vector]) != 0;
            if (handled && s->status == EmuStatus::Running) {
                s->status = EmuStatus::Stopped;
            }
            return handled;
        }
        return false;
    });
}

int32_t X86_GetFaultVector(EmuState* state) {
    if (state->status != EmuStatus::Fault) return -1;
    return (int32_t)state->fault_vector;
}

void X86_FlushCache(EmuState* state) {
    if (state) {
        state->block_cache.clear();
        state->page_to_blocks.clear();
    }
}

void X86_InvalidateRange(EmuState* state, uint32_t addr, uint32_t size) {
    if (!state || size == 0) return;

    uint32_t start_page = addr >> 12;
    uint32_t end_page = (addr + size - 1) >> 12;

    for (uint32_t p = start_page; p <= end_page; ++p) {
        auto it = state->page_to_blocks.find(p);
        if (it != state->page_to_blocks.end()) {
            for (uint32_t eip : it->second) {
                auto block_it = state->block_cache.find(eip);
                if (block_it != state->block_cache.end()) {
                    block_it->second->Invalidate();
                    state->block_cache.erase(block_it);
                }
            }
            state->page_to_blocks.erase(it);
        }
    }
}

void X86_SetTscFrequency(EmuState* state, uint64_t freq) {
    if (state) state->tsc_frequency = freq;
}

void X86_SetTscMode(EmuState* state, int mode) {
    if (state) state->tsc_mode = mode;
}

void X86_SetTscOffset(EmuState* state, uint64_t offset) {
    if (state) state->tsc_offset = offset;
}

void X86_GetTlbStats(EmuState* state, X86_TlbStats* stats) {
#ifdef ENABLE_TLB_STATS
    if (state && stats) {
        stats->l1_read_hits = state->mmu.stats.l1_read_hits;
        stats->l1_write_hits = state->mmu.stats.l1_write_hits;
        stats->l2_read_hits = state->mmu.stats.l2_read_hits;
        stats->l2_write_hits = state->mmu.stats.l2_write_hits;
        stats->read_misses = state->mmu.stats.read_misses;
        stats->write_misses = state->mmu.stats.write_misses;
        stats->total_reads = state->mmu.stats.total_reads;
        stats->total_writes = state->mmu.stats.total_writes;
    }
#else
    if (stats) std::memset(stats, 0, sizeof(X86_TlbStats));
#endif
}

void X86_ResetTlbStats(EmuState* state) {
#ifdef ENABLE_TLB_STATS
    if (state) state->mmu.stats.reset();
#endif
}

int X86_DumpStats(EmuState* state, char* buffer, size_t buffer_size) {
#ifdef ENABLE_TLB_STATS
    if (!state || !buffer || buffer_size == 0) return -1;
    auto& s = state->mmu.stats;
    int n = snprintf(buffer, buffer_size,
                     "{\"l1_read_hits\":%llu,\"l1_write_hits\":%llu,"
                     "\"l2_read_hits\":%llu,\"l2_write_hits\":%llu,"
                     "\"read_misses\":%llu,\"write_misses\":%llu,"
                     "\"total_reads\":%llu,\"total_writes\":%llu}",
                     (unsigned long long)s.l1_read_hits, (unsigned long long)s.l1_write_hits,
                     (unsigned long long)s.l2_read_hits, (unsigned long long)s.l2_write_hits,
                     (unsigned long long)s.read_misses, (unsigned long long)s.write_misses,
                     (unsigned long long)s.total_reads, (unsigned long long)s.total_writes);
    return n;
#else
    if (buffer && buffer_size > 0) {
        strncpy(buffer, "{}", buffer_size);
        return 2;
    }
    return -1;
#endif
}

}  // extern "C"