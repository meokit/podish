#pragma once

#include <algorithm>
#include <atomic>
#include <cstring>

#include "../mem/tlb.h"
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
    return address <= UINT64_MAX - (bytes - 1) && memory.read &&
           memory.read(memory.context, address, data, bytes, access);
}

inline void ClearOverlappingExclusive(State& state, uint64_t address, size_t bytes) {
    if (state.exclusive_size &&
        (address - state.exclusive_address < state.exclusive_size || state.exclusive_address - address < bytes))
        state.ClearExclusive();
}

inline bool WriteMemory(State& state, const Memory& memory, uint64_t address, const void* data, size_t bytes) {
    if (address > UINT64_MAX - (bytes - 1) || !memory.write || !memory.write(memory.context, address, data, bytes))
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

inline void StoreRegister(const State& state, uint8_t* data, unsigned reg, unsigned width, bool vector) {
    if (vector) {
        WriteElement(data, width, state.v[reg].lo);
        if (width == 128) WriteLittle(data + 8, state.v[reg].hi);
    } else {
        WriteElement(data, width, ReadRegister(state, reg, width));
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
                                               uint64_t literal_address, const mem::MicroTLB* read_tlb,
                                               const Memory* memory, uint64_t* pinned) {
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

    std::array<uint8_t, pair ? 32 : 16> data;
    const auto size = bytes * (pair ? 2 : 1);
    bool refetch = false;
    if (operation == Store) {
        if constexpr (probe_only) {
            return {Stop::Replay};
        } else {
            StoreRegister(state, data.data(), op.rd, width, vector);
            if constexpr (pair) StoreRegister(state, data.data() + bytes, op.ra, width, vector);
            refetch = address > UINT64_MAX - (size - 1) || !memory->write_fast ||
                      !memory->write_fast(memory->context, address, data.data(), size);
            if (refetch) {
                if (!WriteMemory(state, *memory, address, data.data(), size)) return Fault(address, access);
            } else {
                ClearOverlappingExclusive(state, address, size);
            }
        }
    } else {
        if constexpr (pair) {
            if (!vector && op.rd == op.ra) return Undefined(pc);
        }
        const auto tag = address & ~uint64_t{mem::PAGE_MASK};
        const auto index = (address >> mem::PAGE_SHIFT) & mem::TLB_INDEX_MASK;
        if (read_tlb && (address & mem::PAGE_MASK) <= mem::PAGE_SIZE - size && read_tlb[index].ReadTag() == tag) {
            const auto source = reinterpret_cast<const void*>(read_tlb[index].addend + address);
            if constexpr (fixed_width != 0) {
                std::memcpy(data.data(), source, (fixed_width / 8) * (pair ? 2 : 1));
            } else {
                // Fixed copies keep the generic probe free of calls as well.
                switch (size) {
                    case 1:
                        std::memcpy(data.data(), source, 1);
                        break;
                    case 2:
                        std::memcpy(data.data(), source, 2);
                        break;
                    case 4:
                        std::memcpy(data.data(), source, 4);
                        break;
                    case 8:
                        std::memcpy(data.data(), source, 8);
                        break;
                    case 16:
                        std::memcpy(data.data(), source, 16);
                        break;
                    case 32:
                        if constexpr (pair) std::memcpy(data.data(), source, 32);
                        break;
                }
            }
        } else {
            if constexpr (probe_only) {
                return {Stop::Replay};
            } else {
                refetch = address > UINT64_MAX - (size - 1) || !memory->read_fast ||
                          !memory->read_fast(memory->context, address, data.data(), size);
                if (refetch && !ReadMemory(*memory, address, data.data(), size)) return Fault(address, access);
            }
        }
        LoadRegister<probe_only>(state, data.data(), op.rd, width, vector, operation, pinned);
        if constexpr (pair)
            LoadRegister<probe_only>(state, data.data() + bytes, op.ra, width, vector, operation, pinned);
    }
    if (update_base) WriteTransferRegister<probe_only>(state, op.rn, 64, writeback, true, pinned);
    return refetch ? StepResult{Stop::Refetch} : StepResult{};
}

template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
A64_MEMORY_INLINE StepResult LoadStore(State& state, const Memory& memory, const DecodedInstruction& op) {
    const auto literal_address = kind == Kind::LoadLiteral ? state.pc + op.immediate : 0;
    return LoadStoreTransfer<kind, width, operation, mode, shift, vector, false>(state, op, state.pc, literal_address,
                                                                                 memory.read_tlb, &memory, nullptr);
}

// Only a hit can change guest state. The caller replays the actual member on
// a miss, including stores and architectural exceptions.
template <Kind kind, unsigned width = 0, int operation = -1, int mode = -1, int shift = -1, int vector = -1>
A64_MEMORY_INLINE bool ProbeLoadStore(State& state, const DecodedInstruction& op, uint64_t literal_address,
                                      const mem::MicroTLB* read_tlb A64_HOT_PIN_REF) {
    uint64_t* pin = nullptr;
#if defined(FIBERCPU_AARCH64_PIN_REG) && FIBERCPU_AARCH64_PIN_REG >= 0
    pin = &pinned;
#endif
    return LoadStoreTransfer<kind, width, operation, mode, shift, vector, true>(state, op, 0, literal_address, read_tlb,
                                                                                nullptr, pin)
               .reason == Stop::Continue;
}

#undef A64_MEMORY_INLINE

inline void AcquireFence() { std::atomic_thread_fence(std::memory_order_acquire); }
inline void ReleaseFence() { std::atomic_thread_fence(std::memory_order_release); }

inline StepResult ExclusiveMemory(State& state, const Memory& memory, const DecodedInstruction& op) {
    const bool cas = op.mode == CompareSwap || op.mode == CompareSwapPair;
    const bool pair = op.mode == ExclusivePair || op.mode == CompareSwapPair;
    const auto bytes = op.width / 8;
    const unsigned total = bytes * (pair ? 2 : 1);
    const auto address = ReadRegister(state, op.rn, 64, true);
    const auto access = cas || op.operation == Store ? Access::Write : Access::Read;
    if ((op.rn == 31 && (address & 15)) || (address & (total - 1))) return Alignment(address, access);

    std::array<uint8_t, 16> data;
    if (cas) {
        if (Field(op.word, 15, 1)) ReleaseFence();
        if (!ReadMemory(memory, address, data.data(), total)) return Fault(address, access);
        const auto expected = ReadRegister(state, op.rm, op.width);
        const auto expected2 = pair ? ReadRegister(state, op.rm + 1, op.width) : 0;
        const auto actual = ReadElement(data.data(), op.width);
        const auto actual2 = pair ? ReadElement(data.data() + bytes, op.width) : 0;
        if (expected == actual && expected2 == actual2) {
            StoreRegister(state, data.data(), op.rd, op.width, false);
            if (pair) StoreRegister(state, data.data() + bytes, op.rd + 1, op.width, false);
            if (!WriteMemory(state, memory, address, data.data(), total)) return Fault(address, access);
        }
        WriteRegister(state, op.rm, 64, actual);
        if (pair) WriteRegister(state, op.rm + 1, 64, actual2);
        if (Field(op.word, 22, 1)) AcquireFence();
        return {};
    }

    const bool exclusive = op.mode == Exclusive || op.mode == ExclusivePair;
    const bool ordered = Field(op.word, 15, 1) || op.mode == Acquire;
    if (op.operation == Load) {
        if (pair && op.rd == op.ra) return Undefined(state.pc);
        if (!ReadMemory(memory, address, data.data(), total)) return Fault(address, access);
        if (exclusive) {
            state.exclusive_address = address;
            state.exclusive_size = total;
            std::copy_n(data.data(), total, state.exclusive_value.data());
        }
        if (ordered) AcquireFence();
        LoadRegister(state, data.data(), op.rd, op.width, false, Load);
        if (pair) LoadRegister(state, data.data() + bytes, op.ra, op.width, false, Load);
        return {};
    }

    if (exclusive) {
        if ((op.rm != 31 && (op.rm == op.rd || (pair && op.rm == op.ra))) || (op.rn != 31 && op.rm == op.rn))
            return Undefined(state.pc);
        const bool match = state.exclusive_size == total && state.exclusive_address == address;
        state.ClearExclusive();
        if (!match) {
            WriteRegister(state, op.rm, 32, 1);
            return {};
        }
        if (!ReadMemory(memory, address, data.data(), total)) return Fault(address, access);
        if (!std::equal(data.data(), data.data() + total, state.exclusive_value.data())) {
            WriteRegister(state, op.rm, 32, 1);
            return {};
        }
    }
    if (ordered) ReleaseFence();
    StoreRegister(state, data.data(), op.rd, op.width, false);
    if (pair) StoreRegister(state, data.data() + bytes, op.ra, op.width, false);
    if (!WriteMemory(state, memory, address, data.data(), total)) return Fault(address, access);
    if (exclusive) WriteRegister(state, op.rm, 32, 0);
    return {};
}

inline StepResult AtomicMemory(State& state, const Memory& memory, const DecodedInstruction& op) {
    const unsigned bytes = op.width / 8;
    const auto address = ReadRegister(state, op.rn, 64, true);
    if ((op.rn == 31 && (address & 15)) || (address & (bytes - 1))) return Alignment(address, Access::Write);
    if (Field(op.word, 22, 1)) ReleaseFence();
    std::array<uint8_t, 8> data;
    if (!ReadMemory(memory, address, data.data(), bytes)) return Fault(address, Access::Write);
    const auto previous = ReadElement(data.data(), op.width);
    const auto operand = ReadRegister(state, op.rm, op.width);
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
    if (!WriteMemory(state, memory, address, data.data(), bytes)) return Fault(address, Access::Write);
    if (Field(op.word, 23, 1) && op.rd != 31) AcquireFence();
    WriteRegister(state, op.rd, 64, previous);
    return {};
}

}  // namespace fiberish::aarch64::detail
