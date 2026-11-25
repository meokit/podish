#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "../musttail.h"

// Apple Clang's address sanitizer does not preserve its shadow-address
// temporaries across preserve_none calls. Keep tail dispatch with the standard
// ABI when compiling with address instrumentation.
#if defined(__clang__) && !defined(FIBERCPU_AARCH64_STANDARD_THREAD_ABI)
#if __has_feature(address_sanitizer)
#define FIBERCPU_AARCH64_STANDARD_THREAD_ABI 1
#endif
#endif

#ifdef FIBERCPU_AARCH64_STANDARD_THREAD_ABI
#define FIBERCPU_AARCH64_THREAD_ABI
#else
#define FIBERCPU_AARCH64_THREAD_ABI ATTR_PRESERVE_NONE
#endif

#ifndef FIBERCPU_AARCH64_PIN_REG
#define FIBERCPU_AARCH64_PIN_REG -1
#endif
static_assert(FIBERCPU_AARCH64_PIN_REG >= -1 && FIBERCPU_AARCH64_PIN_REG <= 30);

namespace fiberish::mem {
struct MicroTLB;
}

namespace fiberish::aarch64 {

namespace detail {
struct RunFrame;
}

struct Vector128 {
    uint64_t lo = 0;
    uint64_t hi = 0;
};

struct State {
    std::array<uint64_t, 31> x{};
    uint64_t sp = 0;
    uint64_t pc = 0;
    uint64_t tpidr_el0 = 0;
    uint64_t tpidrro_el0 = 0;
    std::array<Vector128, 32> v{};
    uint32_t nzcv = 0;
    uint32_t fpcr = 0;
    uint32_t fpsr = 0;
    uint64_t exclusive_address = 0;
    std::array<uint8_t, 16> exclusive_value{};
    uint8_t exclusive_size = 0;
    detail::RunFrame* active_run = nullptr;

    void ClearExclusive() { exclusive_size = 0; }
};

enum class Access : uint8_t { Read, Write, Execute };

struct Memory {
    void* context = nullptr;
    bool (*read)(void*, uint64_t, void*, size_t, Access) = nullptr;
    bool (*write)(void*, uint64_t, const void*, size_t) = nullptr;
    // Resolves the entire 4 KiB page without invoking a fault handler.
    const uint8_t* (*executable_page)(void*, uint64_t page_address) = nullptr;
    // Reads already mapped data without faults, hooks or other callbacks.
    bool (*read_fast)(void*, uint64_t, void*, size_t) = nullptr;
    // Writes already dirty, non-executable data without faults, hooks or callbacks.
    bool (*write_fast)(void*, uint64_t, const void*, size_t) = nullptr;
    // Live entries owned and invalidated by the guest MMU, including hook installation.
    const mem::MicroTLB* read_tlb = nullptr;
};

enum class Stop : uint8_t {
    Budget,
    Syscall,
    Undefined,
    Breakpoint,
    Fault,
    Alignment,
    Continue,
    Branch,
    Refetch,
    Replay
};

struct StepResult {
    Stop reason = Stop::Continue;
    Access access = Access::Read;
    uint16_t immediate = 0;
    uint64_t address = 0;
};

struct DecodedInstruction;
using Handler = StepResult (*)(State&, const Memory&, const DecodedInstruction&);

namespace detail {
struct DispatchSlot;
#if FIBERCPU_AARCH64_PIN_REG >= 0
#define A64_THREAD_PIN_PARAM , [[maybe_unused]] uint64_t pinned
#define A64_THREAD_PIN_PASS , pinned
#else
#define A64_THREAD_PIN_PARAM
#define A64_THREAD_PIN_PASS
#endif

#define A64_THREAD_PARAMS                                                                                   \
    State &state, const ::fiberish::aarch64::detail::DispatchSlot *current, uint64_t remaining_after_block, \
        [[maybe_unused]] const mem::MicroTLB *read_tlb, uint32_t nzcv A64_THREAD_PIN_PARAM
#define A64_THREAD_ARGS state, current, remaining_after_block, read_tlb, nzcv A64_THREAD_PIN_PASS

using ThreadHandler = uint64_t(FIBERCPU_AARCH64_THREAD_ABI*)(A64_THREAD_PARAMS);
}  // namespace detail

enum class Kind : uint8_t {
    Undefined,
    Address,
    AddSubImmediate,
    AddSubShifted,
    AddSubExtended,
    AddSubCarry,
    LogicalImmediate,
    LogicalShifted,
    MoveWide,
    Bitfield,
    Extract,
    ConditionalCompare,
    ConditionalSelect,
    DataOneSource,
    DataTwoSource,
    Multiply,
    BranchImmediate,
    BranchRegister,
    BranchCondition,
    CompareBranch,
    TestBranch,
    LoadStoreImmediate,
    LoadStoreRegister,
    LoadStorePair,
    LoadLiteral,
    LoadStoreExclusive,
    LoadStoreAtomic,
    Syscall,
    Breakpoint,
    System,
    ScalarFloat,
    Vector,
    Count,
};

struct DecodedInstruction {
    Handler handler = nullptr;
    uint64_t immediate = 0;
    union {
        uint64_t mask = 0;
        uint64_t pc_target;
    };
    uint32_t word = 0;
    Kind kind = Kind::Undefined;
    uint8_t rd = 0;
    uint8_t rn = 0;
    uint8_t rm = 0;
    uint8_t ra = 0;
    uint8_t width = 0;
    uint8_t operation = 0;
    uint8_t mode = 0;
    uint8_t shift = 0;
};

namespace detail {
struct HandlerBinding {
    Handler scalar = nullptr;
    ThreadHandler threaded = nullptr;
};
}  // namespace detail

struct Profile {
    void* context = nullptr;
    void (*record)(void*, const DecodedInstruction&) = nullptr;
    detail::HandlerBinding (*resolve)(const DecodedInstruction&, bool omit_nzcv) = nullptr;
    Handler (*resolve_pair)(uint32_t first, uint32_t second, uint32_t dead, detail::ThreadHandler& threaded) = nullptr;
    Handler (*resolve_sequence)(const uint32_t* words, unsigned count, unsigned* matched, uint32_t dead,
                                detail::ThreadHandler& threaded) = nullptr;
    // Scalar bindings have the same callback and refetch behavior as decoded instructions.
    bool trusted_baking = false;
};

struct RunResult {
    Stop reason = Stop::Budget;
    uint64_t executed = 0;
    uint64_t address = 0;
    uint32_t instruction = 0;
    uint16_t immediate = 0;
    Access access = Access::Read;
};

DecodedInstruction Decode(uint32_t instruction);
RunResult Run(State& state, const Memory& memory, uint64_t budget, const Profile& profile = {});

}  // namespace fiberish::aarch64
