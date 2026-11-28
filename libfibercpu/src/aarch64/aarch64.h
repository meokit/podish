#pragma once

#include <array>
#include <cstddef>
#include <cstdint>

#include "../musttail.h"

#ifndef FIBERCPU_AARCH64_UNMANAGED_CACHE
#define FIBERCPU_AARCH64_UNMANAGED_CACHE 0
#endif

// Apple Clang's address sanitizer does not preserve its shadow-address
// temporaries across preserve_none calls. Keep tail dispatch with the standard
// ABI when compiling with address instrumentation.
#if defined(__clang__) && !defined(FIBERCPU_AARCH64_STANDARD_THREAD_ABI)
#if __has_feature(address_sanitizer)
#define FIBERCPU_AARCH64_STANDARD_THREAD_ABI 1
#endif
#endif

#ifdef FIBERCPU_AARCH64_STANDARD_THREAD_ABI
#define FIBERCPU_AARCH64_HANDLER_ABI
#else
#define FIBERCPU_AARCH64_HANDLER_ABI ATTR_PRESERVE_NONE
#endif

#if defined(_MSC_VER)
#define A64_FORCE_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_FORCE_INLINE inline __attribute__((always_inline))
#else
#define A64_FORCE_INLINE inline
#endif

#if __has_include("aarch64_gpr_pins.generated.h")
#include "aarch64_gpr_pins.generated.h"
#elif defined(FIBERCPU_AARCH64_PIN_REG) && FIBERCPU_AARCH64_PIN_REG >= 0
static_assert(FIBERCPU_AARCH64_PIN_REG <= 30);
#define FIBERCPU_AARCH64_PIN_COUNT 1
#define FIBERCPU_AARCH64_FOR_EACH_PIN(X) X(FIBERCPU_AARCH64_PIN_REG, 0)
#else
#define FIBERCPU_AARCH64_PIN_COUNT 0
#define FIBERCPU_AARCH64_FOR_EACH_PIN(X)
#endif

namespace fiberish::mem {
class SoftTlb;
struct TlbStats;
struct DispatchStats;
}  // namespace fiberish::mem

namespace fiberish::aarch64 {

namespace detail {
struct RunFrame;
struct BasicBlock;
}  // namespace detail

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
    const detail::BasicBlock* last_block = nullptr;
    // The active threaded run supplies this pointer and restores it on exit.
    const mem::SoftTlb* data_tlb = nullptr;

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
    const mem::SoftTlb* data_tlb = nullptr;
    // An MMU owns its blocks across Run calls and invalidates links on mapping changes.
    // A null backing asks lookup_cached_block to probe the executable mapping without invoking callbacks.
    detail::BasicBlock* (*lookup_cached_block)(void*, uint64_t, const uint8_t*) = nullptr;
    void* (*allocate_block)(void*, size_t, uint64_t) = nullptr;
    void (*cache_block)(void*, detail::BasicBlock*) = nullptr;
#if FIBERCPU_LLVM_JIT
    void (*observe_hot_block)(void*, detail::BasicBlock&) = nullptr;
    bool retain_region_context = false;
#endif
#ifdef ENABLE_TLB_STATS
    mem::TlbStats* stats = nullptr;
    mem::DispatchStats* dispatch_stats = nullptr;
#endif
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
    Replay,
    CachePressure
};

struct StepResult {
    Stop reason = Stop::Continue;
    Access access = Access::Read;
    uint16_t immediate = 0;
    uint64_t address = 0;
};

struct DecodedInstruction;

namespace detail {
struct DecodedOp;

// Encoding 31 selects SP only for operands that permit it.
template <unsigned reg>
inline uint64_t& PinSlot(State& state) {
    if constexpr (reg == 31)
        return state.sp;
    else
        return state.x[reg];
}

#define A64_PIN_PARAM(reg, slot) , [[maybe_unused]] uint64_t pin_##slot
#define A64_PIN_ARG(reg, slot) , pin_##slot
#define A64_PIN_INIT(reg, slot) , ::fiberish::aarch64::detail::PinSlot<reg>(state)
#define A64_PIN_REF(reg, slot) , uint64_t& pin_##slot
#define A64_HANDLER_PIN_PARAMS FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_PARAM)
#define A64_HANDLER_PIN_ARGS FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_ARG)
#define A64_HANDLER_PIN_INIT FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_INIT)
#define A64_HOT_PIN_REF FIBERCPU_AARCH64_FOR_EACH_PIN(A64_PIN_REF)

#ifndef FIBERCPU_AARCH64_MICRO_TLB
#define FIBERCPU_AARCH64_MICRO_TLB 0
#endif

#if FIBERCPU_AARCH64_MICRO_TLB
#define A64_UTLB_PARAMS , [[maybe_unused]] uint64_t utlb_tags, [[maybe_unused]] uintptr_t utlb_addend
#define A64_UTLB_REF_PARAMS , [[maybe_unused]] uint64_t &utlb_tags, [[maybe_unused]] uintptr_t &utlb_addend
#define A64_UTLB_ARGS , utlb_tags, utlb_addend
#define A64_UTLB_INIT , ::fiberish::mem::InvalidMicroTlbAbiTags(), uintptr_t{0}
#define A64_UTLB_INVALIDATE() (utlb_tags = ::fiberish::mem::InvalidMicroTlbAbiTags())
#else
#define A64_UTLB_PARAMS
#define A64_UTLB_REF_PARAMS
#define A64_UTLB_ARGS
#define A64_UTLB_INIT
#define A64_UTLB_INVALIDATE() ((void)0)
#endif

#define A64_HANDLER_PARAMS                                                                                    \
    State &state, const ::fiberish::aarch64::detail::DecodedOp *current, int64_t instr_limit A64_UTLB_PARAMS, \
        uint32_t flags_cache A64_HANDLER_PIN_PARAMS
#define A64_HANDLER_ARGS state, current, instr_limit A64_UTLB_ARGS, flags_cache A64_HANDLER_PIN_ARGS

using HandlerFunc = int64_t(FIBERCPU_AARCH64_HANDLER_ABI*)(A64_HANDLER_PARAMS);
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
    uint64_t immediate = 0;
    union {
        uint64_t mask = 0;
        uint64_t target_pc;
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
    HandlerFunc handler = nullptr;
    bool can_follow = false;
};
}  // namespace detail

struct Profile {
    void* context = nullptr;
    void (*record)(void*, uint64_t pc, uint32_t word, bool can_follow) = nullptr;
    detail::HandlerFunc (*resolve)(const DecodedInstruction&, bool omit_nzcv) = nullptr;
    // Fusion resolvers receive baking keys with PC-relative offsets cleared.
    detail::HandlerFunc (*resolve_pair)(uint32_t first, uint32_t second, uint32_t dead) = nullptr;
    detail::HandlerFunc (*resolve_sequence)(const uint32_t* words, unsigned count, unsigned* matched,
                                            uint32_t dead) = nullptr;
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
enum class BudgetMode { Exact, Block };

RunResult Run(State& state, const Memory& memory, uint64_t budget, const Profile& profile = {},
              BudgetMode budget_mode = BudgetMode::Exact);

}  // namespace fiberish::aarch64
