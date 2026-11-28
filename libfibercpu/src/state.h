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

namespace aarch64 {
struct State;
}
struct EmuState;

#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
struct HandlerPairKey {
    uintptr_t first, second;
    bool operator==(const HandlerPairKey&) const = default;
};
struct HandlerPairHash {
    uint64_t operator()(const HandlerPairKey& pair) const {
        return ankerl::unordered_dense::hash<uint64_t>{}(pair.first) ^
               (ankerl::unordered_dense::hash<uint64_t>{}(pair.second) << 1);
    }
};
struct HandlerNGramKey {
    std::array<uintptr_t, 4> handlers;
    bool operator==(const HandlerNGramKey&) const = default;
};
struct HandlerNGramHash {
    uint64_t operator()(const HandlerNGramKey& key) const {
        uint64_t hash = 0;
        for (auto handler : key.handlers) hash = (hash << 7) ^ ankerl::unordered_dense::hash<uint64_t>{}(handler);
        return hash;
    }
};
#endif

// Callback signatures for internal storage and C bindings
using FaultHandler = int (*)(EmuState* state, uint32_t addr, int is_write, void* userdata);
using MemHook = void (*)(EmuState* state, uint32_t addr, uint32_t size, int is_write, uint64_t val, void* userdata);
using InterruptHandler = int (*)(EmuState* state, uint32_t vector, void* userdata);

struct alignas(16) PendingMemoryAccess {
    std::array<std::byte, 16> data;
    const DecodedOp* instruction;
    uint32_t addr;
    uint8_t size;
    bool done;
    bool can_restart;
};

static_assert(sizeof(PendingMemoryAccess) == 32);

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
    bool native_execution_active = false;

    // Callback Storage
    FaultHandler fault_handler = nullptr;
    bool extended_fault_callback = false;
    // A deferred fault exits X86_Run, discarding the temporary memory op.
    // Only paths that can restart at the saved EIP may set this.
    bool can_restart_memory_fault = false;
    uint32_t rep_allowance = 0;
    uint32_t rep_iterations = 0;
    void* fault_userdata = nullptr;

    MemHook mem_hook = nullptr;
    void* mem_userdata = nullptr;

    InterruptHandler interrupt_handlers[256] = {nullptr};
    void* interrupt_userdata[256] = {nullptr};

    LogCallback log_callback = nullptr;
    void* log_userdata = nullptr;

    bool eip_dirty = false;  // External API Set EIP; cleared at execution entry.
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
#ifdef FIBERCPU_ENABLE_SEQUENCE_PROFILE
    // Cumulative over Run calls, independent of code-cache lifetime. No shared/TLS counters.
    ankerl::unordered_dense::map<HandlerPairKey, uint64_t, HandlerPairHash> sequence_counts;
    ankerl::unordered_dense::map<HandlerNGramKey, uint64_t, HandlerNGramHash> ngram_counts;
    DecodedOp* sequence_expected_op = nullptr;
    std::array<uintptr_t, 2> sequence_history{};
    uint8_t sequence_history_length = 0;
#endif
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
    void sync_eip_to_op_start(const DecodedOp* op) { ctx.eip = last_block->GetStartEip(op); }
    void sync_eip_to_op_end(const DecodedOp* op) { ctx.eip = last_block->GetNextEip(op); }

    // Pending results belong to one native replay chain. Clear the tag
    // before leaving execution or returning stack-backed decoded storage.
    PendingMemOp mem_op_type = PendingMemOp::None;
    PendingMemoryAccess mem_op;
    alignas(16) std::array<uint8_t, 16> wasm_jit_scratch{};
    BasicBlock* llvm_hot_block = nullptr;
    std::shared_ptr<aarch64::State> arm;
    uint64_t arm_fault_address = 0;
#if FIBERCPU_GUEST_AARCH64
    std::unique_ptr<mem::ArmMmuRuntime> arm_mmu;
#endif

    FORCE_INLINE void clear_pending_mem_op() { mem_op_type = PendingMemOp::None; }

    template <typename T>
    FORCE_INLINE mem::MemResult<T> request_read_and_check_pending(uint32_t addr, const DecodedOp* instruction) {
        static_assert(sizeof(T) <= 16);

        // Use pending value
        if (mem_op_type == PendingMemOp::Read) {
            const auto& read_op = mem_op;
            if (read_op.done && read_op.instruction == instruction) {
                T pending{};
                std::memcpy(&pending, read_op.data.data(), sizeof(T));
                clear_pending_mem_op();
                return pending;
            }
        }

        mem_op_type = PendingMemOp::Read;
        auto& op = mem_op;
        op.data = {};
        op.addr = addr;
        op.size = sizeof(T);
        op.instruction = instruction;
        op.done = false;

        return std::unexpected(mem::FaultCode::PageFault);
    }

    template <typename T, bool can_restart = false>
    FORCE_INLINE mem::MemResult<void> request_write_and_check_pending(uint32_t addr, const T& value,
                                                                      const DecodedOp* instruction) {
        static_assert(sizeof(T) <= 16);

        if (mem_op_type == PendingMemOp::Write) {
            const auto& write_op = mem_op;
            if (write_op.done && write_op.instruction == instruction) {
                clear_pending_mem_op();
                return {};
            }
        }

        mem_op_type = PendingMemOp::Write;
        auto& op = mem_op;

        op.addr = addr;
        op.size = sizeof(T);
        op.done = false;
        op.can_restart = can_restart;
        op.instruction = instruction;
        std::memcpy(op.data.data(), &value, sizeof(T));

        return std::unexpected(mem::FaultCode::PageFault);
    }

    template <typename T, bool can_restart = false>
    FORCE_INLINE mem::MemResult<void> request_write_only(uint32_t addr, const T& value, const DecodedOp* instruction) {
        static_assert(sizeof(T) <= 16);

        // No check for pending, just request
        mem_op_type = PendingMemOp::Write;
        auto& op = mem_op;

        op.addr = addr;
        op.size = sizeof(T);
        op.done = false;
        op.can_restart = can_restart;
        op.instruction = instruction;
        std::memcpy(op.data.data(), &value, sizeof(T));

        return std::unexpected(mem::FaultCode::PageFault);
    }
};

// Used only at instruction boundaries where retrying can reproduce the same
// architectural state, including between successfully committed REP iterations.
class RestartableMemoryFaultScope {
public:
    explicit RestartableMemoryFaultScope(EmuState* state)
        : state_(state), previous_(std::exchange(state->can_restart_memory_fault, true)) {}
    ~RestartableMemoryFaultScope() { state_->can_restart_memory_fault = previous_; }

private:
    EmuState* state_;
    bool previous_;
};

FORCE_INLINE uint32_t GetNextEip(const EmuState* state, const DecodedOp* op) {
    return BakeableBlockEip<LLVMBlockEipField::Next>(op, state->last_block->GetNextEip(op));
}

template <unsigned Slots = 1>
FORCE_INLINE uint32_t GetCallReturnEip(const EmuState* state, const DecodedOp* op) {
    // Calls end the decoded block.
    assert(op + Slots == state->last_block->Sentinel());
    return BakeableBlockEip<LLVMBlockEipField::Next>(op, state->last_block->sentinel_eip);
}

FORCE_INLINE uint8_t GetOpLength(const EmuState* state, const DecodedOp* op) {
    return state->last_block->GetOpLength(state->last_block->LogicalIndex(op));
}

}  // namespace fiberish

#include "mem/mmu_impl.h"
