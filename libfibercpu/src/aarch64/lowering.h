#pragma once

#include <chrono>
#include <utility>

#include "floating.h"
#include "memory.h"
#include "threading.h"
#include "vector.h"

namespace fiberish::aarch64::detail {

inline StepResult UndefinedInstruction(State& state, const Memory&, const DecodedInstruction&) {
    return Undefined(state.pc);
}

inline StepResult SyscallInstruction(State& state, const Memory&, const DecodedInstruction& op) {
    return {Stop::Syscall, Access::Execute, uint16_t(op.immediate), state.pc};
}

inline StepResult BreakpointInstruction(State& state, const Memory&, const DecodedInstruction& op) {
    return {Stop::Breakpoint, Access::Execute, uint16_t(op.immediate), state.pc};
}

inline StepResult SystemInstruction(State& state, const Memory& memory, const DecodedInstruction& op) {
    const auto word = op.word;
    const auto reg = word & 0xffffffe0;
    const auto barrier = word & 0xfffff0ff;

    if ((word & 0xfffff01f) == 0xd503201f) return {};  // HINT, including architectural NOP.
    switch (barrier) {
        case 0xd503305f:  // CLREX
            state.ClearExclusive();
            return {};
        case 0xd503309f:  // DSB
        case 0xd50330bf:  // DMB
            std::atomic_thread_fence(std::memory_order_seq_cst);
            return {};
        case 0xd50330df:  // ISB
            return {};
    }

    uint64_t result;
    switch (reg) {
        case 0xd53bd040:
            result = state.tpidr_el0;
            break;
        case 0xd53bd060:
            result = state.tpidrro_el0;
            break;
        case 0xd53b4200:
            result = state.nzcv;
            break;
        case 0xd53b4400:
            result = state.fpcr;
            break;
        case 0xd53b4420:
            result = state.fpsr;
            break;
        case 0xd53b0020:
            result = 0x8444c004;
            break;  // CTR_EL0, 64-byte I/D cache lines.
        case 0xd53b00e0:
            result = 3;
            break;  // DCZID_EL0, 32-byte DC ZVA block.
        case 0xd53be000:
            result = 1'000'000'000;
            break;        // CNTFRQ_EL0.
        case 0xd53be020:  // CNTPCT_EL0.
        case 0xd53be040:  // CNTVCT_EL0.
            result = std::chrono::duration_cast<std::chrono::nanoseconds>(
                         std::chrono::steady_clock::now().time_since_epoch())
                         .count();
            break;
        case 0xd51bd040:
            state.tpidr_el0 = ReadRegister(state, op.rd, 64);
            return {};
        case 0xd51b4200:
            state.nzcv = uint32_t(ReadRegister(state, op.rd, 64)) & FlagMask;
            return {};
        case 0xd51b4400:
            state.fpcr = uint32_t(ReadRegister(state, op.rd, 64)) & 0x07c00000u;
            return {};
        case 0xd51b4420:
            state.fpsr = uint32_t(ReadRegister(state, op.rd, 64)) & 0xf800009fu;
            return {};
        case 0xd50b7420: {  // DC ZVA
            std::array<uint8_t, 32> zero{};
            const auto address = ReadRegister(state, op.rd, 64) & ~uint64_t{zero.size() - 1};
            if (!WriteMemory(state, memory, address, zero.data(), zero.size())) return Fault(address, Access::Write);
            return {};
        }
        case 0xd50b7520:  // IC IVAU
        case 0xd50b7a20:  // DC CVAC
        case 0xd50b7b20:  // DC CVAU
        case 0xd50b7e20:  // DC CIVAC
            return {};    // Decode entries depend on current instruction bytes.
        default:
            return Undefined(state.pc);
    }
    WriteRegister(state, op.rd, 64, result);
    return {};
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <Kind kind, unsigned width, int operation, int mode, int shift, int vector>
FIBERCPU_AARCH64_THREAD_ABI uint64_t DispatchMemory(A64_THREAD_PARAMS) {
    if (!ProbeLoadStore<kind, width, operation, mode, shift, vector>(
            state, current->op, BlockTarget<kind>(current->op), data_tlb, tlb_tags, tlb_addend A64_THREAD_PIN_PASS))
        [[unlikely]] {
        A64_THREAD_REPLAY(current);
    }
    A64_THREAD_NEXT(1);
}
#endif

template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
constexpr HandlerBinding BindMemoryHandler() {
    constexpr auto scalar = LoadStore<kind, width, operation, mode, shift, vector>;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {scalar, &DispatchMemory<kind, width, operation, mode, shift, vector>};
#else
    return {scalar, nullptr};
#endif
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <Kind kind, unsigned width, int operation, int shift, bool write_nzcv>
FIBERCPU_AARCH64_THREAD_ABI uint64_t IntegerThread(A64_THREAD_PARAMS) {
    const auto result = HotInteger<kind, width, operation, shift, write_nzcv>(
        state, current->op, current->pc, BlockTarget<kind>(current->op), nzcv A64_THREAD_PIN_PASS);
    if constexpr (kind >= Kind::BranchImmediate && kind <= Kind::TestBranch) {
        if (result.reason == Stop::Branch) {
            if constexpr (kind == Kind::BranchRegister) {
                A64_THREAD_BRANCH(result.address);
            } else {
                A64_THREAD_DIRECT();
            }
        }
    }
    A64_THREAD_NEXT(1);
}
#endif

template <Kind kind, unsigned width = 0, int operation = -1, int shift = -1, bool write_nzcv = true>
constexpr HandlerBinding BindIntegerKernel() {
    constexpr auto scalar = Integer<kind, width, operation, shift, write_nzcv>;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {scalar, &IntegerThread<kind, width, operation, shift, write_nzcv>};
#else
    return {scalar, nullptr};
#endif
}

constexpr std::array<HandlerBinding, size_t(Kind::Count)> Handlers{
    BindHandler<Kind::Undefined, UndefinedInstruction>(),
    BindIntegerKernel<Kind::Address>(),
    BindIntegerKernel<Kind::AddSubImmediate>(),
    BindIntegerKernel<Kind::AddSubShifted>(),
    BindIntegerKernel<Kind::AddSubExtended>(),
    BindIntegerKernel<Kind::AddSubCarry>(),
    BindIntegerKernel<Kind::LogicalImmediate>(),
    BindIntegerKernel<Kind::LogicalShifted>(),
    BindIntegerKernel<Kind::MoveWide>(),
    BindIntegerKernel<Kind::Bitfield>(),
    BindIntegerKernel<Kind::Extract>(),
    BindIntegerKernel<Kind::ConditionalCompare>(),
    BindIntegerKernel<Kind::ConditionalSelect>(),
    BindIntegerKernel<Kind::DataOneSource>(),
    BindIntegerKernel<Kind::DataTwoSource>(),
    BindIntegerKernel<Kind::Multiply>(),
    BindIntegerKernel<Kind::BranchImmediate>(),
    BindIntegerKernel<Kind::BranchRegister>(),
    BindIntegerKernel<Kind::BranchCondition>(),
    BindIntegerKernel<Kind::CompareBranch>(),
    BindIntegerKernel<Kind::TestBranch>(),
    BindMemoryHandler<Kind::LoadStoreImmediate>(),
    BindMemoryHandler<Kind::LoadStoreRegister>(),
    BindMemoryHandler<Kind::LoadStorePair, 32>(),
    BindMemoryHandler<Kind::LoadLiteral>(),
    BindHandler<Kind::LoadStoreExclusive, ExclusiveMemory>(),
    BindHandler<Kind::LoadStoreAtomic, AtomicMemory>(),
    BindHandler<Kind::Syscall, SyscallInstruction>(),
    BindHandler<Kind::Breakpoint, BreakpointInstruction>(),
    BindHandler<Kind::System, SystemInstruction>(),
    BindHandler<Kind::ScalarFloat, UndefinedInstruction>(),
    BindHandler<Kind::Vector, UndefinedInstruction>(),
};

template <Kind kind, unsigned operations, int shift, bool write_nzcv, size_t... index>
constexpr auto IntegerVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindIntegerKernel < kind, index<operations ? 32 : 64, index % operations, shift, write_nzcv>()...};
}

template <Kind kind, unsigned operations = 4, int shift = -1, bool write_nzcv = true>
HandlerBinding BindInteger(const DecodedInstruction& op) {
    constexpr auto variants =
        IntegerVariants<kind, operations, shift, write_nzcv>(std::make_index_sequence<2 * operations>{});
    return variants[(op.width == 64 ? operations : 0) + op.operation];
}

template <size_t... index>
constexpr auto ImmediateMemoryVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreImmediate, 8u << (index / 4), index % 4, Offset, -1, 0>()...};
}

template <size_t... index>
constexpr auto RegisterMemoryVariants(std::index_sequence<index...>) {
    return std::array<HandlerBinding, sizeof...(index)>{
        BindMemoryHandler<Kind::LoadStoreRegister, 8u << (index / 16), index % 4, 2 + (index / 4) % 2,
                          (index / 8) % 2 ? index / 16 : 0, 0>()...};
}

inline HandlerBinding Lower(const DecodedInstruction& op) {
    switch (op.kind) {
        case Kind::ScalarFloat:
            if (auto handler = LowerFloat(op.word)) return {handler, nullptr};
            break;
        case Kind::Vector:
            if (auto handler = LowerVector(op.word)) return {handler, nullptr};
            break;
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted>(op) : BindInteger<Kind::AddSubShifted, 4, 0>(op);
        case Kind::AddSubExtended:
            return BindInteger<Kind::AddSubExtended>(op);
        case Kind::AddSubCarry:
            return BindInteger<Kind::AddSubCarry>(op);
        case Kind::LogicalImmediate:
            return BindInteger<Kind::LogicalImmediate>(op);
        case Kind::LogicalShifted:
            return op.shift ? BindInteger<Kind::LogicalShifted, 8>(op) : BindInteger<Kind::LogicalShifted, 8, 0>(op);
        case Kind::MoveWide:
            return BindInteger<Kind::MoveWide>(op);
        case Kind::ConditionalSelect:
            return BindInteger<Kind::ConditionalSelect>(op);
        case Kind::CompareBranch:
            return BindInteger<Kind::CompareBranch, 2>(op);
        case Kind::LoadStoreImmediate:
            if (!Field(op.word, 26, 1) && op.operation < Prefetch && op.mode == Offset) {
                constexpr auto variants = ImmediateMemoryVariants(std::make_index_sequence<16>{});
                return variants[4 * Field(op.word, 30, 2) + op.operation];
            }
            break;
        case Kind::LoadStorePair:
            // Both constituents have the same decoded width. A fixed transfer
            // size lets the compiler keep the pair in registers on a TLB hit.
            if (op.width == 64) return BindMemoryHandler<Kind::LoadStorePair, 64>();
            if (op.width == 128) return BindMemoryHandler<Kind::LoadStorePair, 128>();
            break;
        case Kind::LoadStoreRegister:
            if (!Field(op.word, 26, 1) && op.operation < Prefetch && (op.mode == 2 || op.mode == 3)) {
                constexpr auto variants = RegisterMemoryVariants(std::make_index_sequence<64>{});
                return variants[16 * Field(op.word, 30, 2) + 8 * Field(op.word, 12, 1) + 4 * (op.mode - 2) +
                                op.operation];
            }
            break;
        default:
            break;
    }
    return Handlers[size_t(op.kind)];
}

inline HandlerBinding LowerDeadNZCV(const DecodedInstruction& op) {
    if (!WritesIntegerNZCV(op)) return {};
    switch (op.kind) {
        case Kind::AddSubImmediate:
            return BindInteger<Kind::AddSubImmediate, 4, -1, false>(op);
        case Kind::AddSubShifted:
            return op.shift ? BindInteger<Kind::AddSubShifted, 4, -1, false>(op)
                            : BindInteger<Kind::AddSubShifted, 4, 0, false>(op);
        case Kind::AddSubExtended:
            return BindInteger<Kind::AddSubExtended, 4, -1, false>(op);
        case Kind::AddSubCarry:
            return BindInteger<Kind::AddSubCarry, 4, -1, false>(op);
        case Kind::LogicalImmediate:
            return BindInteger<Kind::LogicalImmediate, 4, -1, false>(op);
        case Kind::LogicalShifted:
            return op.shift ? BindInteger<Kind::LogicalShifted, 8, -1, false>(op)
                            : BindInteger<Kind::LogicalShifted, 8, 0, false>(op);
        case Kind::ConditionalCompare:
            return BindIntegerKernel<Kind::ConditionalCompare, 0, -1, -1, false>();
        default:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
