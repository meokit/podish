#pragma once

#include <cstddef>
#include <cstdint>

namespace fiberish {

struct DecodedOp;
struct EmuState;
namespace mem {
struct MicroTLB;
}

enum class LogicFlow : uint8_t;

#if defined(_MSC_VER)
#define RESTRICT __restrict
#elif defined(__GNUC__) || defined(__clang__)
#define RESTRICT __restrict__
#else
#define RESTRICT
#endif

#define LogicFuncParams                                                                                    \
    EmuState *RESTRICT state, DecodedOp *RESTRICT op, mem::MicroTLB *utlb, uint32_t imm, uint32_t *branch, \
        uint64_t &flags_cache

#ifdef __clang__
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#else
#define ATTR_PRESERVE_NONE
#endif

using LogicFunc = LogicFlow (*)(LogicFuncParams);
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);

namespace jit {

ATTR_PRESERVE_NONE int64_t JitContinueTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                             mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);
ATTR_PRESERVE_NONE int64_t JitContinueSkipOneTarget(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                    int64_t instr_limit, mem::MicroTLB utlb, uint32_t branch,
                                                    uint64_t flags_cache);

enum class PatchKind : uint8_t {
    OpQword64,
};

struct PatchDesc {
    uint32_t offset;
    PatchKind kind;
    uint16_t aux;
};

struct BranchRelocDesc {
    uint32_t offset;
    uint16_t target_id;
};

struct StencilDesc {
    const uint8_t* code;
    uint32_t code_size;
    const PatchDesc* patches;
    uint16_t patch_count;
    const BranchRelocDesc* branch_relocs;
    uint16_t branch_reloc_count;
    uint16_t id;
    uint32_t flags;
};

// Magic base to easily grep in disassembly.
constexpr uint32_t PATCH_MAGIC_HIGH = 0xABCD;
constexpr uint32_t PATCH_MAGIC64_MID = 0xEF01;
constexpr uint32_t PATCH_MAGIC64_HIGH = 0x2345;

template <PatchKind Kind, uint8_t Aux = 0>
inline uint32_t PatchMagic32() {
    uint32_t val;
    constexpr uint32_t low16 = (static_cast<uint32_t>(Aux) << 8) | static_cast<uint32_t>(Kind);
    asm volatile(
        "movz %w0, %1 \n"
        "movk %w0, %2, lsl #16"
        : "=r"(val)
        : "n"(low16), "n"(PATCH_MAGIC_HIGH));
    return val;
}

template <PatchKind Kind, uint8_t Aux = 0>
inline uint8_t PatchMagic8() {
    return static_cast<uint8_t>(PatchMagic32<Kind, Aux>());
}

template <PatchKind Kind, uint8_t Aux = 0>
inline uint16_t PatchMagic16() {
    return static_cast<uint16_t>(PatchMagic32<Kind, Aux>());
}

template <PatchKind Kind, uint8_t Aux = 0>
inline uint64_t PatchMagic64() {
    uint64_t val;
    constexpr uint32_t low16 = (static_cast<uint32_t>(Aux) << 8) | static_cast<uint32_t>(Kind);
    asm volatile(
        "movz %x0, %1 \n"
        "movk %x0, %2, lsl #16 \n"
        "movk %x0, %3, lsl #32 \n"
        "movk %x0, %4, lsl #48"
        : "=r"(val)
        : "n"(low16), "n"(PATCH_MAGIC_HIGH), "n"(PATCH_MAGIC64_MID), "n"(PATCH_MAGIC64_HIGH));
    return val;
}

}  // namespace jit
}  // namespace fiberish
