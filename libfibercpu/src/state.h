#pragma once

#include <ankerl/unordered_dense.h>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <variant>
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

enum class BlockStopReason : uint8_t {
    Unknown = 0,
    LimitEip,
    FetchFault,
    DecodeFault,
    CrossPageFault,
    ControlFlow,
    PageCross,
    MaxInsts,
};

struct BlockStats {
    uint64_t block_count = 0;
    uint64_t total_block_insts = 0;
    uint64_t stop_reason_counts[8] = {};
    uint64_t inst_histogram[65] = {};
    uint64_t block_concat_attempts = 0;
    uint64_t block_concat_success = 0;
    uint64_t block_concat_success_direct_jmp = 0;
    uint64_t block_concat_success_jcc_fallthrough = 0;
    uint64_t block_concat_reject_not_concat_terminal = 0;
    uint64_t block_concat_reject_cross_page = 0;
    uint64_t block_concat_reject_size_limit = 0;
    uint64_t block_concat_reject_loop = 0;
    uint64_t block_concat_reject_target_missing = 0;

    void Record(uint32_t inst_count, BlockStopReason reason) {
        block_count++;
        total_block_insts += inst_count;
        stop_reason_counts[static_cast<size_t>(reason)]++;
        inst_histogram[std::min<uint32_t>(inst_count, 64)]++;
    }
};

enum class PendingMemOp : uint8_t {
    None = 0,
    Read = 1,
    Write = 2,
};

struct EmuState {
    Context ctx;
    mem::Mmu mmu;
    HookManager hooks;
    EmuStatus status = EmuStatus::Stopped;

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
    // Set when a write targets an executable guest page while exec writes are
    // disallowed. X86_Run consumes this and re-executes the current instruction
    // in a safe single-instruction mode.
    bool smc_write_to_exec = false;
    // Temporary override used by X86_Run to permit exactly one instruction to
    // write an executable page after an SMC-triggered yield.
    bool allow_write_exec_page = false;
    // True only while guest instructions are actively executing. External API
    // memory writes (program loading, tests, patching) must not trigger the
    // SMC rerun path.
    bool intercept_exec_write_for_smc = false;
    BlockStats block_stats;
#ifdef FIBERCPU_ENABLE_HANDLER_PROFILE
    ankerl::unordered_dense::map<uintptr_t, uint64_t> handler_exec_counts;
    DecodedOp* current_block_head = nullptr;
#endif

    // TSC State
    uint64_t tsc_frequency = 1000000000;  // Default 1GHz
    uint64_t tsc_offset = 0;
    int tsc_mode = 1;                // 0: Fixed Increment, 1: Real-time
    uint64_t tsc_fixed_counter = 0;  // For mode 0
    std::chrono::steady_clock::time_point tsc_start_time;

    // Helper function to sync eip
    void sync_eip_to_op_start(const DecodedOp* op) { ctx.eip = op->next_eip - op->len; }
    void sync_eip_to_op_end(const DecodedOp* op) { ctx.eip = op->next_eip; }

    union MemOpUnion {
        MemReadOperation read;
        MemWriteOperation write;

        MemOpUnion() noexcept {}
        ~MemOpUnion() noexcept {}
    };

    PendingMemOp mem_op_type = PendingMemOp::None;
    MemOpUnion mem_op;

    FORCE_INLINE void clear_pending_mem_op() { mem_op_type = PendingMemOp::None; }

    template <typename T>
    FORCE_INLINE mem::MemResult<T> request_read_and_check_pending(uint32_t addr, uint32_t eip) {
        // Use pending value
        if (mem_op_type == PendingMemOp::Read) {
            const auto& read_op = mem_op.read;
            if (read_op.done && read_op.eip == eip) {
                T pending{};
                std::memcpy(&pending, read_op.data.data(), sizeof(T));
                clear_pending_mem_op();
                return pending;
            }
        }

        mem_op_type = PendingMemOp::Read;
        auto& op = mem_op.read;
        op.data = {};
        op.addr = addr;
        op.size = sizeof(T);
        op.eip = eip;
        op.done = false;

        return std::unexpected(mem::FaultCode::PageFault);
    }

    template <typename T>
    FORCE_INLINE mem::MemResult<void> request_write_and_check_pending(uint32_t addr, const T& value, uint32_t eip) {
        static_assert(sizeof(T) <= 16);

        if (mem_op_type == PendingMemOp::Write) {
            const auto& write_op = mem_op.write;
            if (write_op.done && write_op.eip == eip) {
                clear_pending_mem_op();
                return {};
            }
        }

        mem_op_type = PendingMemOp::Write;
        auto& op = mem_op.write;

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
        mem_op_type = PendingMemOp::Write;
        auto& op = mem_op.write;

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
