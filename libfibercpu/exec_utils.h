#pragma once

#include <type_traits>
#include "common.h"
#include "decoder.h"
#include "state.h"

namespace fiberish {
using mem::FaultCode;
using mem::MemResult;

// ------------------------------------------------------------------------------------------------
// Register Access
// ------------------------------------------------------------------------------------------------

FORCE_INLINE uint32_t* GetRegPtr(EmuState* state, uint8_t reg_idx) { return &state->ctx.regs[reg_idx]; }

FORCE_INLINE uint32_t GetReg(EmuState* state, uint8_t reg_idx) { return *GetRegPtr(state, reg_idx); }

FORCE_INLINE void SetReg(EmuState* state, uint8_t reg_idx, uint32_t val) { *GetRegPtr(state, reg_idx) = val; }

FORCE_INLINE void SetReg8(EmuState* state, uint8_t reg_idx, uint8_t val) {
    uint32_t* rptr = GetRegPtr(state, reg_idx & 3);
    if (reg_idx < 4)
        *rptr = (*rptr & 0xFFFFFF00) | val;
    else
        *rptr = (*rptr & 0xFFFF00FF) | (val << 8);
}

FORCE_INLINE uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4)
        return val & 0xFF;
    else
        return (val >> 8) & 0xFF;
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
FORCE_INLINE bool PeekPFNoUpdate(uint64_t flags_cache);
FORCE_INLINE bool ResolvePF(uint64_t& flags_cache);

FORCE_INLINE uint64_t GetStateFlagsCache(const EmuState* state) { return state->ctx.flags_state; }

FORCE_INLINE void SetStateFlagsCache(EmuState* state, uint64_t flags_cache) { state->ctx.flags_state = flags_cache; }

FORCE_INLINE uint32_t GetArchitecturalEflags(const EmuState* state) {
    uint64_t flags_cache = GetStateFlagsCache(state);
    ResolvePF(flags_cache);
    return GetFlags32(flags_cache);
}

FORCE_INLINE void SetArchitecturalEflags(EmuState* state, uint32_t eflags) {
    SetStateFlagsCache(state, InitFlagsCache(eflags));
}

FORCE_INLINE uint32_t GetFlags32(uint64_t flags_cache) { return static_cast<uint32_t>(flags_cache); }

FORCE_INLINE uint32_t GetFlags32NoPF(uint64_t flags_cache) { return static_cast<uint32_t>(flags_cache); }

FORCE_INLINE void SetFlags32(uint64_t& flags_cache, uint32_t eflags) {
    flags_cache = (flags_cache & ~0xFFFFFFFFull) | eflags;
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
FORCE_INLINE bool ReadCF(uint64_t flags_cache) { return TestFlagBits(flags_cache, CF_MASK); }
FORCE_INLINE bool ReadZF(uint64_t flags_cache) { return TestFlagBits(flags_cache, ZF_MASK); }
FORCE_INLINE bool ReadSF(uint64_t flags_cache) { return TestFlagBits(flags_cache, SF_MASK); }
FORCE_INLINE bool ReadOF(uint64_t flags_cache) { return TestFlagBits(flags_cache, OF_MASK); }
FORCE_INLINE bool ReadAF(uint64_t flags_cache) { return TestFlagBits(flags_cache, AF_MASK); }

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
        return PeekPFNoUpdate(flags_cache);  // JP/JPE
    else if constexpr (Cond == 11)
        return !PeekPFNoUpdate(flags_cache);  // JNP/JPO
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
    if ((cond & 0xF) == 10) return PeekPFNoUpdate(flags_cache);
    if ((cond & 0xF) == 11) return !PeekPFNoUpdate(flags_cache);

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

FORCE_INLINE uint32_t GetSegmentBase(EmuState* state, const DecodedOp* op) {
    uint8_t seg = op->prefixes.flags.segment;
    // 1=ES, 2=CS, 3=SS, 4=DS, 5=FS, 6=GS
    if (seg >= 5) {
        return state->ctx.seg_base[seg];
    }
    return 0;
}

FORCE_INLINE uint32_t ComputeEA(EmuState* state, const DecodedOp* op) {
    // Computes Effective Address (no segment base)
    // Branchless calculation using pre-calculated offsets to registers (or zero register)
    const auto* ext = GetExt(op);
    const uintptr_t regs_base = (uintptr_t)state->ctx.regs;
    uint32_t base = *(const uint32_t*)(regs_base + ext->data.base_offset);
    uint32_t index = *(const uint32_t*)(regs_base + ext->data.index_offset);

    // index is usually 0 if no index (pointing to zero reg)
    // base is usually 0 if no base (pointing to zero reg)
    return base + (index << ext->data.scale) + ext->data.disp;
}

FORCE_INLINE uint32_t ComputeLinearAddress(EmuState* state, const DecodedOp* op) {
    uint32_t ea = ComputeEA(state, op);
    return ea + GetSegmentBase(state, op);
}

// ------------------------------------------------------------------------------------------------
// Internal Helpers (Register Access & Memory Splits)
// ------------------------------------------------------------------------------------------------

// Helper: Handle Register Reads for various sizes
template <typename T>
inline T ReadRegGeneric(EmuState* state, uint8_t rm) {
    if constexpr (std::is_same_v<T, simde__m128>) {
        // 128-bit XMM access
        return state->ctx.xmm[rm];
    } else {
        // General Purpose Registers (8, 16, 32-bit)
        uint32_t val = GetReg(state, rm & (std::is_same_v<T, uint8_t> ? 3 : 7));

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
inline void WriteRegGeneric(EmuState* state, uint8_t rm, T val) {
    if constexpr (std::is_same_v<T, simde__m128>) {
        // 128-bit XMM write
        state->ctx.xmm[rm] = val;
    } else {
        // General Purpose Registers
        if constexpr (std::is_same_v<T, uint32_t>) {
            // 32-bit: Direct overwrite
            SetReg(state, rm, val);
        } else {
            // 8-bit and 16-bit: Read-Modify-Write to preserve other bits
            // Note: For 8-bit, index logic differs (rm & 3)
            uint32_t regIdx = std::is_same_v<T, uint8_t> ? (rm & 3) : rm;
            uint32_t* rptr = GetRegPtr(state, regIdx);
            uint32_t curr = *rptr;

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
            *rptr = curr;
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
template <typename T, OpOnTLBMiss Strategy>
FORCE_INLINE MemResult<T> ReadModRM(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register Operand
        return ReadRegGeneric<T>(state, rm);
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, op);

        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.read<T, false>(addr, utlb, op);
        } else {
            // Restart or Retry (though Retry for Read is unusual, treating as Restart)
            auto value = state->mmu.read<T, true>(addr, utlb, op);
            if (!value) {
                // For Read, we always need to check pending if we want to restart
                // If usage suggests Retry for Read, it likely implies prefetch which we don't support explicitly yet.
                // Assuming Restart semantics for both Restart and Retry enums for Read to be safe.
                value = state->request_read_and_check_pending<T>(addr, op->next_eip);
            }
            return value;
        }
    }
}

/**
 * Writes a value to a Register or Memory based on the ModRM byte.
 * Supports uint8_t, uint16_t, uint32_t, and simde__m128.
 */
template <typename T, OpOnTLBMiss Strategy>
FORCE_INLINE MemResult<void> WriteModRM(EmuState* state, DecodedOp* op, T val, mem::MicroTLB* utlb) {
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    if (mod == 3) {
        // Register Operand
        WriteRegGeneric<T>(state, rm, val);
        return {};
    } else {
        // Memory Operand
        uint32_t addr = ComputeLinearAddress(state, op);

        if constexpr (Strategy == OpOnTLBMiss::Blocking) {
            return state->mmu.write<T, false>(addr, val, utlb, op);
        } else {
            auto result = state->mmu.write<T, true>(addr, val, utlb, op);
            if (!result) {
                if constexpr (Strategy == OpOnTLBMiss::Restart) {
                    result = state->request_write_and_check_pending<T>(addr, val, op->next_eip);
                } else {
                    // Retry
                    result = state->request_write_only<T>(addr, val, op->next_eip);
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
inline MemResult<T> ReadMem(EmuState* state, uint32_t addr, mem::MicroTLB* utlb, const DecodedOp* op) {
    if constexpr (Strategy == OpOnTLBMiss::Blocking) {
        return state->mmu.read<T, false>(addr, utlb, op);
    } else {
        auto value = state->mmu.read<T, true>(addr, utlb, op);
        if (!value) {
            value = state->request_read_and_check_pending<T>(addr, op->next_eip);
        }
        return value;
    }
}

/**
 * Generic Helper for Direct Memory Write
 */
template <typename T, OpOnTLBMiss Strategy>
FORCE_INLINE MemResult<void> WriteMem(EmuState* state, uint32_t addr, T val, mem::MicroTLB* utlb, const DecodedOp* op) {
    if constexpr (Strategy == OpOnTLBMiss::Blocking) {
        return state->mmu.write<T, false>(addr, val, utlb, op);
    } else {
        auto result = state->mmu.write<T, true>(addr, val, utlb, op);
        if (!result) {
            if constexpr (Strategy == OpOnTLBMiss::Restart) {
                result = state->request_write_and_check_pending<T>(addr, val, op->next_eip);
            } else {
                // Retry
                result = state->request_write_only<T>(addr, val, op->next_eip);
            }
        }
        return result;
    }
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Refactored)
// ------------------------------------------------------------------------------------------------

template <typename T, bool fail_on_tlb_miss = false>
FORCE_INLINE MemResult<void> Push(EmuState* state, T val, mem::MicroTLB* utlb, DecodedOp* op) {
    constexpr uint32_t size = sizeof(T);
    uint32_t esp = GetReg(state, ESP);

    // Write to memory (ESP - size)
    auto res = state->mmu.write<T, fail_on_tlb_miss>(esp - size, val, utlb, op);

    // Request a write and return
    if (!res && fail_on_tlb_miss) {
        // Push can satisfy "Retry" semantics (update ESP only on success)
        res = state->request_write_and_check_pending<T>(esp - size, val, op->next_eip);
    }

    if (res) {
        // Update ESP only if memory write succeeded
        SetReg(state, ESP, esp - size);
    }
    return res;
}

template <typename T, bool fail_on_tlb_miss = false>
FORCE_INLINE MemResult<T> Pop(EmuState* state, mem::MicroTLB* utlb, DecodedOp* op) {
    constexpr uint32_t size = sizeof(T);
    uint32_t esp = GetReg(state, ESP);

    // Read from memory (ESP)
    auto res = state->mmu.read<T, fail_on_tlb_miss>(esp, utlb, op);
    if (!res && fail_on_tlb_miss) {
        res = state->request_read_and_check_pending<T>(esp, op->next_eip);
    }

    if (res) {
        // Update ESP only if memory read succeeded
        SetReg(state, ESP, esp + size);
    }
    return res;
}

// Explicit aliases for compatibility
// Default to fail_on_tlb_miss = true for simple usage (ops_control)
FORCE_INLINE MemResult<void> Push16(EmuState* s, uint16_t v, mem::MicroTLB* u, DecodedOp* o) {
    return Push<uint16_t, true>(s, v, u, o);
}
FORCE_INLINE MemResult<void> Push32(EmuState* s, uint32_t v, mem::MicroTLB* u, DecodedOp* o) {
    return Push<uint32_t, true>(s, v, u, o);
}
FORCE_INLINE MemResult<uint16_t> Pop16(EmuState* s, mem::MicroTLB* u, DecodedOp* o) {
    return Pop<uint16_t, true>(s, u, o);
}
FORCE_INLINE MemResult<uint32_t> Pop32(EmuState* s, mem::MicroTLB* u, DecodedOp* o) {
    return Pop<uint32_t, true>(s, u, o);
}

// ------------------------------------------------------------------------------------------------
// ALU Operations & Flags
// ------------------------------------------------------------------------------------------------

// Use compiler builtins where possible
#if defined(__GNUC__) || defined(__clang__)
#define FIBER_POPCOUNT32(x) __builtin_popcount(x)
#define FIBER_PARITY32(x) __builtin_parity(x)
#elif defined(_MSC_VER)
#include <intrin.h>
#define FIBER_POPCOUNT32(x) __popcnt(x)
// MSVC doesn't have a direct parity builtin, use popcount & 1
#define FIBER_PARITY32(x) (__popcnt(x) & 1)
#else
// Fallback
#define FIBER_POPCOUNT32(x) std::bitset<32>(x).count()
#define FIBER_PARITY32(x) (FIBER_POPCOUNT32(x) & 1)
#endif

// 256-entry Parity Lookup Table (1 = Odd Parity i.e. odd number of 1s)
static const uint8_t g_ParityLUT[256] = {
    1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1,
    0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0,
    0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0,
    1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1,
    0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1,
    0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1,
    1, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1, 0, 0, 1,
};

// Returns true if PF should be set (even number of 1s in low 8 bits)
inline bool CalcPflag(uint8_t res_byte) {
#if defined(__x86_64__) || defined(_M_X64) || defined(__i386__) || defined(_M_IX86)
    // On x86, we can use builtins which map to efficient instructions or use a small sequence
    // __builtin_parity returns 1 for odd, 0 for even.
    // PF wants 1 for even, 0 for odd.
    return !FIBER_PARITY32(res_byte);
#else
    // On non-x86 (like ARM64), use LUT to avoid bit-twiddling overhead
    return g_ParityLUT[res_byte];
#endif
}

// Backward compatibility for other ops files
inline uint8_t Parity(uint8_t v) { return CalcPflag(v) ? 1 : 0; }

FORCE_INLINE bool CalcPFlagsFastPath(uint8_t pf_state) {
    return pf_state < 2 ? static_cast<bool>(~pf_state & 1) : CalcPflag(pf_state);
}

FORCE_INLINE void SetParityState(uint64_t& flags_cache, uint8_t res_byte) {
    uint8_t pf_state = res_byte;
    if (res_byte == 0) {
        pf_state = FLAGS_CACHE_PF_KNOWN_TRUE;
    } else if (res_byte == 1) {
        pf_state = FLAGS_CACHE_PF_KNOWN_FALSE;
    }
    flags_cache &= ~FLAGS_CACHE_PF_STATE_MASK;
    flags_cache |= (static_cast<uint64_t>(pf_state) << FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE uint8_t PeekPFState(uint64_t flags_cache) {
    return static_cast<uint8_t>((flags_cache & FLAGS_CACHE_PF_STATE_MASK) >> FLAGS_CACHE_PF_STATE_SHIFT);
}

FORCE_INLINE bool IsKnownPFState(uint8_t pf_state) {
    return pf_state == FLAGS_CACHE_PF_KNOWN_TRUE || pf_state == FLAGS_CACHE_PF_KNOWN_FALSE;
}

FORCE_INLINE bool PeekPFNoUpdate(uint64_t flags_cache) {
    const uint8_t pf_state = PeekPFState(flags_cache);
    return CalcPFlagsFastPath(pf_state);
}

FORCE_INLINE void CommitFlagsCache(EmuState* state, uint64_t& flags_cache) {
    ResolvePF(flags_cache);
    SetStateFlagsCache(state, flags_cache);
}

FORCE_INLINE bool ResolvePF(uint64_t& flags_cache) {
    const uint8_t pf_state = PeekPFState(flags_cache);
    const bool pf = CalcPFlagsFastPath(pf_state);
    SetFlags32(flags_cache, pf ? (GetFlags32(flags_cache) | PF_MASK) : (GetFlags32(flags_cache) & ~PF_MASK));
    flags_cache &= ~FLAGS_CACHE_PF_STATE_MASK;
    flags_cache |= (static_cast<uint64_t>(EncodeKnownParityState(pf)) << FLAGS_CACHE_PF_STATE_SHIFT);
    return pf;
}

template <typename T, bool UpdateFlags = true>
inline T AluAdd(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest + src;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        // ZF: Zero Flag
        if (res == 0) flags |= ZF_MASK;

        // SF: Sign Flag (MSB)
        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        // CF: Unsigned Overflow (Carry)
        // res < dest implies overflow for ADD
        if (res < dest) flags |= CF_MASK;

        // OF: Signed Overflow
        // (dest^src) >= 0 (same sign) AND (dest^res) < 0 (sign changed)
        // Optimization: ((dest ^ src ^ -1) & (dest ^ res)) & sign_mask
        T sign_mask = (T)1 << msb_idx;
        if (!((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        if (((dest ^ src ^ res) & AF_MASK) != 0) flags |= AF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSub(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest - src;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        // CF: Borrow
        if (dest < src) flags |= CF_MASK;

        // OF: Signed Overflow
        // (dest^src) < 0 (diff sign) AND (dest^res) < 0 (sign flipped from dest)
        // equivalent: ((dest ^ src) & (dest ^ res)) & sign_mask
        T sign_mask = (T)1 << msb_idx;
        if (((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        if (((dest ^ src ^ res) & AF_MASK) != 0) flags |= AF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluAdc(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    uint32_t cf_in = (GetFlags32(flags_cache) & CF_MASK);

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest + wsrc + cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        // CF
        if (wres >> (sizeof(T) * 8)) flags |= CF_MASK;

        // OF: Signed Overflow
        T sign_mask = (T)1 << msb_idx;
        if (!((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        if (((dest ^ src ^ res) & AF_MASK) != 0) flags |= AF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSbb(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    uint32_t cf_in = (GetFlags32(flags_cache) & CF_MASK);

    using UT = std::make_unsigned_t<T>;
    unsigned long long wdest = (UT)dest;
    unsigned long long wsrc = (UT)src;
    unsigned long long wres = wdest - wsrc - cf_in;

    T res = (T)wres;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;

        uint32_t msb_idx = sizeof(T) * 8 - 1;
        if ((res >> msb_idx) & 1) flags |= SF_MASK;

        // CF: Borrow
        if (wdest < (wsrc + cf_in)) flags |= CF_MASK;

        // OF: Signed Overflow
        T sign_mask = (T)1 << msb_idx;
        if (((dest ^ src) & sign_mask) && ((dest ^ res) & sign_mask)) flags |= OF_MASK;

        if (((dest ^ src ^ res) & AF_MASK) != 0) flags |= AF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluAnd(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest & src;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluOr(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest | src;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluXor(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    T res = dest ^ src;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);

        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

// ------------------------------------------------------------------------------------------------
// Shift / Rotate
// ------------------------------------------------------------------------------------------------

template <typename T, bool UpdateFlags = true>
inline T AluShl(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // CF: Last bit shifted out is bit (sizeof(T)*8 - count) of original
    uint32_t width = sizeof(T) * 8;
    bool cf = (dest >> (width - count)) & 1;

    T res = dest << count;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (width - 1)) & 1) flags |= SF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ CF
        if (count == 1) {
            bool msb_res = (res >> (width - 1)) & 1;
            if (msb_res != cf) flags |= OF_MASK;
        }

        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluShr(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // CF: Last bit shifted out is bit (count-1)
    bool cf = (dest >> (count - 1)) & 1;

    T res = dest >> count;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Original)
        if (count == 1) {
            if ((dest >> (sizeof(T) * 8 - 1)) & 1) flags |= OF_MASK;
        }

        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluSar(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    count &= 0x1F;
    if (count == 0) return dest;

    // Arithmetic Shift
    using ST = std::make_signed_t<T>;
    ST sdest = (ST)dest;
    ST sres = sdest >> count;
    T res = (T)sres;

    // CF: Last bit shifted out
    bool cf = (dest >> (count - 1)) & 1;

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32(flags_cache) & ~(CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK);
        if (res == 0) flags |= ZF_MASK;
        if ((res >> (sizeof(T) * 8 - 1)) & 1) flags |= SF_MASK;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = 0
        if (count == 1) {
            // Clear OF (already cleared)
        }

        SetFlags32(flags_cache, flags);
        SetParityState(flags_cache, static_cast<uint8_t>(res));
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRol(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;

    T res = (dest << count) | (dest >> (width - count));

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32NoPF(flags_cache) & ~(CF_MASK | OF_MASK);
        // SF/ZF/PF/AF unaffected

        // CF: LSB of result
        bool cf = (res & 1);
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ CF
        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            if (msb != cf) flags |= OF_MASK;
        }

        SetFlags32(flags_cache, (GetFlags32NoPF(flags_cache) & (PF_MASK | AF_MASK | ZF_MASK | SF_MASK)) | flags);
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRor(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    if (count == 0) return dest;
    uint32_t width = sizeof(T) * 8;
    count &= 0x1F;
    count %= width;
    if (count == 0) return dest;

    T res = (dest >> count) | (dest << (width - count));

    if constexpr (UpdateFlags) {
        uint32_t flags = GetFlags32NoPF(flags_cache) & ~(CF_MASK | OF_MASK);

        // CF: MSB of result
        bool cf = (res >> (width - 1)) & 1;
        if (cf) flags |= CF_MASK;

        // OF: For 1-bit, OF = MSB(Res) ^ MSB-1(Res)
        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            if (msb != smsb) flags |= OF_MASK;
        }

        SetFlags32(flags_cache, (GetFlags32NoPF(flags_cache) & (PF_MASK | AF_MASK | ZF_MASK | SF_MASK)) | flags);
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRcl(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    count %= (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (GetFlags32(flags_cache) & CF_MASK) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val << count) | (val >> (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        if (new_cf)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);

        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            if (msb != new_cf)
                SetFlagBits(flags_cache, OF_MASK);
            else
                ClearFlagBits(flags_cache, OF_MASK);
        }
    }
    return res;
}

template <typename T, bool UpdateFlags = true>
inline T AluRcr(EmuState* state, uint64_t& flags_cache, T dest, uint8_t count) {
    uint32_t width = sizeof(T) * 8;
    count %= (width + 1);
    if (count == 0) return dest;

    uint64_t val = (uint64_t)(std::make_unsigned_t<T>)dest;
    if (GetFlags32(flags_cache) & CF_MASK) val |= (1ULL << width);

    uint64_t mask = (1ULL << (width + 1)) - 1;
    uint64_t res64 = ((val >> count) | (val << (width + 1 - count))) & mask;

    T res = (T)res64;
    bool new_cf = (res64 >> width) & 1;

    if constexpr (UpdateFlags) {
        if (new_cf)
            SetFlagBits(flags_cache, CF_MASK);
        else
            ClearFlagBits(flags_cache, CF_MASK);

        if (count == 1) {
            bool msb = (res >> (width - 1)) & 1;
            bool smsb = (res >> (width - 2)) & 1;
            if (msb != smsb)
                SetFlagBits(flags_cache, OF_MASK);
            else
                ClearFlagBits(flags_cache, OF_MASK);
        }
    }
    return res;
}

template <typename T>
inline void AluCmp(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    AluSub<T, true>(state, flags_cache, dest, src);
}

template <typename T>
inline void AluTest(EmuState* state, uint64_t& flags_cache, T dest, T src) {
    AluAnd<T, true>(state, flags_cache, dest, src);
}

}  // namespace fiberish
