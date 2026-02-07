#pragma once

#include <ankerl/unordered_dense.h>
#include <chrono>
#include <memory>
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
    
    // Intrusive Pointer Wrapper for automatic Retain/Release
    struct BlockPtr {
        BasicBlock* ptr = nullptr;
        
        BlockPtr() = default;
        BlockPtr(BasicBlock* p) : ptr(p) { if(ptr) ptr->Retain(); }
        BlockPtr(const BlockPtr& other) : ptr(other.ptr) { if(ptr) ptr->Retain(); }
        BlockPtr(BlockPtr&& other) noexcept : ptr(other.ptr) { other.ptr = nullptr; }
        
        ~BlockPtr() { if(ptr) ptr->Release(); }
        
        BlockPtr& operator=(const BlockPtr& other) {
            if (this != &other) {
                if(ptr) ptr->Release();
                ptr = other.ptr;
                if(ptr) ptr->Retain();
            }
            return *this;
        }
        
        BlockPtr& operator=(BasicBlock* p) {
            if (ptr != p) {
                if(ptr) ptr->Release();
                ptr = p;
                if(ptr) ptr->Retain();
            }
            return *this;
        }

        BasicBlock* get() const { return ptr; }
        BasicBlock* operator->() const { return ptr; }
        operator bool() const { return ptr != nullptr; }
    };

    // Block Cache - Stores raw pointers, but we treat them as "Strong Refs" owned by the map.
    // However, std::map/unordered_map doesn't automatically call Release on raw pointers when erased.
    // So we use our BlockPtr wrapper as the value type to ensure Release is called on erase/clear.
    ankerl::unordered_dense::map<uint32_t, BlockPtr> block_cache;
    
    // Reverse Mapping: Page Address (aligned) -> List of EIPs in that page
    // Using vector is simple enough. For massive code pages, a set might be better but overhead is higher.
    ankerl::unordered_dense::map<uint32_t, std::vector<uint32_t>> page_to_blocks;

    // Fault Info
    uint8_t fault_vector = 0xFF;  // 0xFF = No Fault
    uint32_t fault_addr = 0;

    // Chaining Info
    BlockPtr last_block;

    // Callback Storage
    FaultHandler fault_handler = nullptr;
    void* fault_userdata = nullptr;

    MemHook mem_hook = nullptr;
    void* mem_userdata = nullptr;

    InterruptHandler interrupt_handlers[256] = {nullptr};
    void* interrupt_userdata[256] = {nullptr};

    // TSC State
    uint64_t tsc_frequency = 1000000000; // Default 1GHz
    uint64_t tsc_offset = 0;
    int tsc_mode = 1; // 0: Fixed Increment, 1: Real-time
    uint64_t tsc_fixed_counter = 0; // For mode 0
    std::chrono::steady_clock::time_point tsc_start_time;
};

}  // namespace x86emu