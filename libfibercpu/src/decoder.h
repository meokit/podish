#pragma once
#include <bit>
#include <cstddef>
#include <cstdint>
#include <vector>
#include "common.h"
#include "mem/tlb.h"

#if __has_cpp_attribute(clang::musttail)
#define ATTR_MUSTTAIL [[clang::musttail]]
#elif __has_cpp_attribute(gnu::musttail)
#define ATTR_MUSTTAIL [[gnu::musttail]]
#elif __has_cpp_attribute(msvc::musttail)
#define ATTR_MUSTTAIL [[msvc::musttail]]
#else
#define ATTR_MUSTTAIL
#endif

#if __has_cpp_attribute(preserve_none)
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#else
#define ATTR_PRESERVE_NONE
#endif

#if defined(_MSC_VER)
#define FORCE_INLINE __forceinline
#elif defined(__GNUC__) || defined(__clang__)
#define FORCE_INLINE __attribute__((always_inline)) inline
#else
#define FORCE_INLINE inline
#endif

#if defined(_MSC_VER)
#define RESTRICT __restrict
#elif defined(__GNUC__) || defined(__clang__)
#define RESTRICT __restrict__
#else
#define RESTRICT
#endif

namespace fiberish {
struct EmuState;
struct DecodedOp;

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTLB utlb, uint32_t branch, uint64_t flags_cache);

enum class LogicFlow : uint8_t {
    Continue = 0,
    ContinueSkipOne = 1,
    ExitOnCurrentEIP = 2,
    ExitOnNextEIP = 3,
    RestartMemoryOp = 4,
    RetryMemoryOp = 5,
    ExitToBranch = 6,
};

#define LogicFuncParams                                                                                    \
    EmuState *RESTRICT state, DecodedOp *RESTRICT op, mem::MicroTLB *utlb, uint32_t imm, uint32_t *branch, \
        uint64_t &flags_cache
#define LogicPassParams state, op, utlb, imm, branch, flags_cache

// Logic Function (Standard ABI, implementation)
// It may modify op and next_handler to control flow!
using LogicFunc = LogicFlow (*)(LogicFuncParams);  // Always inlined, no restrict needed

struct BasicBlock;

enum class BlockTerminalKind : uint8_t {
    None = 0,
    DirectJmpRel,
    DirectJccRel,
    OtherControlFlow,
};

namespace prefix {
constexpr uint8_t LOCK = 1 << 0;
constexpr uint8_t REP = 1 << 1;
constexpr uint8_t REPNE = 1 << 2;
constexpr uint8_t OPSIZE = 1 << 6;
constexpr uint8_t ADDRSIZE = 1 << 7;
}  // namespace prefix

constexpr uint8_t kNoRegOffset = 32;

union Prefixes {
    uint8_t all;
    struct {
        uint8_t lock : 1;
        uint8_t rep : 1;
        uint8_t repne : 1;
        uint8_t segment : 3;
        uint8_t opsize : 1;
        uint8_t addrsize : 1;
    } flags;
};

union Meta {
    uint8_t all;
    struct {
        uint8_t has_modrm : 1;
        uint8_t has_mem : 1;
        uint8_t has_imm : 1;
        uint8_t is_control_flow : 1;
        uint8_t no_flags : 1;
        uint8_t reserved0 : 1;
        uint8_t ext_kind : 2;
    } flags;
};

enum class ExtKind : uint8_t {
    Data = 0,
    Link = 1,
    ControlFlow = 2,
};

struct DecodedMemData {
    uint32_t imm = 0;
    uint32_t ea_desc = 0;
    uint32_t disp = 0;
    uint32_t reserved = 0;
};

namespace memdesc {
constexpr uint32_t kOffsetMask = 0x3f;
constexpr uint32_t kBaseShift = 0;
constexpr uint32_t kIndexShift = 6;
constexpr uint32_t kScaleShift = 12;
constexpr uint32_t kSegmentShift = 14;

FORCE_INLINE constexpr uint32_t PackEA(uint8_t base_offset, uint8_t index_offset, uint8_t scale, uint8_t segment) {
    return (static_cast<uint32_t>(base_offset) << kBaseShift) | (static_cast<uint32_t>(index_offset) << kIndexShift) |
           (static_cast<uint32_t>(scale) << kScaleShift) | (static_cast<uint32_t>(segment) << kSegmentShift);
}

FORCE_INLINE constexpr uint8_t BaseOffset(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kBaseShift) & kOffsetMask);
}

FORCE_INLINE constexpr uint8_t IndexOffset(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kIndexShift) & kOffsetMask);
}

FORCE_INLINE constexpr uint8_t Scale(uint32_t ea_desc) { return static_cast<uint8_t>((ea_desc >> kScaleShift) & 0x3); }

FORCE_INLINE constexpr uint8_t Segment(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kSegmentShift) & 0x7);
}
}  // namespace memdesc

struct DecodedControlFlowData {
    uint32_t imm = 0;
    uint32_t target_eip = 0;
    BasicBlock* cached_target = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t cached_target_padding;
#endif
};

struct alignas(16) DecodedOp {
    HandlerFunc handler;
#if INTPTR_MAX == INT32_MAX
    uint32_t handler_padding;
#endif
    uint32_t next_eip;
    uint8_t len;
    uint8_t modrm;
    Prefixes prefixes;
    Meta meta;
    union {
        DecodedMemData data;
        struct {
            uint64_t reserved;
            BasicBlock* next_block;
#if INTPTR_MAX == INT32_MAX
            uint32_t next_block_padding;
#endif
        } link;
        DecodedControlFlowData control;
    } ext;

    uint8_t GetLength() const { return len; }
    void SetLength(uint8_t l) { len = l; }
};

struct alignas(16) DecodedInstTmp {
    DecodedOp head{};
};

static_assert(sizeof(DecodedOp) == 32, "DecodedOp must be exactly 32 bytes");
static_assert(sizeof(DecodedMemData) == 16, "DecodedMemData must be exactly 16 bytes");
static_assert(sizeof(DecodedInstTmp) == 32, "DecodedInstTmp must be exactly 32 bytes");
static_assert(offsetof(DecodedOp, handler) == 0, "DecodedOp: handler must start at offset 0");
static_assert(offsetof(DecodedOp, ext) == 16, "DecodedOp: ext must start at offset 16");

static_assert(sizeof(DecodedControlFlowData) == 16, "DecodedControlFlowData must be exactly 16 bytes");
static_assert(offsetof(DecodedOp, next_eip) == 8, "DecodedOp: next_eip must start at offset 8");
static_assert(offsetof(DecodedOp, meta) == 15, "DecodedOp: meta must start at offset 15");

template <typename OpT>
FORCE_INLINE bool HasExt(const OpT* op) {
    (void)op;
    return true;
}

template <typename OpT>
FORCE_INLINE ExtKind GetExtKind(const OpT* op) {
    return static_cast<ExtKind>(op->meta.flags.ext_kind);
}

template <typename OpT>
FORCE_INLINE void SetExtKind(OpT* op, ExtKind kind) {
    op->meta.flags.ext_kind = static_cast<uint8_t>(kind);
}

template <typename OpT>
FORCE_INLINE bool HasMem(const OpT* op) {
    return op->meta.flags.has_mem;
}

template <typename OpT>
FORCE_INLINE bool HasImm(const OpT* op) {
    return op->meta.flags.has_imm;
}

template <typename OpT>
FORCE_INLINE const auto* GetExt(const OpT* op) {
    return &op->ext;
}

template <typename OpT>
FORCE_INLINE auto* GetExt(OpT* op) {
    return &op->ext;
}

template <typename OpT>
FORCE_INLINE uint32_t GetImm(const OpT* op) {
    if (!HasImm(op)) return 0;
    return GetExt(op)->data.imm;
}

FORCE_INLINE const DecodedOp* NextOp(const DecodedOp* op) { return op + 1; }

FORCE_INLINE DecodedOp* NextOp(DecodedOp* op) { return op + 1; }

template <typename OpT>
FORCE_INLINE BasicBlock* GetNextBlock(const OpT* op) {
    return GetExt(op)->link.next_block;
}

template <typename OpT>
FORCE_INLINE void SetNextBlock(OpT* op, BasicBlock* block) {
    SetExtKind(op, ExtKind::Link);
    GetExt(op)->link.next_block = block;
}

template <typename OpT>
FORCE_INLINE BasicBlock* GetCachedTarget(const OpT* op) {
    return GetExt(op)->control.cached_target;
}

template <typename OpT>
FORCE_INLINE uint32_t GetControlTargetEip(const OpT* op) {
    return GetExt(op)->control.target_eip;
}

template <typename OpT>
FORCE_INLINE void SetControlTargetEip(OpT* op, uint32_t target_eip) {
    SetExtKind(op, ExtKind::ControlFlow);
    GetExt(op)->control.target_eip = target_eip;
}

template <typename OpT>
FORCE_INLINE void SetCachedTarget(OpT* op, BasicBlock* block) {
    SetExtKind(op, ExtKind::ControlFlow);
    GetExt(op)->control.cached_target = block;
}

struct BasicBlockChainPrefix {
    uint64_t start_eip : 32;
    uint64_t inst_count : 8;
    uint64_t reserved : 24;
};

struct alignas(16) BasicBlock {
    BasicBlockChainPrefix chain;
    HandlerFunc entry = nullptr;
    uint32_t end_eip;
    uint32_t slot_count;           // Total decoded ops including sentinel
    uint32_t sentinel_slot_index;  // Index where sentinel starts
    uint32_t branch_target_eip = 0;
    uint32_t fallthrough_eip = 0;
    uint8_t terminal_kind_raw = 0;
    uint8_t block_padding0 = 0;
    uint16_t block_padding1 = 0;
    uint64_t exec_count = 0;  // Number of times block was executed

    // Flexible Array Member - fixed-size decoded op stream.
    alignas(16) std::byte slots[sizeof(DecodedOp)];

    static size_t CalculateSize(size_t slot_count) {
        if (slot_count == 0) return sizeof(BasicBlock);
        return offsetof(BasicBlock, slots) + sizeof(DecodedOp) * slot_count;
    }

    DecodedOp* FirstOp() { return reinterpret_cast<DecodedOp*>(slots); }
    const DecodedOp* FirstOp() const { return reinterpret_cast<const DecodedOp*>(slots); }

    DecodedOp* Sentinel() { return reinterpret_cast<DecodedOp*>(slots + sentinel_slot_index * sizeof(DecodedOp)); }
    const DecodedOp* Sentinel() const {
        return reinterpret_cast<const DecodedOp*>(slots + sentinel_slot_index * sizeof(DecodedOp));
    }

    BlockTerminalKind terminal_kind() const { return static_cast<BlockTerminalKind>(terminal_kind_raw); }
    void set_terminal_kind(BlockTerminalKind kind) { terminal_kind_raw = static_cast<uint8_t>(kind); }

    static constexpr uint32_t kInvalidStartEipBit = 0x80000000u;

    uint32_t start_eip() const { return static_cast<uint32_t>(chain.start_eip); }
    void set_start_eip(uint32_t start_eip) { chain.start_eip = start_eip; }
    bool is_valid() const { return (start_eip() & kInvalidStartEipBit) == 0; }
    uint32_t canonical_start_eip() const { return start_eip() & ~kInvalidStartEipBit; }

    uint32_t inst_count() const { return chain.inst_count; }
    void set_inst_count(uint32_t count) { chain.inst_count = static_cast<uint8_t>(count); }

    bool MatchesChainTarget(uint32_t target_eip) const { return start_eip() == target_eip; }

    // Mark block as invalid
    void Invalidate();
    void Revalidate();
};

static_assert(offsetof(BasicBlock, chain) == 0, "BasicBlock: chain must start at offset 0");
static_assert(offsetof(BasicBlock, entry) == 8, "BasicBlock: entry must start at offset 8");
static_assert(sizeof(BasicBlockChainPrefix) == 8, "BasicBlockChainPrefix must be exactly 8 bytes");

#if INTPTR_MAX == INT32_MAX
static_assert(offsetof(BasicBlock, slots) == 48, "BasicBlock: slots must start at offset 48 on 32-bit");
#else
static_assert(offsetof(BasicBlock, slots) == 48, "BasicBlock: slots must start at offset 48");
#endif

// Decoder Logic
bool DecodeInstruction(const uint8_t* code, DecodedInstTmp* inst, uint16_t* handler_index);

// Start EIP, Limit EIP, Max Instructions -> Returns Pointer to allocated block or nullptr
BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts,
                        BasicBlock* invalidated_candidate = nullptr);

}  // namespace fiberish
