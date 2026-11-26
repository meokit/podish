#pragma once

#include <chrono>

#include "memory.h"

namespace fiberish::aarch64::detail {

enum class SystemOp {
    Hint,
    ClearExclusive,
    DataBarrier,
    Refetch,
    ReadThreadId,
    ReadThreadIdReadOnly,
    ReadNzcv,
    ReadFpcr,
    ReadFpsr,
    ReadCtr,
    ReadDczid,
    ReadCounterFrequency,
    ReadCounter,
    WriteThreadId,
    WriteNzcv,
    WriteFpcr,
    WriteFpsr,
    ZeroCacheLine,
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

template <SystemOp operation, class Registers>
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
        registers.Write(
            rd, 64,
            std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now().time_since_epoch())
                .count());
    else if constexpr (operation == SystemOp::WriteThreadId)
        state.tpidr_el0 = registers.Read(rd, 64);
    else if constexpr (operation == SystemOp::WriteNzcv)
        flags = uint32_t(registers.Read(rd, 64)) & FlagMask;
    else if constexpr (operation == SystemOp::WriteFpcr)
        state.fpcr = uint32_t(registers.Read(rd, 64)) & 0x07c00000u;
    else if constexpr (operation == SystemOp::WriteFpsr)
        state.fpsr = uint32_t(registers.Read(rd, 64)) & 0xf800009fu;
}

template <SystemOp operation>
inline StepResult SystemInstruction(State& state, const Memory& memory, const DecodedInstruction& op) {
    if constexpr (operation == SystemOp::Undefined) {
        return Undefined(state.pc);
    } else if constexpr (operation == SystemOp::ZeroCacheLine) {
        const std::array<uint8_t, ZeroBlockBytes> zero{};
        const auto address = ReadRegister(state, op.rd, 64) & ~uint64_t{ZeroBlockBytes - 1};
        if (!WriteMemory(state, memory, address, zero.data(), zero.size())) return Fault(address, Access::Write);
    } else {
        SystemRegisterOp<operation>(state, op.rd, state.nzcv, ScalarRegisters{state});
    }
    return {};
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
template <SystemOp operation>
FIBERCPU_AARCH64_HANDLER_ABI uint64_t SystemHandler(A64_HANDLER_PARAMS) {
    const auto rd = Field(uint32_t(current->Operands()), 0, 5);
    const HotRegisters registers{state A64_HANDLER_PIN_ARGS};
    if constexpr (operation == SystemOp::Refetch) {
        return ReturnFromBlock(state, current, instr_limit, flags_cache A64_HANDLER_PIN_ARGS, {Stop::Refetch});
    } else if constexpr (operation == SystemOp::ZeroCacheLine) {
        const auto address = registers.Read(rd, 64) & ~uint64_t{ZeroBlockBytes - 1};
        uint8_t* data = nullptr;
        if (!ProbeDataTlb<true>(state, address, ZeroBlockBytes, data_tlb, utlb_tags, utlb_addend, data)) [[unlikely]] {
            A64_MEMORY_OP_GENERIC(current);
        }
        std::memset(data, 0, ZeroBlockBytes);
        ClearOverlappingExclusive(state, address, ZeroBlockBytes);
    } else {
        SystemRegisterOp<operation>(state, rd, flags_cache, registers);
    }
    A64_DISPATCH_NEXT(current->NextOp<false>());
}
#endif

template <SystemOp operation>
constexpr HandlerBinding BindSystemHandler() {
    // Cache maintenance and callback-based DC ZVA must retain their refetch boundary.
    constexpr bool can_follow = operation != SystemOp::Refetch && operation != SystemOp::ZeroCacheLine;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    return {&SystemInstruction<operation>, &SystemHandler<operation>, can_follow};
#else
    return {&SystemInstruction<operation>, nullptr, can_follow};
#endif
}

inline HandlerBinding LowerSystem(uint32_t word) {
    switch (DecodeSystemOp(word)) {
        case SystemOp::Hint:
            return BindSystemHandler<SystemOp::Hint>();
        case SystemOp::ClearExclusive:
            return BindSystemHandler<SystemOp::ClearExclusive>();
        case SystemOp::DataBarrier:
            return BindSystemHandler<SystemOp::DataBarrier>();
        case SystemOp::Refetch:
            return BindSystemHandler<SystemOp::Refetch>();
        case SystemOp::ReadThreadId:
            return BindSystemHandler<SystemOp::ReadThreadId>();
        case SystemOp::ReadThreadIdReadOnly:
            return BindSystemHandler<SystemOp::ReadThreadIdReadOnly>();
        case SystemOp::ReadNzcv:
            return BindSystemHandler<SystemOp::ReadNzcv>();
        case SystemOp::ReadFpcr:
            return BindSystemHandler<SystemOp::ReadFpcr>();
        case SystemOp::ReadFpsr:
            return BindSystemHandler<SystemOp::ReadFpsr>();
        case SystemOp::ReadCtr:
            return BindSystemHandler<SystemOp::ReadCtr>();
        case SystemOp::ReadDczid:
            return BindSystemHandler<SystemOp::ReadDczid>();
        case SystemOp::ReadCounterFrequency:
            return BindSystemHandler<SystemOp::ReadCounterFrequency>();
        case SystemOp::ReadCounter:
            return BindSystemHandler<SystemOp::ReadCounter>();
        case SystemOp::WriteThreadId:
            return BindSystemHandler<SystemOp::WriteThreadId>();
        case SystemOp::WriteNzcv:
            return BindSystemHandler<SystemOp::WriteNzcv>();
        case SystemOp::WriteFpcr:
            return BindSystemHandler<SystemOp::WriteFpcr>();
        case SystemOp::WriteFpsr:
            return BindSystemHandler<SystemOp::WriteFpsr>();
        case SystemOp::ZeroCacheLine:
            return BindSystemHandler<SystemOp::ZeroCacheLine>();
        case SystemOp::Undefined:
            return {};
    }
}

}  // namespace fiberish::aarch64::detail
