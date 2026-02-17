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
#include "logger.h"
#include "mem/mmu.h"

namespace fiberish {

struct EmuState;

// Callback signatures for internal storage and C bindings
using FaultHandler = int (*)(EmuState* state, uint32_t addr, int is_write, void* userdata);
using MemHook = void (*)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
using InterruptHandler = int (*)(EmuState* state, uint32_t vector, void* userdata);

struct alignas(16) MemReadOperation {
    std::array<std::byte, 16> data;
    uint32_t addr;
    uint32_t size;
    uint32_t eip;
    bool done;
};

struct alignas(16) MemWriteOperation {
    std::array<std::byte, 16> data;
    uint32_t addr;
    uint32_t size;
    uint32_t eip;
    bool done;
};

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

    LogCallback log_callback = nullptr;
    void* log_userdata = nullptr;

    bool eip_dirty = false;  // External API Set EIP? Warning: only cleared by x86_Run

    // TSC State
    uint64_t tsc_frequency = 1000000000;  // Default 1GHz
    uint64_t tsc_offset = 0;
    int tsc_mode = 1;                // 0: Fixed Increment, 1: Real-time
    uint64_t tsc_fixed_counter = 0;  // For mode 0
    std::chrono::steady_clock::time_point tsc_start_time;

    // Helper function to sync eip
    void sync_eip_to_op_start(const ShimOp* op) { ctx.eip = op->next_eip - op->len; }
    void sync_eip_to_op_end(const ShimOp* op) { ctx.eip = op->next_eip; }

    std::variant<std::monostate, MemReadOperation, MemWriteOperation> mem_op;

    template <typename T>
    FORCE_INLINE mem::MemResult<T> request_read_and_check_pending(uint32_t addr, uint32_t eip) {
        // Use pending value
        if (auto read_op = std::get_if<MemReadOperation>(&mem_op)) {
            if (read_op->done && read_op->eip == eip) {
                T pending{};
                std::memcpy(&pending, read_op->data.data(), sizeof(T));
                mem_op.emplace<0>();  // Clear result
                return pending;
            }
        }

        mem_op = MemReadOperation{.addr = addr, .size = sizeof(T), .data = {}, .done = false, .eip = eip};

        return std::unexpected(mem::FaultCode::PageFault);
    }

    template <typename T>
    FORCE_INLINE mem::MemResult<void> request_write_and_check_pending(uint32_t addr, const T& value, uint32_t eip) {
        static_assert(sizeof(T) <= 16);

        if (auto write_op = std::get_if<MemWriteOperation>(&mem_op)) {
            if (write_op->done && write_op->eip == eip) {
                mem_op.emplace<0>();
                return {};
            }
        }

        auto& op = mem_op.emplace<MemWriteOperation>();

        op.addr = addr;
        op.size = sizeof(T);
        op.done = false;
        op.eip = eip;
        std::memcpy(op.data.data(), &value, sizeof(T));

        return std::unexpected(mem::FaultCode::PageFault);
    }

    template <typename T>
    FORCE_INLINE mem::MemResult<void> request_write_only(uint32_t addr, const T& value, uint32_t eip) {
        static_assert(sizeof(T) <= 16);

        // No check for pending, just request
        auto& op = mem_op.emplace<MemWriteOperation>();

        op.addr = addr;
        op.size = sizeof(T);
        op.done = false;
        op.eip = eip;
        std::memcpy(op.data.data(), &value, sizeof(T));

        return std::unexpected(mem::FaultCode::PageFault);
    }
};

}  // namespace fiberish

#include "mem/mmu_impl.h"