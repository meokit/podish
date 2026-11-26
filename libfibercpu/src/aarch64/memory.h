#pragma once

#include <algorithm>
#include <atomic>
#include <cstring>

#include "../mem/tlb.h"
#include "dispatch.h"
#include "integer.h"

namespace fiberish::aarch64::detail {

#if defined(_MSC_VER)
#define A64_MEMORY_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define A64_MEMORY_INLINE inline __attribute__((always_inline))
#else
#define A64_MEMORY_INLINE inline
#endif

template <class T>
inline T ReadLittle(const void* data) {
    T value;
    std::memcpy(&value, data, sizeof(value));
    if constexpr (std::endian::native == std::endian::big) value = std::byteswap(value);
    return value;
}

template <class T>
inline void WriteLittle(void* data, T value) {
    if constexpr (std::endian::native == std::endian::big) value = std::byteswap(value);
    std::memcpy(data, &value, sizeof(value));
}

constexpr StepResult Fault(uint64_t address, Access access) { return {Stop::Fault, access, 0, address}; }

constexpr StepResult Alignment(uint64_t address, Access access) { return {Stop::Alignment, access, 0, address}; }

constexpr StepResult Undefined(uint64_t pc) { return {Stop::Undefined, Access::Execute, 0, pc}; }

inline bool ReadMemory(const Memory& memory, uint64_t address, void* data, size_t bytes, Access access = Access::Read) {
    return mem::ArmAddressRangeValid(address, bytes) && memory.read &&
           memory.read(memory.context, address, data, bytes, access);
}

inline void ClearOverlappingExclusive(State& state, uint64_t address, size_t bytes) {
    if (state.exclusive_size &&
        (address - state.exclusive_address < state.exclusive_size || state.exclusive_address - address < bytes))
        state.ClearExclusive();
}

inline bool WriteMemory(State& state, const Memory& memory, uint64_t address, const void* data, size_t bytes) {
    if (!mem::ArmAddressRangeValid(address, bytes) || !memory.write ||
        !memory.write(memory.context, address, data, bytes))
        return false;
    ClearOverlappingExclusive(state, address, bytes);
    return true;
}

inline uint64_t ReadElement(const uint8_t* data, unsigned width) {
    switch (width) {
        case 8:
            return *data;
        case 16:
            return ReadLittle<uint16_t>(data);
        case 32:
            return ReadLittle<uint32_t>(data);
        default:
            return ReadLittle<uint64_t>(data);
    }
}

inline void WriteElement(uint8_t* data, unsigned width, uint64_t value) {
    switch (width) {
        case 8:
            *data = uint8_t(value);
            break;
        case 16:
            WriteLittle(data, uint16_t(value));
            break;
        case 32:
            WriteLittle(data, uint32_t(value));
            break;
        default:
            WriteLittle(data, value);
            break;
    }
}

template <bool hot>
A64_MEMORY_INLINE uint64_t ReadTransferRegister(const State& state, unsigned reg, bool use_sp,
                                                [[maybe_unused]] uint64_t* pinned) {
#if defined(FIBERCPU_AARCH64_PIN_REG) && FIBERCPU_AARCH64_PIN_REG >= 0
    if constexpr (hot) return ReadHotRegister(state, reg, 64, use_sp, *pinned);
#endif
    return ReadRegister(state, reg, 64, use_sp);
}

template <bool hot>
A64_MEMORY_INLINE void WriteTransferRegister(State& state, unsigned reg, unsigned width, uint64_t value, bool use_sp,
                                             [[maybe_unused]] uint64_t* pinned) {
#if defined(FIBERCPU_AARCH64_PIN_REG) && FIBERCPU_AARCH64_PIN_REG >= 0
    if constexpr (hot) {
        WriteHotRegister(state, reg, width, value, use_sp, *pinned);
        return;
    }
#endif
    WriteRegister(state, reg, width, value, use_sp);
}

template <bool hot = false>
A64_MEMORY_INLINE void StoreRegister(const State& state, uint8_t* data, unsigned reg, unsigned width, bool vector,
                                     uint64_t* pinned = nullptr) {
    if (vector) {
        WriteElement(data, width, state.v[reg].lo);
        if (width == 128) WriteLittle(data + 8, state.v[reg].hi);
    } else {
        WriteElement(data, width, ReadTransferRegister<hot>(state, reg, false, pinned));
    }
}

template <bool write>
A64_MEMORY_INLINE bool ProbeDataTlb([[maybe_unused]] const State& state, uint64_t address, unsigned size,
                                    const mem::SoftTlb* data_tlb, uint64_t& utlb_tags, uintptr_t& utlb_addend,
                                    uint8_t*& data) {
#ifdef ENABLE_TLB_STATS
    auto* stats = state.active_run ? state.active_run->memory.stats : nullptr;
    if (stats) ++(write ? stats->total_writes : stats->total_reads);
#endif
    if (address < mem::ArmTaskSize && (address & PageMask) <= PageBytes - size) [[likely]] {
        const auto tag = mem::ArmPageTag(address);
        const auto cached = write ? uint32_t(utlb_tags >> 32) : uint32_t(utlb_tags);
        if (cached == tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            if (stats) ++(write ? stats->l1_write_hits : stats->l1_read_hits);
#endif
            data = reinterpret_cast<uint8_t*>(utlb_addend + static_cast<uintptr_t>(address));
            return true;
        }
        const auto index = tag & mem::TLB_INDEX_MASK;
        const auto entry = (write ? data_tlb->write_tlb : data_tlb->read_tlb)[index];
        if ((write ? entry.WriteTag() : entry.ReadTag()) == tag) [[likely]] {
#ifdef ENABLE_TLB_STATS
            if (stats) ++(write ? stats->l2_write_hits : stats->l2_read_hits);
#endif
            utlb_tags = entry.tags;
            utlb_addend = entry.addend;
            data = reinterpret_cast<uint8_t*>(entry.addend + static_cast<uintptr_t>(address));
            return true;
        }
    }
#ifdef ENABLE_TLB_STATS
    if (stats) ++(write ? stats->write_misses : stats->read_misses);
#endif
    return false;
}

template <bool hot = false>
A64_MEMORY_INLINE void LoadRegister(State& state, const uint8_t* data, unsigned reg, unsigned width, bool vector,
                                    unsigned operation, uint64_t* pinned = nullptr) {
    auto value = ReadElement(data, width);
    if (vector) {
        state.v[reg] = {value, width == 128 ? ReadLittle<uint64_t>(data + 8) : 0};
    } else {
        if (operation == LoadSigned64 || operation == LoadSigned32) value = SignExtend(value, width);
        WriteTransferRegister<hot>(state, reg, operation == LoadSigned32 ? 32 : 64, value, false, pinned);
    }
}

template <Kind kind, unsigned fixed_width, int fixed_operation, int fixed_mode, int fixed_shift, int fixed_vector,
          bool probe_only>
A64_MEMORY_INLINE StepResult LoadStoreTransfer(State& state, const DecodedInstruction& op, uint64_t pc,
                                               uint64_t literal_address, const mem::SoftTlb* data_tlb,
                                               uint64_t& utlb_tags, uintptr_t& utlb_addend, const Memory* memory,
                                               uint64_t* pinned) {
    const unsigned width = fixed_width == 0 ? op.width : fixed_width;
    const unsigned operation = fixed_operation < 0 ? op.operation : fixed_operation;
    const unsigned mode = fixed_mode < 0 ? op.mode : fixed_mode;
    const unsigned shift = fixed_shift < 0 ? op.shift : fixed_shift;
    constexpr bool pair = kind == Kind::LoadStorePair;
    constexpr bool literal = kind == Kind::LoadLiteral;
    const bool vector = fixed_vector < 0 ? Field(op.word, 26, 1) != 0 : fixed_vector != 0;
    const auto bytes = width / 8;
    const auto access = operation == Store ? Access::Write : Access::Read;
    uint64_t address;
    uint64_t writeback = 0;
    bool update_base = false;

    if constexpr (literal) {
        address = literal_address;
    } else {
        const auto base = ReadTransferRegister<probe_only>(state, op.rn, true, pinned);
        if (op.rn == 31 && (base & 15)) return Alignment(base, access);
        if constexpr (kind == Kind::LoadStoreRegister) {
            const auto index = ReadTransferRegister<probe_only>(state, op.rm, false, pinned);
            address = base + Extend(index, mode, shift, 64);
        } else {
            update_base = mode == PostIndex || mode == PreIndex;
            // An overlapping writeback with a general-purpose destination/source is
            // CONSTRAINED UNPREDICTABLE. Signal an undefined instruction.
            if (update_base && !vector && op.rn != 31 && (op.rn == op.rd || (pair && op.rn == op.ra)))
                return Undefined(pc);
            writeback = base + op.immediate;
            address = mode == PostIndex ? base : writeback;
        }
    }
    if (operation == Prefetch) return {};

    const auto size = bytes * (pair ? 2 : 1);
    if constexpr (pair) {
        if (operation != Store && !vector && op.rd == op.ra) return Undefined(pc);
    }
    bool refetch = false;
    std::array<uint8_t, pair ? 32 : 16> storage;
    auto* data = storage.data();
    if constexpr (probe_only) {
        const bool hit = operation == Store
                             ? ProbeDataTlb<true>(state, address, size, data_tlb, utlb_tags, utlb_addend, data)
                             : ProbeDataTlb<false>(state, address, size, data_tlb, utlb_tags, utlb_addend, data);
        if (!hit) return {Stop::Replay};
    }
    if (operation == Store) {
#if defined(__has_builtin)
#if __has_builtin(__builtin_assume_separate_storage)
        if constexpr (probe_only) __builtin_assume_separate_storage(&state, data);
#endif
#endif
        StoreRegister<probe_only>(state, data, op.rd, width, vector, pinned);
        if constexpr (pair) StoreRegister<probe_only>(state, data + bytes, op.ra, width, vector, pinned);
        if constexpr (!probe_only) {
            refetch = !mem::ArmAddressRangeValid(address, size) || !memory->write_fast ||
                      !memory->write_fast(memory->context, address, data, size);
            if (refetch && !WriteMemory(state, *memory, address, data, size)) return Fault(address, access);
        }
        ClearOverlappingExclusive(state, address, size);
    } else {
        if constexpr (!probe_only) {
            const bool hit = memory->data_tlb && !memory->lookup_cached_block &&
                             ProbeDataTlb<false>(state, address, size, memory->data_tlb, utlb_tags, utlb_addend, data);
            if (!hit) {
                refetch = !mem::ArmAddressRangeValid(address, size) || !memory->read_fast ||
                          !memory->read_fast(memory->context, address, data, size);
                if (refetch && !ReadMemory(*memory, address, data, size)) return Fault(address, access);
            }
        }
        LoadRegister<probe_only>(state, data, op.rd, width, vector, operation, pinned);
        if constexpr (pair) LoadRegister<probe_only>(state, data + bytes, op.ra, width, vector, operation, pinned);
    }
    if (update_base) WriteTransferRegister<probe_only>(state, op.rn, 64, writeback, true, pinned);
    return refetch ? StepResult{Stop::Refetch} : StepResult{};
}

template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
A64_MEMORY_INLINE StepResult LoadStore(State& state, const Memory& memory, const DecodedInstruction& op) {
    const auto literal_address = kind == Kind::LoadLiteral ? state.pc + op.immediate : 0;
    uint64_t tags = mem::InvalidMicroTlbAbiTags();
    uintptr_t addend = 0;
    return LoadStoreTransfer<kind, width, operation, mode, shift, vector, false>(
        state, op, state.pc, literal_address, nullptr, tags, addend, &memory, nullptr);
}

// Only a TLB hit can change guest state. A miss replays the current member.
template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
A64_MEMORY_INLINE bool ProbeLoadStore(State& state, const DecodedInstruction& op, uint64_t literal_address,
                                      const mem::SoftTlb* data_tlb, uint64_t& utlb_tags,
                                      uintptr_t& utlb_addend A64_HOT_PIN_REF) {
    uint64_t* pin = nullptr;
#if defined(FIBERCPU_AARCH64_PIN_REG) && FIBERCPU_AARCH64_PIN_REG >= 0
    pin = &pinned;
#endif
    return LoadStoreTransfer<kind, width, operation, mode, shift, vector, true>(state, op, 0, literal_address, data_tlb,
                                                                                utlb_tags, utlb_addend, nullptr, pin)
               .reason == Stop::Continue;
}

#undef A64_MEMORY_INLINE

inline void AcquireFence() { std::atomic_thread_fence(std::memory_order_acquire); }
inline void ReleaseFence() { std::atomic_thread_fence(std::memory_order_release); }

struct CallbackTransfers {
    State& state;
    const Memory& memory;

    bool Read(uint64_t address, void* dest, unsigned size) const { return ReadMemory(memory, address, dest, size); }

    bool Write(uint64_t address, const void* src, unsigned size) const {
        return WriteMemory(state, memory, address, src, size);
    }
};

template <class Registers, class Transfers>
inline StepResult ExclusiveMemoryOp(State& state, const DecodedInstruction& op, uint64_t address,
                                    const Registers& registers, const Transfers& memory) {
    const bool cas = op.mode == CompareSwap || op.mode == CompareSwapPair;
    const bool pair = op.mode == ExclusivePair || op.mode == CompareSwapPair;
    const auto bytes = op.width / 8;
    const unsigned total = bytes * (pair ? 2 : 1);
    const auto access = cas || op.operation == Store ? Access::Write : Access::Read;
    if ((op.rn == 31 && (address & 15)) || (address & (total - 1))) return Alignment(address, access);

    std::array<uint8_t, 16> data;
    if (cas) {
        if (Field(op.word, 15, 1)) ReleaseFence();
        if (!memory.Read(address, data.data(), total)) return Fault(address, access);
        const auto expected = registers.Read(op.rm, op.width);
        const auto expected2 = pair ? registers.Read(op.rm + 1, op.width) : 0;
        const auto actual = ReadElement(data.data(), op.width);
        const auto actual2 = pair ? ReadElement(data.data() + bytes, op.width) : 0;
        if (expected == actual && expected2 == actual2) {
            WriteElement(data.data(), op.width, registers.Read(op.rd, op.width));
            if (pair) WriteElement(data.data() + bytes, op.width, registers.Read(op.rd + 1, op.width));
            if (!memory.Write(address, data.data(), total)) return Fault(address, access);
        }
        registers.Write(op.rm, 64, actual);
        if (pair) registers.Write(op.rm + 1, 64, actual2);
        if (Field(op.word, 22, 1)) AcquireFence();
        return {};
    }

    const bool exclusive = op.mode == Exclusive || op.mode == ExclusivePair;
    const bool ordered = Field(op.word, 15, 1) || op.mode == Acquire;
    if (op.operation == Load) {
        if (pair && op.rd == op.ra) return Undefined(state.pc);
        if (!memory.Read(address, data.data(), total)) return Fault(address, access);
        if (exclusive) {
            state.exclusive_address = address;
            state.exclusive_size = total;
            std::copy_n(data.data(), total, state.exclusive_value.data());
        }
        if (ordered) AcquireFence();
        registers.Write(op.rd, 64, ReadElement(data.data(), op.width));
        if (pair) registers.Write(op.ra, 64, ReadElement(data.data() + bytes, op.width));
        return {};
    }

    if (exclusive) {
        if ((op.rm != 31 && (op.rm == op.rd || (pair && op.rm == op.ra))) || (op.rn != 31 && op.rm == op.rn))
            return Undefined(state.pc);
        const bool match = state.exclusive_size == total && state.exclusive_address == address;
        state.ClearExclusive();
        if (!match) {
            registers.Write(op.rm, 32, 1);
            return {};
        }
        if (!memory.Read(address, data.data(), total)) return Fault(address, access);
        if (!std::equal(data.data(), data.data() + total, state.exclusive_value.data())) {
            registers.Write(op.rm, 32, 1);
            return {};
        }
    }
    if (ordered) ReleaseFence();
    WriteElement(data.data(), op.width, registers.Read(op.rd, op.width));
    if (pair) WriteElement(data.data() + bytes, op.width, registers.Read(op.ra, op.width));
    if (!memory.Write(address, data.data(), total)) return Fault(address, access);
    if (exclusive) registers.Write(op.rm, 32, 0);
    return {};
}

template <class Registers, class Transfers>
inline StepResult AtomicMemoryOp(const DecodedInstruction& op, uint64_t address, const Registers& registers,
                                 const Transfers& memory) {
    const unsigned bytes = op.width / 8;
    if ((op.rn == 31 && (address & 15)) || (address & (bytes - 1))) return Alignment(address, Access::Write);
    if (Field(op.word, 22, 1)) ReleaseFence();
    std::array<uint8_t, 8> data;
    if (!memory.Read(address, data.data(), bytes)) return Fault(address, Access::Write);
    const auto previous = ReadElement(data.data(), op.width);
    const auto operand = registers.Read(op.rm, op.width);
    uint64_t result;
    switch (op.operation) {
        case 0:
            result = previous + operand;
            break;
        case 1:
            result = previous & ~operand;
            break;
        case 2:
            result = previous ^ operand;
            break;
        case 3:
            result = previous | operand;
            break;
        case 4:
            result = Signed(previous, op.width) > Signed(operand, op.width) ? previous : operand;
            break;
        case 5:
            result = Signed(previous, op.width) < Signed(operand, op.width) ? previous : operand;
            break;
        case 6:
            result = std::max(previous, operand);
            break;
        case 7:
            result = std::min(previous, operand);
            break;
        default:
            result = operand;
            break;
    }
    WriteElement(data.data(), op.width, result);
    if (!memory.Write(address, data.data(), bytes)) return Fault(address, Access::Write);
    if (Field(op.word, 23, 1) && op.rd != 31) AcquireFence();
    registers.Write(op.rd, 64, previous);
    return {};
}

inline StepResult ExclusiveMemory(State& state, const Memory& memory, const DecodedInstruction& op) {
    const ScalarRegisters registers{state};
    return ExclusiveMemoryOp(state, op, registers.Read(op.rn, 64, true), registers, CallbackTransfers{state, memory});
}

inline StepResult AtomicMemory(State& state, const Memory& memory, const DecodedInstruction& op) {
    const ScalarRegisters registers{state};
    return AtomicMemoryOp(op, registers.Read(op.rn, 64, true), registers, CallbackTransfers{state, memory});
}

}  // namespace fiberish::aarch64::detail
