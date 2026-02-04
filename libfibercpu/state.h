#pragma once

#include "common.h"
#include "mem/mmu.h"
#include "hooks.h"
#include <ankerl/unordered_dense.h>
#include <memory>
#include "decoder.h" // For BasicBlock definition

namespace x86emu {

struct EmuState;

// Callback signatures for internal storage and C bindings
using FaultHandler = void(*)(EmuState* state, uint32_t addr, int is_write, void* userdata);
using MemHook = void(*)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
using InterruptHandler = int(*)(EmuState* state, uint32_t vector, void* userdata);

struct EmuState {
    Context ctx;
    mem::Mmu mmu;
    HookManager hooks;
    EmuStatus status = EmuStatus::Stopped;
    // Simple Block Cache - Using unique_ptr for pointer stability (needed for chaining)
    ankerl::unordered_dense::map<uint32_t, std::unique_ptr<BasicBlock>> block_cache;
    // Reverse Mapping: Page Address (aligned) -> List of EIPs in that page
    // Using vector is simple enough. For massive code pages, a set might be better but overhead is higher.
    ankerl::unordered_dense::map<uint32_t, std::vector<uint32_t>> page_to_blocks;

    // Fault Info
    uint8_t fault_vector = 0xFF; // 0xFF = No Fault
    uint32_t fault_addr = 0;

    // Chaining Info
    BasicBlock* last_block = nullptr;

    // Callback Storage
    FaultHandler fault_handler = nullptr;
    void* fault_userdata = nullptr;

    MemHook mem_hook = nullptr;
    void* mem_userdata = nullptr;

    InterruptHandler interrupt_handlers[256] = { nullptr };
    void* interrupt_userdata[256] = { nullptr };
};

}