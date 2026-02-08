#pragma once

#include <ankerl/unordered_dense.h>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <memory_resource>
#include <vector>
#include "common.h"
#include "decoder.h"  // For BasicBlock definition
#include "hooks.h"
#include "mem/mmu.h"

namespace fiberish {

struct EmuState;

// Callback signatures for internal storage and C bindings
using FaultHandler = void (*)(EmuState* state, uint32_t addr, int is_write, void* userdata);
using MemHook = void (*)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
using InterruptHandler = int (*)(EmuState* state, uint32_t vector, void* userdata);

struct EmuState {
    Context ctx;
    mem::Mmu mmu;
    HookManager hooks;
    EmuStatus status = EmuStatus::Stopped;

    // PMR Allocation
    std::pmr::monotonic_buffer_resource block_pool;
    std::pmr::polymorphic_allocator<BasicBlock> block_alloc{&block_pool};

    // Block Cache - Stores raw pointers. Blocks are owned by block_pool.
    ankerl::unordered_dense::map<uint32_t, BasicBlock*> block_cache;

    // Optimization: Dummy "Invalid" block.
    // next_block pointers are initialized to this instead of nullptr.
    // This allows removing the "if (next_block)" check in OpExitBlock.
    BasicBlock* dummy_invalid_block = nullptr;

    // Reverse Mapping: Page Address (aligned) -> List of EIPs in that page
    // Using vector is simple enough. For massive code pages, a set might be better but overhead is higher.
    ankerl::unordered_dense::map<uint32_t, std::vector<uint32_t>> page_to_blocks;

    // Fault Info
    uint8_t fault_vector = 0xFF;  // 0xFF = No Fault
    uint32_t fault_addr = 0;

    // Chaining Info
    BasicBlock* last_block = nullptr;

    // Callback Storage
    FaultHandler fault_handler = nullptr;
    void* fault_userdata = nullptr;

    MemHook mem_hook = nullptr;
    void* mem_userdata = nullptr;

    InterruptHandler interrupt_handlers[256] = {nullptr};
    void* interrupt_userdata[256] = {nullptr};

    bool eip_dirty = false;  // External API Set EIP?

    // TSC State
    uint64_t tsc_frequency = 1000000000;  // Default 1GHz
    uint64_t tsc_offset = 0;
    int tsc_mode = 1;                // 0: Fixed Increment, 1: Real-time
    uint64_t tsc_fixed_counter = 0;  // For mode 0
    std::chrono::steady_clock::time_point tsc_start_time;

    // Precise Exception Support
    // When a fault occurs in the fast dispatch loop, we swap the NEXT instruction's handler
    // with HandlerInterrupt. This field stores the original handler to be restored.
    // Use void* to avoid circular dependency with decoder.h if HandlerFunc not visible here (it is visible via
    // decoder.h include) Actually decoder.h is included. Wait, EmuState forward declared in decoder.h, but state.h
    // includes decoder.h. decoder.h includes common.h. state.h includes decoder.h.
    int64_t (*saved_handler)(EmuState* RESTRICT, DecodedOp* RESTRICT, int64_t, mem::MicroTLB);
};

}  // namespace fiberish

#include "mem/mmu_impl.h"