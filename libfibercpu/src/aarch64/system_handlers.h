#pragma once

#include <chrono>

#include "memory.h"

namespace fiberish::aarch64::detail {

enum class SystemOp {
#define ARM_SYSTEM(Name) Name,
#include "system_operations.def"
#undef ARM_SYSTEM
    Undefined,
};

inline SystemOp DecodeSystemOp(uint32_t word) {
    if ((word & 0xfffff01f) == 0xd503201f) return SystemOp::Hint;
    switch (word & 0xfffff0ff) {
        case 0xd503305f:
            return SystemOp::ClearExclusive;
        case 0xd503309f:  // DSB
        case 0xd50330bf:
            return SystemOp::DataBarrier;  // DMB
        case 0xd50330df:
            return SystemOp::Refetch;  // ISB
    }
    switch (word & 0xffffffe0) {
        case 0xd53bd040:
            return SystemOp::ReadThreadId;
        case 0xd53bd060:
            return SystemOp::ReadThreadIdReadOnly;
        case 0xd53b4200:
            return SystemOp::ReadNzcv;
        case 0xd53b4400:
            return SystemOp::ReadFpcr;
        case 0xd53b4420:
            return SystemOp::ReadFpsr;
        case 0xd53b0020:
            return SystemOp::ReadCtr;
        case 0xd53b00e0:
            return SystemOp::ReadDczid;
        case 0xd53be000:
            return SystemOp::ReadCounterFrequency;
        case 0xd53be020:  // CNTPCT_EL0
        case 0xd53be040:
            return SystemOp::ReadCounter;  // CNTVCT_EL0
        case 0xd51bd040:
            return SystemOp::WriteThreadId;
        case 0xd51b4200:
            return SystemOp::WriteNzcv;
        case 0xd51b4400:
            return SystemOp::WriteFpcr;
        case 0xd51b4420:
            return SystemOp::WriteFpsr;
        case 0xd50b7420:
            return SystemOp::ZeroCacheLine;  // DC ZVA
        case 0xd50b7520:                     // IC IVAU
        case 0xd50b7a20:                     // DC CVAC
        case 0xd50b7b20:                     // DC CVAU
        case 0xd50b7e20:
            return SystemOp::Refetch;  // DC CIVAC
        default:
            return SystemOp::Undefined;
    }
}

constexpr unsigned ZeroBlockBytes = 32;

inline uint64_t ReadCounterValue() noexcept {
    return std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
        .count();
}

template <SystemOp operation, class Registers, auto read_counter = ReadCounterValue>
inline void SystemRegisterOp(State& state, unsigned rd, uint32_t& flags, const Registers& registers) {
    if constexpr (operation == SystemOp::ClearExclusive)
        state.ClearExclusive();
    else if constexpr (operation == SystemOp::DataBarrier)
        std::atomic_thread_fence(std::memory_order_seq_cst);
    else if constexpr (operation == SystemOp::ReadThreadId)
        registers.Write(rd, 64, state.tpidr_el0);
    else if constexpr (operation == SystemOp::ReadThreadIdReadOnly)
        registers.Write(rd, 64, state.tpidrro_el0);
    else if constexpr (operation == SystemOp::ReadNzcv)
        registers.Write(rd, 64, flags);
    else if constexpr (operation == SystemOp::ReadFpcr)
        registers.Write(rd, 64, state.fpcr);
    else if constexpr (operation == SystemOp::ReadFpsr)
        registers.Write(rd, 64, state.fpsr);
    else if constexpr (operation == SystemOp::ReadCtr)
        registers.Write(rd, 64, 0x8444c004);  // 64-byte I/D cache lines.
    else if constexpr (operation == SystemOp::ReadDczid)
        registers.Write(rd, 64, 3);  // 32-byte DC ZVA block.
    else if constexpr (operation == SystemOp::ReadCounterFrequency)
        registers.Write(rd, 64, 1'000'000'000);
    else if constexpr (operation == SystemOp::ReadCounter)
        registers.Write(rd, 64, read_counter());
    else if constexpr (operation == SystemOp::WriteThreadId)
        state.tpidr_el0 = registers.Read(rd, 64);
    else if constexpr (operation == SystemOp::WriteNzcv)
        flags = uint32_t(registers.Read(rd, 64)) & FlagMask;
    else if constexpr (operation == SystemOp::WriteFpcr)
        state.fpcr = uint32_t(registers.Read(rd, 64)) & 0x07c00000u;
    else if constexpr (operation == SystemOp::WriteFpsr)
        state.fpsr = uint32_t(registers.Read(rd, 64)) & 0xf800009fu;
}

inline StepResult ZeroCacheLineSlow(State& state, const Memory& memory, const DecodedInstruction& op) {
    const std::array<uint8_t, ZeroBlockBytes> zero{};
    const auto address = ReadRegister(state, op.rd, 64) & ~uint64_t{ZeroBlockBytes - 1};
    if (!WriteMemory(state, memory, address, zero.data(), zero.size())) return Fault(address, Access::Write);
    return {};
}

A64_FORCE_INLINE bool ProbeZeroCacheLine(State& state, unsigned rd, const HotRegisters& registers,
                                         const mem::SoftTlb* tlb A64_UTLB_REF_PARAMS) {
    const auto address = registers.Read(rd, 64) & ~uint64_t{ZeroBlockBytes - 1};
    uint8_t* data = nullptr;
    if (!ProbeDataTlb<true>(state, address, ZeroBlockBytes, tlb A64_UTLB_ARGS, data)) return false;
    std::memset(data, 0, ZeroBlockBytes);
    ClearOverlappingExclusive(state, address, ZeroBlockBytes);
    return true;
}

template <SystemOp operation>
A64_FORCE_INLINE StepResult SystemLogic(State& state, const DecodedOp*& current A64_UTLB_REF_PARAMS,
                                        uint32_t& flags_cache A64_HOT_PIN_REF) {
    const auto rd = Field(uint32_t(current->Operands()), 0, 5);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (operation == SystemOp::Refetch) {
        return {Stop::Refetch};
    } else if constexpr (operation == SystemOp::ZeroCacheLine) {
        return ProbeZeroCacheLine(state, rd, registers, state.data_tlb A64_UTLB_ARGS) ? StepResult{}
                                                                                      : StepResult{Stop::Replay};
    } else {
        SystemRegisterOp<operation>(state, rd, flags_cache, registers);
        return {};
    }
}

template <SystemOp operation>
constexpr HandlerBinding BindSystemHandler() {
    // Cache maintenance and callback-based DC ZVA must retain their refetch boundary.
    constexpr bool can_follow = operation != SystemOp::Refetch && operation != SystemOp::ZeroCacheLine;
    constexpr DispatchPolicy policy{
        .wide = false,
        .may_replay = operation == SystemOp::ZeroCacheLine,
        .may_exit = operation == SystemOp::Refetch,
    };
    return {&DispatchWrapper<SystemLogic<operation>, policy>, can_follow};
}

inline HandlerBinding LowerSystem(uint32_t word) {
    switch (DecodeSystemOp(word)) {
#define ARM_SYSTEM(Name) \
    case SystemOp::Name: \
        return BindSystemHandler<SystemOp::Name>();
#include "system_operations.def"
#undef ARM_SYSTEM
        case SystemOp::Undefined:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
