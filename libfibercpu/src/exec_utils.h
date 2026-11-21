#pragma once

#include <bit>
#include <type_traits>
#include "common.h"
#include "decoder.h"
#include "instruction_kernel_spec.h"
#include "state.h"

namespace fiberish {
using mem::FaultCode;
using mem::MemResult;

// ------------------------------------------------------------------------------------------------
// Register Access
// ------------------------------------------------------------------------------------------------

struct GprCache {
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_CACHE_FIELD(id, reg, slot, ...) uint32_t& reg;
    FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_FIELD)
#undef FIBERCPU_CACHE_FIELD
#endif

    // Dispatch supplies this only for the current instruction's matched address.
    const BakedAddress* baked_address = nullptr;

    FORCE_INLINE uint32_t GetReg(EmuState* state, unsigned index) const {
        const uint32_t value = state->ctx.regs[index];
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_CACHE_READ(id, reg, slot, ...) index == id ? reg:
        return FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_READ) value;
#undef FIBERCPU_CACHE_READ
#else
        return value;
#endif
    }

    FORCE_INLINE uint32_t GetAddressIndex(EmuState* state, unsigned index) const {
        const uint32_t value = state->ctx.regs[index];
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_CACHE_INDEX(id, reg, slot, ...) (id != ESP && index == id) ? reg:
        return index == ESP ? 0 : FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_INDEX) value;
#undef FIBERCPU_CACHE_INDEX
#else
        return index == ESP ? 0 : value;
#endif
    }

    FORCE_INLINE uint32_t GetRegFromOffset(EmuState* state, unsigned offset) const {
        // The decoder stores offsets into the GPR array, including the zero slot.
        return GetReg(state, offset / sizeof(uint32_t));
    }

    FORCE_INLINE void SetReg(EmuState* state, unsigned index, uint32_t value) const {
#if FIBERCPU_GPR_PIN_MODE == 4
        if (FIBERCPU_PIN_CONTAINS(index)) {
#if FIBERCPU_PIN_COUNT == 2
            const bool second = index == FIBERCPU_PIN_ID_1;
            FIBERCPU_PIN_FIELD_0 = second ? FIBERCPU_PIN_FIELD_0 : value;
            FIBERCPU_PIN_FIELD_1 = second ? value : FIBERCPU_PIN_FIELD_1;
#else
#define FIBERCPU_CACHE_WRITE(id, reg, slot, ...) reg = index == id ? value : reg;
            FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_WRITE)
#undef FIBERCPU_CACHE_WRITE
#endif
            return;
        }
#endif
        state->ctx.regs[index] = value;
    }

    FORCE_INLINE void SetReg8(EmuState* state, unsigned index, uint8_t value) const {
        const unsigned parent = index & 3;
        const uint32_t old = GetReg(state, parent);
        if (index < 4)
            SetReg(state, parent, (old & 0xffffff00u) | value);
        else
            SetReg(state, parent, (old & 0xffff00ffu) | (uint32_t(value) << 8));
    }

    FORCE_INLINE uint8_t GetReg8(EmuState* state, unsigned index) const {
        const uint32_t value = GetReg(state, index & 3);
        return static_cast<uint8_t>(index < 4 ? value : value >> 8);
    }

    FORCE_INLINE void Commit(EmuState* state) const {
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_CACHE_COMMIT(id, reg, slot, ...) state->ctx.regs[id] = reg;
        FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_COMMIT)
#undef FIBERCPU_CACHE_COMMIT
#endif
    }

    FORCE_INLINE void Reload(EmuState* state) const {
#if FIBERCPU_GPR_PIN_MODE == 4
#define FIBERCPU_CACHE_RELOAD(id, reg, slot, ...) reg = state->ctx.regs[id];
        FIBERCPU_FOR_EACH_PIN(FIBERCPU_CACHE_RELOAD)
#undef FIBERCPU_CACHE_RELOAD
#endif
    }

    template <typename Callback>
    FORCE_INLINE auto WithCanonical(EmuState* state, Callback&& callback) const {
        Commit(state);
        auto result = callback();
        Reload(state);
        return result;
    }
};

// Keep the inline TLB hit path inside the threaded chain. On a blocking miss,
// observers may read or change GPRs before the instruction continues.
template <typename T, bool FailOnMiss>
FORCE_INLINE MemResult<T> ReadGuestMem(EmuState* state, GprCache& gpr, uint32_t addr, mem::MicroTLB* utlb,
                                       const DecodedOp* op) {
#if FIBERCPU_GPR_PIN_MODE == 4
    if constexpr (!FailOnMiss) {
        if (auto result = state->mmu.read_tlb_only<T>(addr, utlb)) return result;
        state->sync_eip_to_op_start(op);
        return gpr.WithCanonical(state, [&] { return state->mmu.read_slow<T>(addr); });
    }
#endif
    return state->mmu.read<T, FailOnMiss>(addr, utlb, op);
}

template <typename T, bool FailOnMiss>
FORCE_INLINE MemResult<void> WriteGuestMem(EmuState* state, GprCache& gpr, uint32_t addr, T value, mem::MicroTLB* utlb,
                                           const DecodedOp* op) {
#if FIBERCPU_GPR_PIN_MODE == 4
    if constexpr (!FailOnMiss) {
        if (auto result = state->mmu.write_tlb_only<T>(addr, value, utlb)) return result;
        state->sync_eip_to_op_start(op);
        return gpr.WithCanonical(state, [&] { return state->mmu.write_slow<T>(addr, value); });
    }
#endif
    return state->mmu.write<T, FailOnMiss>(addr, value, utlb, op);
}

// ------------------------------------------------------------------------------------------------
// EFLAGS Masks
// ------------------------------------------------------------------------------------------------

constexpr uint32_t CF_MASK = 0x0001;
constexpr uint32_t PF_MASK = 0x0004;
constexpr uint32_t AF_MASK = 0x0010;
constexpr uint32_t ZF_MASK = 0x0040;
constexpr uint32_t SF_MASK = 0x0080;
constexpr uint32_t OF_MASK = 0x0800;

constexpr uint64_t FLAGS_CACHE_PF_STATE_SHIFT = 40;
constexpr uint64_t FLAGS_CACHE_PF_STATE_MASK = 0xFFull << FLAGS_CACHE_PF_STATE_SHIFT;
constexpr uint8_t FLAGS_CACHE_PF_KNOWN_TRUE = 0;
constexpr uint8_t FLAGS_CACHE_PF_KNOWN_FALSE = 1;

constexpr uint32_t ARITHMETIC_FLAGS_MASK = CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK;

#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
// These calls express flag producers and reads in the bitcode templates.
// Block assembly lowers them before the resulting code is emitted.
extern "C" uint64_t FiberLLVMWriteArithmeticFlags(uint64_t, uint32_t, uint8_t) __attribute__((const));
extern "C" uint64_t FiberLLVMWriteLogicalFlags(uint64_t, uint32_t, unsigned) __attribute__((const));
extern "C" bool FiberLLVMReadFlag(uint64_t, unsigned) __attribute__((const));
#endif

FORCE_INLINE uint8_t EncodeKnownParityState(bool pf_set) {
    return pf_set ? FLAGS_CACHE_PF_KNOWN_TRUE : FLAGS_CACHE_PF_KNOWN_FALSE;
}

FORCE_INLINE uint64_t InitFlagsCache(uint32_t eflags) {
    return static_cast<uint64_t>(eflags) |
           (static_cast<uint64_t>(EncodeKnownParityState((eflags & PF_MASK) != 0)) << FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE uint32_t GetFlags32(uint64_t flags_cache);
FORCE_INLINE uint32_t GetFlags32NoPF(uint64_t flags_cache);
FORCE_INLINE uint8_t PeekPFState(uint64_t flags_cache);
FORCE_INLINE bool IsKnownPFState(uint8_t pf_state);
FORCE_INLINE bool EvaluatePF(uint64_t flags_cache);

FORCE_INLINE uint64_t GetStateFlagsCache(const EmuState* state) { return state->ctx.flags_state; }

FORCE_INLINE void SetStateFlagsCache(EmuState* state, uint64_t flags_cache) { state->ctx.flags_state = flags_cache; }

FORCE_INLINE uint32_t GetArchitecturalEflags(const EmuState* state) {
    uint64_t flags_cache = GetStateFlagsCache(state);
    const bool pf = EvaluatePF(flags_cache);
    uint32_t flags = GetFlags32(flags_cache);
    if (pf)
        flags |= PF_MASK;
    else
        flags &= ~PF_MASK;
    return flags;
}

FORCE_INLINE void SetArchitecturalEflags(EmuState* state, uint32_t eflags) {
    SetStateFlagsCache(state, InitFlagsCache(eflags));
}

FORCE_INLINE uint32_t GetFlags32(uint64_t flags_cache) { return static_cast<uint32_t>(flags_cache); }

FORCE_INLINE uint32_t GetFlags32NoPF(uint64_t flags_cache) { return static_cast<uint32_t>(flags_cache); }

FORCE_INLINE void SetFlags32(uint64_t& flags_cache, uint32_t eflags) {
    flags_cache = (flags_cache & ~0xFFFFFFFFull) | eflags;
}

FORCE_INLINE void SetFlags32AndParityState(uint64_t& flags_cache, uint32_t eflags, uint8_t parity_state) {
    flags_cache = (flags_cache & ~(0xFFFFFFFFull | FLAGS_CACHE_PF_STATE_MASK)) | static_cast<uint64_t>(eflags) |
                  (static_cast<uint64_t>(parity_state) << FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE void SyncParityStateFromFlags(uint64_t& flags_cache) {
    flags_cache &= ~FLAGS_CACHE_PF_STATE_MASK;
    flags_cache |= (static_cast<uint64_t>(EncodeKnownParityState((GetFlags32(flags_cache) & PF_MASK) != 0))
                    << FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE void SetFlags32AndSyncParityState(uint64_t& flags_cache, uint32_t eflags) {
    SetFlags32(flags_cache, eflags);
    SyncParityStateFromFlags(flags_cache);
}

FORCE_INLINE void CommitFlagsCache(EmuState* state, uint64_t& flags_cache);

FORCE_INLINE void SetFlagBits(uint64_t& flags_cache, uint32_t mask) {
    SetFlags32(flags_cache, GetFlags32(flags_cache) | mask);
    if (mask & PF_MASK) SyncParityStateFromFlags(flags_cache);
}

FORCE_INLINE void ClearFlagBits(uint64_t& flags_cache, uint32_t mask) {
    SetFlags32(flags_cache, GetFlags32(flags_cache) & ~mask);
    if (mask & PF_MASK) SyncParityStateFromFlags(flags_cache);
}

FORCE_INLINE bool TestFlagBits(uint64_t flags_cache, uint32_t mask) { return (GetFlags32(flags_cache) & mask) != 0; }
template <uint32_t Mask>
FORCE_INLINE bool ReadArithmeticFlag(uint64_t flags_cache) {
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    return FiberLLVMReadFlag(flags_cache, Mask);
#else
    return TestFlagBits(flags_cache, Mask);
#endif
}
FORCE_INLINE bool ReadCF(uint64_t flags_cache) { return ReadArithmeticFlag<CF_MASK>(flags_cache); }
FORCE_INLINE bool ReadZF(uint64_t flags_cache) { return ReadArithmeticFlag<ZF_MASK>(flags_cache); }
FORCE_INLINE bool ReadSF(uint64_t flags_cache) { return ReadArithmeticFlag<SF_MASK>(flags_cache); }
FORCE_INLINE bool ReadOF(uint64_t flags_cache) { return ReadArithmeticFlag<OF_MASK>(flags_cache); }
FORCE_INLINE bool ReadAF(uint64_t flags_cache) { return ReadArithmeticFlag<AF_MASK>(flags_cache); }
FORCE_INLINE void SetCF(uint64_t& flags_cache) { SetFlagBits(flags_cache, CF_MASK); }
FORCE_INLINE void ClearCF(uint64_t& flags_cache) { ClearFlagBits(flags_cache, CF_MASK); }
FORCE_INLINE void AssignCF(uint64_t& flags_cache, bool value) { value ? SetCF(flags_cache) : ClearCF(flags_cache); }
FORCE_INLINE void SetZF(uint64_t& flags_cache) { SetFlagBits(flags_cache, ZF_MASK); }
FORCE_INLINE void ClearZF(uint64_t& flags_cache) { ClearFlagBits(flags_cache, ZF_MASK); }
FORCE_INLINE void AssignZF(uint64_t& flags_cache, bool value) { value ? SetZF(flags_cache) : ClearZF(flags_cache); }
FORCE_INLINE void SetSF(uint64_t& flags_cache) { SetFlagBits(flags_cache, SF_MASK); }
FORCE_INLINE void ClearSF(uint64_t& flags_cache) { ClearFlagBits(flags_cache, SF_MASK); }
FORCE_INLINE void AssignSF(uint64_t& flags_cache, bool value) { value ? SetSF(flags_cache) : ClearSF(flags_cache); }
FORCE_INLINE void SetOF(uint64_t& flags_cache) { SetFlagBits(flags_cache, OF_MASK); }
FORCE_INLINE void ClearOF(uint64_t& flags_cache) { ClearFlagBits(flags_cache, OF_MASK); }
FORCE_INLINE void AssignOF(uint64_t& flags_cache, bool value) { value ? SetOF(flags_cache) : ClearOF(flags_cache); }
FORCE_INLINE void SetAF(uint64_t& flags_cache) { SetFlagBits(flags_cache, AF_MASK); }
FORCE_INLINE void ClearAF(uint64_t& flags_cache) { ClearFlagBits(flags_cache, AF_MASK); }
FORCE_INLINE void AssignAF(uint64_t& flags_cache, bool value) { value ? SetAF(flags_cache) : ClearAF(flags_cache); }
FORCE_INLINE uint32_t BuildArithmeticFlagsBase(uint64_t flags_cache) {
    return GetFlags32NoPF(flags_cache) & ~ARITHMETIC_FLAGS_MASK;
}
FORCE_INLINE void WriteArithmeticFlags(uint64_t& flags_cache, uint32_t flags, uint8_t parity_state) {
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    flags_cache = FiberLLVMWriteArithmeticFlags(flags_cache, flags, parity_state);
#else
    SetFlags32AndParityState(flags_cache, BuildArithmeticFlagsBase(flags_cache) | flags, parity_state);
#endif
}
FORCE_INLINE void ClearArithmeticFlags(uint64_t& flags_cache) {
    SetFlags32(flags_cache, GetFlags32NoPF(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK));
}

// ------------------------------------------------------------------------------------------------
// Condition Checking (for Jcc, CMOVcc)
// ------------------------------------------------------------------------------------------------

template <uint8_t Cond>
inline bool CheckConditionFixed(uint64_t& flags_cache) {
    if constexpr (Cond == 0)
        return ReadOF(flags_cache);  // JO
    else if constexpr (Cond == 1)
        return !ReadOF(flags_cache);  // JNO
    else if constexpr (Cond == 2)
        return ReadCF(flags_cache);  // JB/JC
    else if constexpr (Cond == 3)
        return !ReadCF(flags_cache);  // JNB/JNC
    else if constexpr (Cond == 4)
        return ReadZF(flags_cache);  // JZ/JE
    else if constexpr (Cond == 5)
        return !ReadZF(flags_cache);  // JNZ/JNE
    else if constexpr (Cond == 6)
        return ReadCF(flags_cache) || ReadZF(flags_cache);  // JBE
    else if constexpr (Cond == 7)
        return !ReadCF(flags_cache) && !ReadZF(flags_cache);  // JA
    else if constexpr (Cond == 8)
        return ReadSF(flags_cache);  // JS
    else if constexpr (Cond == 9)
        return !ReadSF(flags_cache);  // JNS
    else if constexpr (Cond == 10)
        return EvaluatePF(flags_cache);  // JP/JPE
    else if constexpr (Cond == 11)
        return !EvaluatePF(flags_cache);  // JNP/JPO
    else if constexpr (Cond == 12)
        return ReadSF(flags_cache) != ReadOF(flags_cache);  // JL
    else if constexpr (Cond == 13)
        return ReadSF(flags_cache) == ReadOF(flags_cache);  // JGE
    else if constexpr (Cond == 14)
        return ReadZF(flags_cache) || (ReadSF(flags_cache) != ReadOF(flags_cache));  // JLE
    else if constexpr (Cond == 15)
        return !ReadZF(flags_cache) && (ReadSF(flags_cache) == ReadOF(flags_cache));  // JG
    else
        return false;
}

inline bool CheckCondition(uint64_t& flags_cache, uint8_t cond) {
    if ((cond & 0xF) == 10) return EvaluatePF(flags_cache);
    if ((cond & 0xF) == 11) return !EvaluatePF(flags_cache);

    static const uint32_t g_ConditionLUT[16] = {
        0xFFFF0000,  // cond 0: JO
        0x0000FFFF,  // cond 1: JNO
        0xAAAAAAAA,  // cond 2: JB
        0x55555555,  // cond 3: JAE
        0xF0F0F0F0,  // cond 4: JZ
        0x0F0F0F0F,  // cond 5: JNZ
        0xFAFAFAFA,  // cond 6: JBE
        0x05050505,  // cond 7: JA
        0xFF00FF00,  // cond 8: JS
        0x00FF00FF,  // cond 9: JNS
        0xCCCCCCCC,  // cond 10: JP
        0x33333333,  // cond 11: JNP
        0x00FFFF00,  // cond 12: JL
        0xFF0000FF,  // cond 13: JGE
        0xF0FFFFF0,  // cond 14: JLE
        0x0F00000F,  // cond 15: JG
    };

    uint32_t f = GetFlags32NoPF(flags_cache);
    // Index bits: OF(bit 11) SF(bit 7) ZF(bit 6) PF(bit 2) CF(bit 0)
    // We map them to: OF:4, SF:3, ZF:2, PF:1, CF:0
    uint32_t index = (f & 0x1) | ((f >> 1) & 0x2) | ((f >> 4) & 0x4) | ((f >> 4) & 0x8) | ((f >> 7) & 0x10);

    return (g_ConditionLUT[cond & 0xF] >> index) & 1;
}

// ------------------------------------------------------------------------------------------------
// Effective Address Calculation
// ------------------------------------------------------------------------------------------------

FORCE_INLINE uint32_t GetSegmentBase(EmuState* state, Prefixes prefixes) {
    const uint8_t seg = prefixes.flags.segment;
    // 1=ES, 2=CS, 3=SS, 4=DS, 5=FS, 6=GS
    // Moffs, XLAT and string source operands default to DS. An explicit
    // override may select any of the six caller-provided segment bases.
    return state->ctx.seg_base[seg == 0 ? 4 : seg];
}

FORCE_INLINE uint32_t GetSegmentBase(EmuState* state, const DecodedOp* op) {
    return GetSegmentBase(state, GetPrefixes(op));
}

template <class Operands>
FORCE_INLINE uint32_t GetSegmentBase(EmuState* state, const DecodedOp* op, const Operands& operands) {
    return GetSegmentBase(state, operands.ReadPrefixes(op));
}

FORCE_INLINE uint32_t ComputeEA(EmuState* state, GprCache& gpr, const DecodedOp* op) {
    if (const auto* address = gpr.baked_address) {
        const auto ea = address->descriptor;
        const auto base_offset = memdesc::BaseOffset(ea);
        const auto index_offset = memdesc::IndexOffset(ea);
        const auto base = base_offset == kNoRegOffset ? 0 : gpr.GetRegFromOffset(state, base_offset);
        const auto index = index_offset == kNoRegOffset ? 0 : gpr.GetRegFromOffset(state, index_offset);
        const auto displacement = address->displacement_known ? address->displacement : GetDisplacement(op);
        const auto offset = base + (index << memdesc::Scale(ea)) + displacement;
        return address->address16 ? offset & 0xffffu : offset;
    }
    const uint32_t base = gpr.GetReg(state, op->AddressBase());
    const uint32_t index = gpr.GetAddressIndex(state, op->AddressIndex());
    const uint32_t offset = base + (index << op->AddressScale()) + GetDisplacement(op);
    // Address width wraps the offset before the addition of the segment base.
    return op->AddressIs16() ? offset & 0xffffu : offset;
}

FORCE_INLINE uint32_t GetMemSegmentBase(EmuState* state, const GprCache& gpr, const DecodedOp* op) {
    const auto seg =
        gpr.baked_address ? memdesc::ResolvedSegment(gpr.baked_address->descriptor) : op->ResolvedMemorySegment();
    return state->ctx.seg_base[seg];
}

FORCE_INLINE uint32_t ComputeLinearAddress(EmuState* state, GprCache& gpr, const DecodedOp* op) {
    uint32_t ea = ComputeEA(state, gpr, op);
    return ea + GetMemSegmentBase(state, gpr, op);
}

// ------------------------------------------------------------------------------------------------
// Internal Helpers (Register Access & Memory Splits)
// ------------------------------------------------------------------------------------------------

// Helper: Handle Register Reads for various sizes
template <typename T>
inline T ReadRegGeneric(EmuState* state, GprCache& gpr, uint8_t rm) {
    if constexpr (std::is_same_v<T, simde__m128>) {
        // 128-bit XMM access
        return state->ctx.xmm[rm];
    } else {
        // General Purpose Registers (8, 16, 32-bit)
        uint32_t val = gpr.GetReg(state, rm & (std::is_same_v<T, uint8_t> ? 3 : 7));

        if constexpr (std::is_same_v<T, uint8_t>) {
            // 8-bit: Handle High byte (AH, CH, DH, BH) vs Low byte
            if (rm < 4)
                return static_cast<uint8_t>(val & 0xFF);
            else
                return static_cast<uint8_t>((val >> 8) & 0xFF);
        } else if constexpr (std::is_same_v<T, uint16_t>) {
            // 16-bit: Mask lower word
            return static_cast<uint16_t>(val & 0xFFFF);
        } else {
            // 32-bit: Full register
            return static_cast<T>(val);
        }
    }
}

// Helper: Handle Register Writes (including partial register preservation)
template <typename T>
inline void WriteRegGeneric(EmuState* state, GprCache& gpr, uint8_t rm, T val) {
    if constexpr (std::is_same_v<T, simde__m128>) {
        // 128-bit XMM write
        state->ctx.xmm[rm] = val;
    } else {
        // General Purpose Registers
        if constexpr (std::is_same_v<T, uint32_t>) {
            // 32-bit: Direct overwrite
            gpr.SetReg(state, rm, val);
        } else {
            // 8-bit and 16-bit: Read-Modify-Write to preserve other bits
            // Note: For 8-bit, index logic differs (rm & 3)
            uint32_t regIdx = std::is_same_v<T, uint8_t> ? (rm & 3) : rm;
            uint32_t curr = gpr.GetReg(state, regIdx);

            if constexpr (std::is_same_v<T, uint8_t>) {
                if (rm < 4) {
                    // Low byte (AL, CL...)
                    curr = (curr & 0xFFFFFF00) | val;
                } else {
                    // High byte (AH, CH...)
                    curr = (curr & 0xFFFF00FF) | (static_cast<uint32_t>(val) << 8);
                }
            } else if constexpr (std::is_same_v<T, uint16_t>) {
                // 16-bit
                curr = (curr & 0xFFFF0000) | val;
            }
            gpr.SetReg(state, regIdx, curr);
        }
    }
}

/**
 * Strategy for handling TLB Misses in memory operations.
 */
enum class OpOnTLBMiss {
    // Block until the memory operation is complete (or fault).
    // Corresponds to legacy fail_on_tlb_miss = false.
    Blocking,

    // Return failure logic flow to allow restarting the instruction.
    // Checks for pending operation completion on re-entry.
    // Corresponds to legacy fail_on_tlb_miss = true, request_retry = true (READ) / request_write_and_check (WRITE RMW).
    Restart,

    // Return failure logic flow to retry the operation from the next instruction boundary.
    // Does NOT check for pending operation on re-entry (fire and forget).
    // Corresponds to legacy fail_on_tlb_miss = true, request_retry = true (WRITE ONLY).
    Retry
};

/**
 * Reads a value from a Register or Memory based on the ModRM byte.
 * Supports uint8_t, uint16_t, uint32_t, and simde__m128.
 */
template <typename T, OpOnTLBMiss Strategy, uint32_t Alignment = 1, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE MemResult<T> ReadModRM(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t modrm, mem::MicroTLB* utlb) {
    uint8_t rm = modrm & 7;

    if (OperandIsReg<Form>(modrm)) [[likely]] {
        // Register Operand
        return ReadRegGeneric<T>(state, gpr, rm);
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, gpr, op);

        if constexpr (Alignment > 1) {
            if (addr & (Alignment - 1)) [[unlikely]] {
                state->sync_eip_to_op_start(op);
                state->status = EmuStatus::Fault;
                state->fault_vector = 13;
                return std::unexpected(FaultCode::GeneralProtection);
            }
        }

        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return ReadGuestMem<T, false>(state, gpr, addr, utlb, op);
        } else {
            // Restart or Retry (though Retry for Read is unusual, treating as Restart)
            auto value = state->mmu.read<T, true>(addr, utlb, op);
            if (!value) [[unlikely]] {
                // For Read, we always need to check pending if we want to restart
                // If usage suggests Retry for Read, it likely implies prefetch which we don't support explicitly yet.
                // Assuming Restart semantics for both Restart and Retry enums for Read to be safe.
                value = state->request_read_and_check_pending<T>(addr, op);
            }
            return value;
        }
    }
}

/**
 * Writes a value to a Register or Memory based on the ModRM byte.
 * Supports uint8_t, uint16_t, uint32_t, and simde__m128.
 */
template <typename T, OpOnTLBMiss Strategy, uint32_t Alignment = 1, AddrModeSpec Form = AddrModeSpec::Dynamic>
FORCE_INLINE MemResult<void> WriteModRM(EmuState* state, GprCache& gpr, DecodedOp* op, uint8_t modrm, T val,
                                        mem::MicroTLB* utlb) {
    uint8_t rm = modrm & 7;

    if (OperandIsReg<Form>(modrm)) [[likely]] {
        // Register Operand
        WriteRegGeneric<T>(state, gpr, rm, val);
        return {};
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, gpr, op);

        if constexpr (Alignment > 1) {
            if (addr & (Alignment - 1)) [[unlikely]] {
                state->sync_eip_to_op_start(op);
                state->status = EmuStatus::Fault;
                state->fault_vector = 13;
                return std::unexpected(FaultCode::GeneralProtection);
            }
        }

        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return WriteGuestMem<T, false>(state, gpr, addr, val, utlb, op);
        } else {
            auto result = state->mmu.write<T, true>(addr, val, utlb, op);
            if (!result) [[unlikely]] {
                if constexpr (Strategy == OpOnTLBMiss::Restart) {
                    result = state->request_write_and_check_pending<T>(addr, val, op);
                } else {
                    // Retry
                    result = state->request_write_only<T>(addr, val, op);
                }
            }
            return result;
        }
    }
}

/**
 * Generic Helper for Direct Memory Read
 */
template <typename T, OpOnTLBMiss Strategy>
inline MemResult<T> ReadMem(EmuState* state, GprCache& gpr, uint32_t addr, mem::MicroTLB* utlb, const DecodedOp* op) {
    if constexpr (Strategy == OpOnTLBMiss::Blocking) {
        return ReadGuestMem<T, false>(state, gpr, addr, utlb, op);
    } else {
        auto value = state->mmu.read<T, true>(addr, utlb, op);
        if (!value) [[unlikely]] {
            value = state->request_read_and_check_pending<T>(addr, op);
        }
        return value;
    }
}

/**
 * Generic Helper for Direct Memory Write
 */
template <typename T, OpOnTLBMiss Strategy>
FORCE_INLINE MemResult<void> WriteMem(EmuState* state, GprCache& gpr, uint32_t addr, T val, mem::MicroTLB* utlb,
                                      const DecodedOp* op) {
    if constexpr (Strategy == OpOnTLBMiss::Blocking) {
        return WriteGuestMem<T, false>(state, gpr, addr, val, utlb, op);
    } else {
        auto result = state->mmu.write<T, true>(addr, val, utlb, op);
        if (!result) [[unlikely]] {
            if constexpr (Strategy == OpOnTLBMiss::Restart) {
                result = state->request_write_and_check_pending<T>(addr, val, op);
            } else {
                // Retry
                result = state->request_write_only<T>(addr, val, op);
            }
        }
        return result;
    }
}

// Resolve the destination once for one attempt at a read-modify-write.
// A restarted instruction constructs a new operand before consuming its pending read.
template <AddrModeSpec Form = AddrModeSpec::Dynamic>
class ModRMRmwOperand {
public:
    FORCE_INLINE ModRMRmwOperand(EmuState* state, GprCache& gpr, const DecodedOp* op, uint8_t modrm)
        : modrm_(modrm), address_(IsRegister() ? 0 : ComputeLinearAddress(state, gpr, op)) {}

    FORCE_INLINE bool IsRegister() const { return OperandIsReg<Form>(modrm_); }
    FORCE_INLINE uint8_t Reg() const { return modrm_ & 7; }
    FORCE_INLINE uint32_t Address() const { return address_; }

    template <typename T, OpOnTLBMiss Strategy>
    FORCE_INLINE MemResult<T> Read(EmuState* state, GprCache& gpr, const DecodedOp* op, mem::MicroTLB* utlb) const {
        if (IsRegister()) return ReadRegGeneric<T>(state, gpr, Reg());
        return ReadMem<T, Strategy>(state, gpr, Address(), utlb, op);
    }

    template <typename T, OpOnTLBMiss Strategy>
    FORCE_INLINE MemResult<void> Write(EmuState* state, GprCache& gpr, const DecodedOp* op, T value,
                                       mem::MicroTLB* utlb) const {
        if (IsRegister()) {
            WriteRegGeneric<T>(state, gpr, Reg(), value);
            return {};
        }
        return WriteMem<T, Strategy>(state, gpr, Address(), value, utlb, op);
    }

private:
    uint8_t modrm_;
    uint32_t address_;
};

// ------------------------------------------------------------------------------------------------
// Stack Operations (Refactored)
// ------------------------------------------------------------------------------------------------

template <typename T, bool fail_on_tlb_miss = false>
FORCE_INLINE MemResult<void> Push(EmuState* state, GprCache& gpr, T val, mem::MicroTLB* utlb, DecodedOp* op) {
    constexpr uint32_t size = sizeof(T);
    uint32_t esp = gpr.GetReg(state, ESP);
    const uint32_t ss_base = state->ctx.seg_base[3];  // SS

    // Write to memory (ESP - size)
    auto res = WriteGuestMem<T, fail_on_tlb_miss>(state, gpr, ss_base + esp - size, val, utlb, op);

    // Request a write and return
    if (!res && fail_on_tlb_miss) [[unlikely]] {
        // Push can satisfy "Retry" semantics (update ESP only on success)
        res = state->request_write_and_check_pending<T>(ss_base + esp - size, val, op);
    }

    if (res) {
        // Update ESP only if memory write succeeded
        gpr.SetReg(state, ESP, esp - size);
    }
    return res;
}

template <typename T, bool fail_on_tlb_miss = false>
FORCE_INLINE MemResult<T> Pop(EmuState* state, GprCache& gpr, mem::MicroTLB* utlb, DecodedOp* op) {
    constexpr uint32_t size = sizeof(T);
    uint32_t esp = gpr.GetReg(state, ESP);
    const uint32_t ss_base = state->ctx.seg_base[3];  // SS

    // Read from memory (ESP)
    auto res = ReadGuestMem<T, fail_on_tlb_miss>(state, gpr, ss_base + esp, utlb, op);
    if (!res && fail_on_tlb_miss) [[unlikely]] {
        res = state->request_read_and_check_pending<T>(ss_base + esp, op);
    }

    if (res) {
        // Update ESP only if memory read succeeded
        gpr.SetReg(state, ESP, esp + size);
    }
    return res;
}

// Explicit aliases for compatibility
// Default to fail_on_tlb_miss = true for simple usage (ops_control)
FORCE_INLINE MemResult<void> Push16(EmuState* s, GprCache& gpr, uint16_t v, mem::MicroTLB* u, DecodedOp* o) {
    return Push<uint16_t, true>(s, gpr, v, u, o);
}
FORCE_INLINE MemResult<void> Push32(EmuState* s, GprCache& gpr, uint32_t v, mem::MicroTLB* u, DecodedOp* o) {
    return Push<uint32_t, true>(s, gpr, v, u, o);
}
FORCE_INLINE MemResult<uint16_t> Pop16(EmuState* s, GprCache& gpr, mem::MicroTLB* u, DecodedOp* o) {
    return Pop<uint16_t, true>(s, gpr, u, o);
}
FORCE_INLINE MemResult<uint32_t> Pop32(EmuState* s, GprCache& gpr, mem::MicroTLB* u, DecodedOp* o) {
    return Pop<uint32_t, true>(s, gpr, u, o);
}

// ------------------------------------------------------------------------------------------------
// ALU Operations & Flags
// ------------------------------------------------------------------------------------------------

FORCE_INLINE bool CalcParityOdd(uint8_t v) {
    v ^= static_cast<uint8_t>(v >> 4);
    v ^= static_cast<uint8_t>(v >> 2);
    v ^= static_cast<uint8_t>(v >> 1);
    return static_cast<bool>(v & 1);
}

// Returns true if PF should be set (even number of 1s in low 8 bits)
inline bool CalcPflag(uint8_t res_byte) { return !CalcParityOdd(res_byte); }

// Backward compatibility for other ops files
inline uint8_t Parity(uint8_t v) { return CalcPflag(v) ? 1 : 0; }

FORCE_INLINE void SetParityState(uint64_t& flags_cache, uint8_t res_byte) {
    flags_cache &= ~FLAGS_CACHE_PF_STATE_MASK;
    flags_cache |= (static_cast<uint64_t>(res_byte) << FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE uint8_t PeekPFState(uint64_t flags_cache) {
    return static_cast<uint8_t>((flags_cache & FLAGS_CACHE_PF_STATE_MASK) >> FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE bool IsKnownPFState(uint8_t pf_state) {
    return pf_state == FLAGS_CACHE_PF_KNOWN_TRUE || pf_state == FLAGS_CACHE_PF_KNOWN_FALSE;
}

FORCE_INLINE bool EvaluatePF(uint64_t flags_cache) {
    const uint8_t pf_state = PeekPFState(flags_cache);
    return CalcPflag(pf_state);
}

FORCE_INLINE void CommitFlagsCache(EmuState* state, uint64_t& flags_cache) { SetStateFlagsCache(state, flags_cache); }

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluAdd(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest + src;

    if constexpr (UpdateFlags) {
        uint32_t msb_idx = sizeof(T) * 8 - 1;
        T sign_mask = (T)1 << msb_idx;
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> msb_idx) & 1) != 0);
        AssignCF(flags_cache, res < dest);
        AssignOF(flags_cache, !((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask));
        AssignAF(flags_cache, ((dest ^ src ^ res) & AF_MASK) != 0);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T>
struct SubtractionResultFlags {
    using Difference = std::conditional_t<sizeof(T) == 2, uint32_t, T>;
    T dest;
    T src;

    FORCE_INLINE Difference GetDifference() const { return Difference(dest) - Difference(src); }
    FORCE_INLINE bool IsZero() const { return dest == src; }

    FORCE_INLINE void Pack(uint64_t& flags_cache, bool zero) const {
        const Difference difference = GetDifference();
        const T res = static_cast<T>(difference);
        uint32_t flags = zero ? ZF_MASK : 0;
        if constexpr (sizeof(T) == 1) {
            const uint32_t d = dest;
            const uint32_t s = src;
            const uint32_t r = res;
            flags |= r & SF_MASK;
            if (d < s) flags |= CF_MASK;
            if ((((d ^ s) & (d ^ r)) & SF_MASK) != 0) flags |= OF_MASK;
            if (((d ^ s ^ r) & AF_MASK) != 0) flags |= AF_MASK;
            WriteArithmeticFlags(flags_cache, flags, static_cast<uint8_t>(r));
        } else if constexpr (sizeof(T) == 2) {
            // The same subtraction yields the result and the unsigned borrow.
            // Keep the high bits until flags have been produced.
            const uint32_t sign_changes = (dest ^ src) & (dest ^ difference);
            flags |= (difference >> 8) & SF_MASK;
            flags |= (difference >> 16) & CF_MASK;
            flags |= (sign_changes >> 4) & OF_MASK;
            flags |= (dest ^ src ^ difference) & AF_MASK;
            WriteArithmeticFlags(flags_cache, flags, static_cast<uint8_t>(difference));
        } else {
            uint32_t msb_idx = sizeof(T) * 8 - 1;
            T sign_mask = (T)1 << msb_idx;
            if (((res >> msb_idx) & 1) != 0) flags |= SF_MASK;
            if (dest < src) flags |= CF_MASK;
            if (((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;
            if (((dest ^ src ^ res) & AF_MASK) != 0) flags |= AF_MASK;
            WriteArithmeticFlags(flags_cache, flags, static_cast<uint8_t>(res));
        }
    }
};

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluSub(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    const SubtractionResultFlags<T> flags{dest, src};
    const auto difference = flags.GetDifference();
    if constexpr (UpdateFlags) flags.Pack(flags_cache, difference == 0);
    return static_cast<T>(difference);
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluAdc(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    uint32_t cf_in = ReadCF(flags_cache) ? 1u : 0u;

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest + wsrc + cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t msb_idx = sizeof(T) * 8 - 1;
        T sign_mask = (T)1 << msb_idx;
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> msb_idx) & 1) != 0);
        AssignCF(flags_cache, (wres >> (sizeof(T) * 8)) != 0);
        AssignOF(flags_cache, !((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask));
        AssignAF(flags_cache, ((dest ^ src ^ res) & AF_MASK) != 0);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluSbb(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    uint32_t cf_in = ReadCF(flags_cache) ? 1u : 0u;

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest - wsrc - cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t msb_idx = sizeof(T) * 8 - 1;
        T sign_mask = (T)1 << msb_idx;
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> msb_idx) & 1) != 0);
        AssignCF(flags_cache, wdest < (wsrc + cf_in));
        AssignOF(flags_cache, ((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask));
        AssignAF(flags_cache, ((dest ^ src ^ res) & AF_MASK) != 0);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T>
struct LogicalResultFlags {
    T result;

    FORCE_INLINE bool IsZero() const { return result == 0; }

    FORCE_INLINE void Pack(uint64_t& flags_cache, bool zero) const {
        uint32_t flags = zero ? ZF_MASK : 0;
        if (((result >> (sizeof(T) * 8 - 1)) & 1) != 0) flags |= SF_MASK;
        WriteArithmeticFlags(flags_cache, flags, static_cast<uint8_t>(result));
    }
};

template <typename T>
FORCE_INLINE void SetLogicalResultFlags(uint64_t& flags_cache, T res) {
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    flags_cache = FiberLLVMWriteLogicalFlags(flags_cache, res, sizeof(T) * 8);
#else
    const LogicalResultFlags<T> flags{res};
    flags.Pack(flags_cache, flags.IsZero());
#endif
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluAnd(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    const T res = dest & src;
    if constexpr (UpdateFlags) SetLogicalResultFlags(flags_cache, res);
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluOr(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest | src;

    if constexpr (UpdateFlags) {
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> (sizeof(T) * 8 - 1)) & 1) != 0);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluXor(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest ^ src;

    if constexpr (UpdateFlags) {
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> (sizeof(T) * 8 - 1)) & 1) != 0);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

// ------------------------------------------------------------------------------------------------
// Shift / Rotate
// ------------------------------------------------------------------------------------------------

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluShl(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // Widen before shifting: narrow unsigned operands otherwise promote to
    // signed int, and counts above the operand width must not underflow the
    // carry-bit index. CF is undefined for SHL counts >= the operand width.
    uint32_t width = sizeof(T) * 8;
    uint64_t bits = static_cast<std::make_unsigned_t<T>>(dest);
    bool cf = count <= width && ((bits >> (width - count)) & 1);

    T res = static_cast<T>(bits << count);

    if constexpr (UpdateFlags) {
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> (width - 1)) & 1) != 0);
        AssignCF(flags_cache, cf);
        if (count == 1) {
            bool msb_res = (res >> (width - 1)) & 1;
            AssignOF(flags_cache, msb_res != cf);
        }
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T>
struct RightShiftResultFlags {
    T result;
    bool carry;
    bool overflow;

    FORCE_INLINE bool IsZero() const { return result == 0; }

    FORCE_INLINE void Pack(uint64_t& flags_cache, bool zero) const {
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, zero);
        AssignSF(flags_cache, ((result >> (sizeof(T) * 8 - 1)) & 1) != 0);
        AssignCF(flags_cache, carry);
        AssignOF(flags_cache, overflow);
        SetParityState(flags_cache, static_cast<uint8_t>(result));
    }
};

// The caller has already masked the count and ruled out zero.
template <typename T>
FORCE_INLINE RightShiftResultFlags<T> CalculateRightShift(T dest, uint8_t count) {
    return {static_cast<T>(dest >> count), ((dest >> (count - 1)) & 1) != 0,
            count == 1 && ((dest >> (sizeof(T) * 8 - 1)) & 1) != 0};
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluShr(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    count &= 0x1F;
    if (count == 0) return dest;
    const auto flags = CalculateRightShift(dest, count);
    if constexpr (UpdateFlags) flags.Pack(flags_cache, flags.IsZero());
    return flags.result;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluSar(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // Arithmetic Shift
    using ST = std::make_signed_t<T>;
    ST sdest = (ST)dest;
    ST sres = sdest >> count;
    T res = (T)sres;

    // Once all original bits have left the operand, SAR keeps shifting out
    // the replicated sign bit. Unlike SHL/SHR, CF is defined in this case.
    uint32_t width = sizeof(T) * 8;
    bool cf = count >= width ? sdest < 0 : ((dest >> (count - 1)) & 1) != 0;

    if constexpr (UpdateFlags) {
        ClearArithmeticFlags(flags_cache);
        AssignZF(flags_cache, res == 0);
        AssignSF(flags_cache, ((res >> (sizeof(T) * 8 - 1)) & 1) != 0);
        AssignCF(flags_cache, cf);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluRol(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    constexpr unsigned width = sizeof(T) * 8;
    uint8_t masked_count = count & 0x1F;
    if (masked_count == 0) return dest;

    // A full byte/word turn preserves the value but still updates CF.
    T res = std::rotl(dest, masked_count);

    if constexpr (UpdateFlags) {
        // SF/ZF/PF/AF are untouched, including any deferred parity state.
        bool cf = (res & 1) != 0;
        AssignCF(flags_cache, cf);

        // OF is defined only when the masked (not modulo-width) count is 1.
        if (masked_count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            AssignOF(flags_cache, msb != cf);
        }
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluRor(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    constexpr unsigned width = sizeof(T) * 8;
    uint8_t masked_count = count & 0x1F;
    if (masked_count == 0) return dest;

    T res = std::rotr(dest, masked_count);

    if constexpr (UpdateFlags) {
        // CF: MSB of result
        bool cf = (res >> (width - 1)) & 1;
        AssignCF(flags_cache, cf);

        // SF/ZF/PF/AF are preserved; OF is undefined for masked counts > 1.
        if (masked_count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            AssignOF(flags_cache, msb != smsb);
        }
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluRcl(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    uint8_t masked_count = count & 0x1F;
    count = masked_count % (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (ReadCF(flags_cache)) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val << count) | (val >> (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        AssignCF(flags_cache, new_cf);

        if (masked_count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            AssignOF(flags_cache, msb != new_cf);
        }
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
FORCE_INLINE T AluRcr(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    uint8_t masked_count = count & 0x1F;
    count = masked_count % (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (ReadCF(flags_cache)) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val >> count) | (val << (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        AssignCF(flags_cache, new_cf);

        if (masked_count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            AssignOF(flags_cache, msb != smsb);
        }
    }
    return res;
}

template <typename T>
FORCE_INLINE void AluCmp(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    AluSub<T, true>(state, flags_cache, dest, src);
}

template <typename T>
FORCE_INLINE void AluTest(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    AluAnd<T, true>(state, flags_cache, dest, src);
}

}  // namespace fiberish
