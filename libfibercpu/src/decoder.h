#pragma once
#include <array>
#include <bit>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <span>
#include <type_traits>
#include <utility>
#include <vector>
#include "common.h"
#include "handler_abi.h"
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

// Probe the GNU spelling used below. Clang also recognizes the namespaced
// C++ attribute clang::preserve_none, but the unqualified C++ attribute probe
// silently selected the platform ABI and its nonvolatile register saves.
// Use the GNU probe on x64 and ARM64: Apple Clang supports preserve_none on
// AArch64 but reports 0 for the unqualified C++ attribute probe.
#if defined(__clang__) && (defined(__x86_64__) || defined(_M_X64) || defined(__aarch64__) || defined(_M_ARM64))
#if __has_attribute(preserve_none)
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#else
#define ATTR_PRESERVE_NONE
#endif
#elif __has_cpp_attribute(preserve_none)
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
struct GprCache;
struct DecodedOp;
struct DecodedInstruction;

// Handler Function (Preserve None ABI, functionality + dispatch)
using HandlerFunc = int64_t(ATTR_PRESERVE_NONE*)(EmuState* RESTRICT state, DecodedOp* RESTRICT op, int64_t instr_limit,
                                                 mem::MicroTlbAbiWord utlb_tags, mem::MicroTlbAbiWord utlb_addend,
                                                 uint32_t branch, uint64_t flags_cache FIBERCPU_PIN_PARAMS);

enum class LogicFlow : uint8_t {
    Continue = 0,
    ExitOnCurrentEIP = 2,
    ExitOnNextEIP = 3,
    RestartMemoryOp = 4,
    RetryMemoryOp = 5,
    ExitToBranch = 6,
    ExitToBranchSingleSlot,
};

#define LogicFuncParams                                                                                         \
    EmuState *RESTRICT state, DecodedOp *RESTRICT op, mem::MicroTLB *utlb, uint32_t imm, uint8_t operand_modrm, \
        uint32_t *branch, uint64_t &flags_cache, GprCache gpr
#define LogicPassParams state, op, utlb, imm, operand_modrm, branch, flags_cache, gpr

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
    static constexpr uint8_t has_modrm_mask = 1 << 0;
    static constexpr uint8_t has_mem_mask = 1 << 1;
    static constexpr uint8_t has_imm_mask = 1 << 2;
    static constexpr uint8_t no_flags_mask = 1 << 4;
    static constexpr uint8_t operand_lowered_mask = 1 << 5;

    uint8_t all;
    struct {
        uint8_t has_modrm : 1;
        uint8_t has_mem : 1;
        uint8_t has_imm : 1;
        uint8_t is_control_flow : 1;
        uint8_t no_flags : 1;
        uint8_t operand_lowered : 1;
        uint8_t ext_kind : 2;
    } flags;
};

enum class ExtKind : uint8_t {
    Data = 0,
    Link = 1,
    ControlFlow = 2,
};

struct DecodedMemData {
    uint32_t imm;
    uint32_t ea_desc;
    uint32_t disp;
    uint32_t reserved;
};

namespace memdesc {
constexpr uint32_t kOffsetMask = 0x3f;
constexpr uint32_t kBaseShift = 0;
constexpr uint32_t kIndexShift = 6;
constexpr uint32_t kScaleShift = 12;
constexpr uint32_t kSegmentShift = 14;
constexpr uint32_t kResolvedSegmentShift = 17;

FORCE_INLINE constexpr uint32_t PackEA(uint8_t base_offset, uint8_t index_offset, uint8_t scale, uint8_t segment) {
    // The base register and override are fixed for the lifetime of this decoded
    // operand. Resolve DS vs SS here, but still load the chosen base at runtime.
    // Keep the original override bits for specialization matching.
    const bool stack_base = base_offset == ESP * sizeof(uint32_t) || base_offset == EBP * sizeof(uint32_t);
    const uint8_t resolved_segment = segment != 0 ? segment : (stack_base ? 3 : 4);
    return (static_cast<uint32_t>(base_offset) << kBaseShift) | (static_cast<uint32_t>(index_offset) << kIndexShift) |
           (static_cast<uint32_t>(scale) << kScaleShift) | (static_cast<uint32_t>(segment) << kSegmentShift) |
           (static_cast<uint32_t>(resolved_segment) << kResolvedSegmentShift);
}

FORCE_INLINE constexpr uint8_t BaseOffset(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kBaseShift) & kOffsetMask);
}

FORCE_INLINE constexpr uint8_t IndexOffset(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kIndexShift) & kOffsetMask);
}

FORCE_INLINE constexpr uint8_t Scale(uint32_t ea_desc) { return static_cast<uint8_t>((ea_desc >> kScaleShift) & 0x3); }

FORCE_INLINE constexpr uint32_t CompactEA(uint32_t ea_desc) {
    const auto base = BaseOffset(ea_desc) / sizeof(uint32_t);
    const auto index = IndexOffset(ea_desc) / sizeof(uint32_t);
    return base | ((index == 8 ? ESP : index) << 4) | (Scale(ea_desc) << 7);
}

FORCE_INLINE constexpr uint8_t Segment(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kSegmentShift) & 0x7);
}

FORCE_INLINE constexpr uint8_t ResolvedSegment(uint32_t ea_desc) {
    return static_cast<uint8_t>((ea_desc >> kResolvedSegmentShift) & 0x7);
}
}  // namespace memdesc

struct BakedAddress {
    uint32_t descriptor;
    uint32_t displacement;
    bool address16;
    bool displacement_known = true;
};

enum class RelativeBranchWidth : uint8_t { None, Byte, Word, Dword };

struct DecodedControlFlowData {
    uint32_t imm;
    uint32_t target_eip;
    RelativeBranchWidth relative_width;
    std::byte reserved[7];
};

struct alignas(16) DecodedInstruction {
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

// Value-initialize individual decoded instructions with {}. Default
// initialization is deliberately trivial so unused block slots stay untouched.
struct alignas(16) DecodedInstTmp {
    DecodedInstruction head;
};
static_assert(std::is_trivially_default_constructible_v<DecodedInstTmp>);

static_assert(sizeof(DecodedInstruction) == 32);
static_assert(sizeof(DecodedMemData) == 16, "DecodedMemData must be exactly 16 bytes");
static_assert(sizeof(DecodedInstTmp) == 32, "DecodedInstTmp must be exactly 32 bytes");
static_assert(offsetof(DecodedInstruction, handler) == 0, "DecodedInstruction: handler must start at offset 0");
static_assert(offsetof(DecodedInstruction, ext) == 16, "DecodedInstruction: ext must start at offset 16");

static_assert(sizeof(DecodedControlFlowData) == 16, "DecodedControlFlowData must be exactly 16 bytes");
static_assert(offsetof(DecodedInstruction, next_eip) == 8, "DecodedInstruction: next_eip must start at offset 8");
static_assert(offsetof(DecodedInstruction, meta) == 15, "DecodedInstruction: meta must start at offset 15");

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

FORCE_INLINE uint32_t GetControlTargetEip(const DecodedInstruction* op) { return op->ext.control.target_eip; }

FORCE_INLINE uint32_t RelativeBranchTarget(uint32_t next_eip, uint32_t immediate, RelativeBranchWidth width) {
    switch (width) {
        case RelativeBranchWidth::Byte:
            return next_eip + static_cast<int8_t>(immediate);
        case RelativeBranchWidth::Word:
            return (next_eip + static_cast<int16_t>(immediate)) & 0xFFFFu;
        case RelativeBranchWidth::Dword:
            return next_eip + immediate;
        case RelativeBranchWidth::None:
            return 0;
    }
    return 0;
}

// Single-slot memory immediates. Keep small literals as their own codes and
// reserve the rest for constants from the CoreMark and Webpack profiles.
// These are raw x86 immediates: a sign-extended imm8 of -1 is encoded as 0xff.
namespace compact_imm {
constexpr uint8_t kCodeMask = 0x3f;
constexpr uint8_t kLastLiteral = 16;
constexpr std::array<uint32_t, kCodeMask - kLastLiteral> kConstants{
    0x12,  0x13,  0x18,  0x1c,   0x20,    0x25,      0x2c,       0x2f,       0x3b,       0x3d,       0x3e,       0x40,
    0x41,  0x42,  0x43,  0x5e,   0x60,    0x63,      0x66,       0x80,       0x85,       0xa4,       0xa9,       0xb5,
    0xc4,  0xff,  0x100, 0x102,  0x112,   0x13f,     0x280,      0x3e7,      0x400,      0x410,      0x41b,      0x41c,
    0x424, 0x444, 0xfff, 0xffff, 0x10000, 0x1000000, 0x7fffffff, 0x80000000, 0xcc9e2d51, 0xfffffffe, 0xffffffff,
};

constexpr uint8_t Encode(uint32_t immediate) {
    if (immediate <= kLastLiteral) return uint8_t(immediate);
    for (size_t i = 0; i < kConstants.size(); ++i) {
        if (kConstants[i] == immediate) return uint8_t(kLastLiteral + 1 + i);
    }
    return 0;
}

constexpr uint32_t Decode(uint8_t code) { return code <= kLastLiteral ? code : kConstants[code - kLastLiteral - 1]; }

constexpr bool NeedsSeparateSlot(bool memory, uint8_t prefixes, uint32_t immediate) {
    return memory && immediate && ((prefixes & kCodeMask) || !Encode(immediate));
}

FORCE_INLINE bool NeedsSeparateSlot(const DecodedInstruction& op) {
    return NeedsSeparateSlot(HasMem(&op), op.prefixes.all, GetImm(&op));
}

}  // namespace compact_imm

// Keep link offsets in eight-byte units on both 32- and 64-bit hosts.
constexpr bool IsLinkOnlyReturn(uint8_t metadata) {
    return ExtKind(metadata >> 6) == ExtKind::ControlFlow && !(metadata & Meta::has_imm_mask);
}

constexpr bool HasBranchLinkSlot(uint8_t metadata) {
    return ExtKind(metadata >> 6) == ExtKind::ControlFlow && (metadata & Meta::has_imm_mask);
}

constexpr bool HasExtraOperandSlot(uint8_t metadata, uint8_t prefixes, uint32_t immediate) {
    return HasBranchLinkSlot(metadata) ||
           compact_imm::NeedsSeparateSlot(metadata & Meta::has_mem_mask, prefixes, immediate);
}

struct alignas(16) DecodedOp {
    HandlerFunc handler;
#if INTPTR_MAX == INT32_MAX
    uint32_t handler_padding;
#endif
    uint64_t payload;

    // The fields shared by handlers have fixed offsets in all operand forms.
    // The primary value is an immediate, memory displacement or absolute
    // branch target. Memory implies ModRM, so its redundant bit marks an
    // immediate encoded in the otherwise zero low prefix bits. Other nonzero
    // memory immediates use an adjacent data slot. Cached control-flow
    // instructions with operands use the unused high opcode bits for their
    // tag and relative width. A terminal RET without operands stores only its
    // mutable block link in payload.
    static constexpr unsigned kModRMShift = 32;
    static constexpr unsigned kPrefixShift = 40;
    static constexpr unsigned kMetaShift = 48;
    static constexpr unsigned kAddressShift = 54;
    static constexpr unsigned kBranchWidthShift = kModRMShift + 3;
    static constexpr uint64_t kBranchTag = uint64_t{1} << (kModRMShift + 7);
    static constexpr uint64_t kHasMemory = uint64_t{2} << kMetaShift;
    static constexpr uint64_t kHasModRM = uint64_t{Meta::has_modrm_mask} << kMetaShift;
    static constexpr uint64_t kHasMemoryImmediate = uint64_t{1} << 63;

    static constexpr uint64_t PackOperandHeader(uint8_t modrm, uint8_t prefixes, uint8_t metadata) {
        return (uint64_t{modrm} << kModRMShift) | (uint64_t{prefixes} << kPrefixShift) |
               (uint64_t{metadata & 0x3fu} << kMetaShift);
    }

    static constexpr uint64_t PackMemoryPayload(uint64_t header, uint32_t ea, uint32_t displacement,
                                                uint32_t immediate) {
        const auto payload = header | (uint64_t{memdesc::CompactEA(ea)} << kAddressShift) | displacement;
        constexpr uint64_t prefix_mask = uint64_t{compact_imm::kCodeMask} << kPrefixShift;
        const auto code = (header & prefix_mask) ? 0 : compact_imm::Encode(immediate);
        if (code) return (payload & ~kHasModRM) | (uint64_t(code) << kPrefixShift);
        return payload | (immediate ? kHasMemoryImmediate : 0);
    }

    FORCE_INLINE uint32_t Primary() const { return uint32_t(payload); }
    FORCE_INLINE uint8_t RawPrefixes() const { return uint8_t(payload >> kPrefixShift); }
    FORCE_INLINE Prefixes GetPrefixes() const {
        return {uint8_t(RawPrefixes() & (HasCompactImmediate() ? ~compact_imm::kCodeMask : 0xff))};
    }
    FORCE_INLINE bool HasMemory() const { return payload & kHasMemory; }
    FORCE_INLINE bool HasMemoryImmediate() const { return payload & kHasMemoryImmediate; }
    FORCE_INLINE bool HasCompactImmediate() const { return (payload & (kHasMemory | kHasModRM)) == kHasMemory; }
    FORCE_INLINE bool HasBranchOperands() const {
        return (payload & (kBranchTag | kHasMemory | kHasModRM)) == kBranchTag;
    }
    FORCE_INLINE ExtKind GetExtKind() const {
        if (HasMemory()) return ExtKind::Data;
        if (HasBranchOperands()) return ExtKind::ControlFlow;
        return ExtKind((payload >> kAddressShift) & 3);
    }
    FORCE_INLINE Meta GetMeta() const {
        const uint8_t meta = (payload >> kMetaShift) & 0x3f;
        return {uint8_t(meta | ((meta >> 1) & Meta::has_modrm_mask) | (uint8_t(GetExtKind()) << 6))};
    }

    FORCE_INLINE unsigned AddressBase() const { return (payload >> kAddressShift) & 15; }
    // ESP has no SIB index encoding. In the compact EA it means no index.
    FORCE_INLINE unsigned AddressIndex() const { return (payload >> (kAddressShift + 4)) & 7; }
    FORCE_INLINE unsigned AddressScale() const { return (payload >> (kAddressShift + 7)) & 3; }
    FORCE_INLINE bool AddressIs16() const { return payload & (uint64_t{prefix::ADDRSIZE} << kPrefixShift); }
    FORCE_INLINE unsigned ResolvedMemorySegment() const {
        const unsigned segment = (payload >> (kPrefixShift + 3)) & 7;
        const unsigned default_segment = (AddressBase() & ~1u) == ESP ? 3 : 4;
        return segment == 0 || HasCompactImmediate() ? default_segment : segment;
    }
    FORCE_INLINE uint32_t AddressDescriptor() const {
        const auto index = AddressIndex();
        return memdesc::PackEA(AddressBase() * 4, (index == ESP ? 8 : index) * 4, AddressScale(),
                               GetPrefixes().flags.segment);
    }

    FORCE_INLINE RelativeBranchWidth BranchWidth() const {
        return RelativeBranchWidth((payload >> kBranchWidthShift) & 3);
    }
    FORCE_INLINE BasicBlock* LoadBlockLink() const {
        return reinterpret_cast<BasicBlock*>(static_cast<uintptr_t>(payload));
    }
    FORCE_INLINE void StoreBlockLink(BasicBlock* target) { payload = reinterpret_cast<uintptr_t>(target); }
    FORCE_INLINE void ClearOperandLowered() { payload &= ~(uint64_t{Meta::operand_lowered_mask} << kMetaShift); }
};

static_assert(sizeof(DecodedOp) == 16, "DecodedOp must be exactly 16 bytes");
static_assert(offsetof(DecodedOp, handler) == 0);
static_assert(offsetof(DecodedOp, payload) == 8);

ATTR_PRESERVE_NONE int64_t ForwardMemoryImmediateSlot(EmuState* RESTRICT state, DecodedOp* RESTRICT op,
                                                      int64_t instr_limit, mem::MicroTlbAbiWord utlb_tags,
                                                      mem::MicroTlbAbiWord utlb_addend, uint32_t branch,
                                                      uint64_t flags_cache FIBERCPU_PIN_PARAMS);

FORCE_INLINE const DecodedInstruction* NextOp(const DecodedInstruction* op) { return op + 1; }

FORCE_INLINE ExtKind GetExtKind(const DecodedOp* op) { return op->GetExtKind(); }
FORCE_INLINE bool HasMem(const DecodedOp* op) { return op->HasMemory(); }
FORCE_INLINE bool HasImm(const DecodedOp* op) { return op->GetMeta().flags.has_imm; }
FORCE_INLINE uint32_t GetSingleSlotMemoryImmediate(const DecodedOp* op) {
    if (!op->HasCompactImmediate()) return 0;
    return compact_imm::Decode(op->RawPrefixes() & compact_imm::kCodeMask);
}

// Relative control-flow handlers use GetControlTargetEip for their resolved
// target. ReadOp restores the original relative immediate for decoding tools.
FORCE_INLINE uint32_t GetImm(const DecodedOp* op) {
    if (op->HasMemoryImmediate()) [[unlikely]]
        return uint32_t((op + 1)->payload);
    if (!op->HasMemory()) return op->Primary();
    return GetSingleSlotMemoryImmediate(op);
}

FORCE_INLINE Prefixes GetPrefixes(const DecodedOp* op) { return op->GetPrefixes(); }
FORCE_INLINE Meta GetMeta(const DecodedOp* op) { return op->GetMeta(); }
FORCE_INLINE uint32_t GetAddressDescriptor(const DecodedOp* op) { return op->AddressDescriptor(); }
FORCE_INLINE uint32_t GetDisplacement(const DecodedOp* op) { return op->Primary(); }

FORCE_INLINE uint8_t GetModRM(const DecodedInstruction* op) { return op->modrm; }
FORCE_INLINE Prefixes GetPrefixes(const DecodedInstruction* op) { return op->prefixes; }
FORCE_INLINE Meta GetMeta(const DecodedInstruction* op) { return op->meta; }
FORCE_INLINE uint32_t GetAddressDescriptor(const DecodedInstruction* op) { return op->ext.data.ea_desc; }
FORCE_INLINE uint32_t GetDisplacement(const DecodedInstruction* op) { return op->ext.data.disp; }

FORCE_INLINE uint32_t GetNextEip(const EmuState* state, const DecodedOp* op);
FORCE_INLINE uint8_t GetOpLength(const EmuState* state, const DecodedOp* op);

enum class LLVMBlockEipField : uint8_t { Next, BranchTarget, Fallthrough };

#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
extern "C" [[gnu::const]] uint32_t FiberLLVMReadEip(const DecodedOp* op, uint32_t fallback, LLVMBlockEipField field);
#endif

template <LLVMBlockEipField Field>
FORCE_INLINE uint32_t BakeableBlockEip(const DecodedOp* op, uint32_t fallback) {
#ifdef FIBERCPU_LLVM_BLOCK_TEMPLATE
    return FiberLLVMReadEip(op, fallback, Field);
#else
    (void)op;
    return fallback;
#endif
}

FORCE_INLINE uint32_t GetControlTargetEip(const DecodedOp* op) {
    return BakeableBlockEip<LLVMBlockEipField::BranchTarget>(op, op->Primary());
}

struct BlockSlotLayout {
    uint8_t logical_slot_count;
    uint8_t anchor_count;
    uint8_t data_slot_count;

    constexpr size_t PhysicalSlotCount() const { return size_t(logical_slot_count) + data_slot_count; }
    static BlockSlotLayout Inspect(uint32_t start_eip, std::span<const DecodedInstruction> ops);
};

struct BlockDataSlotMask {
    uint64_t logical;
    uint64_t physical[2];
};

struct BasicBlockChainPrefix {
    uint64_t start_eip : 32;
    uint64_t inst_count : 8;
    uint64_t valid : 1;
    uint64_t weighted_profile : 1;
    uint64_t jit_execution_profile : 1;
    uint64_t reserved : 5;
    uint64_t llvm_hot_count : 16;
};

struct alignas(16) BasicBlock {
    BasicBlockChainPrefix chain{};
    HandlerFunc entry = nullptr;
#if INTPTR_MAX == INT32_MAX
    uint32_t entry_padding = 0;
#endif
    uint32_t end_eip;
    uint8_t slot_count;  // Physical slots, including data and sentinel.
    uint8_t sentinel_slot_index;
    uint8_t anchor_count;
    // LOCK is invalid for RET, leaving that bit free to mark a link-only tail.
    uint8_t link_only_return_prefixes = 0;
    uint32_t sentinel_eip;
    uint32_t branch_target_eip = 0;
    uint32_t fallthrough_eip = 0;
    uint8_t terminal_kind_raw = 0;
    bool has_unfused_handlers = false;
    uint16_t invalidation_revision = 0;
    uint64_t exec_count = 0;  // Number of times block was executed

    // Flexible Array Member - fixed-size decoded op stream.
    alignas(16) std::byte slots[sizeof(DecodedOp)];

    static constexpr size_t AlignFooter(size_t offset) { return (offset + 7) & ~size_t{7}; }

    static constexpr size_t CalculateSize(BlockSlotLayout layout, bool retain_scalar_handlers = false) {
        if (!layout.logical_slot_count) return sizeof(BasicBlock);
        size_t size = offsetof(BasicBlock, slots) + sizeof(DecodedOp) * layout.PhysicalSlotCount();
        if (layout.data_slot_count) size = AlignFooter(size) + sizeof(BlockDataSlotMask);
        size +=
            (layout.logical_slot_count - 1) * (sizeof(uint16_t) + (retain_scalar_handlers ? sizeof(HandlerFunc) : 0));
        if (layout.anchor_count) size = AlignFooter(size) + sizeof(uint64_t) + sizeof(uint32_t) * layout.anchor_count;
        return size;
    }

    DecodedOp* FirstOp() { return reinterpret_cast<DecodedOp*>(slots); }
    const DecodedOp* FirstOp() const { return reinterpret_cast<const DecodedOp*>(slots); }
    DecodedOp* Sentinel() { return FirstOp() + sentinel_slot_index; }
    const DecodedOp* Sentinel() const { return FirstOp() + sentinel_slot_index; }

    FORCE_INLINE bool HasDataSlots() const { return slot_count > inst_count() + 1; }

    FORCE_INLINE size_t SlotsEndOffset() const { return offsetof(BasicBlock, slots) + sizeof(DecodedOp) * slot_count; }
    size_t DataSlotMaskOffset() const { return AlignFooter(SlotsEndOffset()); }

    FORCE_INLINE bool IsLinkOnlyReturn(const DecodedOp* op) const {
        return (link_only_return_prefixes & prefix::LOCK) && op + 1 == Sentinel();
    }
    const BlockDataSlotMask* DataSlotMask() const {
        return reinterpret_cast<const BlockDataSlotMask*>(reinterpret_cast<const std::byte*>(this) +
                                                          DataSlotMaskOffset());
    }

    static FORCE_INLINE uint64_t SlotsBefore(size_t ordinal) {
        return ordinal == 64 ? UINT64_MAX : (uint64_t{1} << ordinal) - 1;
    }

    FORCE_INLINE size_t LogicalIndex(const DecodedOp* op) const {
        const size_t physical = op - FirstOp();
        if (!HasDataSlots()) [[likely]]
            return physical;
        if (physical == sentinel_slot_index) return inst_count();
        const auto* mask = DataSlotMask();
        const auto word = physical / 64;
        const auto earlier_words = word ? std::popcount(mask->physical[0]) : 0;
        return physical - earlier_words - std::popcount(mask->physical[word] & SlotsBefore(physical % 64));
    }

    FORCE_INLINE const DecodedOp* LogicalOp(size_t slot) const {
        if (HasDataSlots()) [[unlikely]]
            slot += std::popcount(DataSlotMask()->logical & SlotsBefore(slot));
        return FirstOp() + slot;
    }
    FORCE_INLINE DecodedOp* LogicalOp(size_t slot) {
        return const_cast<DecodedOp*>(std::as_const(*this).LogicalOp(slot));
    }

    FORCE_INLINE const DecodedOp* NextOp(const DecodedOp* op) const {
        const auto* next = op + 1;
        if (HasDataSlots() && next != Sentinel()) {
            const auto physical = next - FirstOp();
            next += (DataSlotMask()->physical[physical / 64] >> (physical % 64)) & 1;
        }
        return next;
    }
    FORCE_INLINE DecodedOp* NextOp(DecodedOp* op) { return const_cast<DecodedOp*>(std::as_const(*this).NextOp(op)); }

    // Scalar handlers and PC offsets are indexed by guest instruction, never data slot.
    HandlerFunc* UnfusedHandlers() { return const_cast<HandlerFunc*>(std::as_const(*this).UnfusedHandlers()); }
    const HandlerFunc* UnfusedHandlers() const {
        const size_t offset = HasDataSlots() ? DataSlotMaskOffset() + sizeof(BlockDataSlotMask) : SlotsEndOffset();
        return reinterpret_cast<const HandlerFunc*>(reinterpret_cast<const std::byte*>(this) + offset);
    }

    const uint16_t* PCEnds() const {
        return reinterpret_cast<const uint16_t*>(UnfusedHandlers() + (has_unfused_handlers ? inst_count() : 0));
    }

    size_t AnchorOffset() const {
        return AlignFooter(reinterpret_cast<const std::byte*>(PCEnds() + inst_count()) -
                           reinterpret_cast<const std::byte*>(this));
    }

    FORCE_INLINE BasicBlock* FallthroughLink() const { return Sentinel()->LoadBlockLink(); }
    FORCE_INLINE void SetFallthroughLink(BasicBlock* target) { Sentinel()->StoreBlockLink(target); }

    FORCE_INLINE uint32_t PCAdjustment(size_t slot) const {
        const auto* anchors =
            reinterpret_cast<const uint64_t*>(reinterpret_cast<const std::byte*>(this) + AnchorOffset());
        const auto mask = *anchors & (UINT64_MAX >> (63 - slot));
        if (!mask) return 0;
        const auto* adjustments = reinterpret_cast<const uint32_t*>(anchors + 1);
        return adjustments[std::popcount(mask) - 1];
    }
    [[gnu::pure, gnu::noinline]] inline uint32_t GetNextEip(size_t slot) const {
        if (slot == inst_count()) return sentinel_eip;
        return start_eip() + PCEnds()[slot] + (anchor_count ? PCAdjustment(slot) : 0);
    }
    FORCE_INLINE uint32_t GetNextEip(const DecodedOp* op) const { return GetNextEip(LogicalIndex(op)); }
    FORCE_INLINE uint32_t GetStartEip(const DecodedOp* op) const {
        const auto slot = LogicalIndex(op);
        if (slot == inst_count()) [[unlikely]]
            return sentinel_eip;
        const auto offset = slot ? PCEnds()[slot - 1] : 0;
        return start_eip() + offset + (anchor_count ? PCAdjustment(slot) : 0);
    }
    FORCE_INLINE uint8_t GetOpLength(size_t slot) const {
        if (slot == inst_count()) return 0;
        const auto* ends = PCEnds();
        return uint8_t(ends[slot] - (slot ? ends[slot - 1] : 0));
    }

    void WriteOps(BlockSlotLayout layout, std::span<const DecodedInstruction> ops, BasicBlock* invalid,
                  bool preserve_handlers = false);
    DecodedInstruction ReadOp(size_t slot) const;

    BlockTerminalKind terminal_kind() const { return static_cast<BlockTerminalKind>(terminal_kind_raw); }
    void set_terminal_kind(BlockTerminalKind kind) { terminal_kind_raw = static_cast<uint8_t>(kind); }

    uint32_t start_eip() const { return static_cast<uint32_t>(chain.start_eip); }
    void set_start_eip(uint32_t start_eip) { chain.start_eip = start_eip; }
    bool is_valid() const { return chain.valid != 0; }
    void set_valid(bool valid) { chain.valid = valid ? 1u : 0u; }
    uint32_t canonical_start_eip() const { return start_eip(); }

    uint32_t inst_count() const { return chain.inst_count; }
    void set_inst_count(uint32_t count) { chain.inst_count = static_cast<uint8_t>(count); }

    bool MatchesChainTarget(uint32_t target_eip) const { return is_valid() && start_eip() == target_eip; }

    // Mark block as invalid
    void Invalidate();
    void Revalidate();
};

static_assert(offsetof(BasicBlock, chain) == 0, "BasicBlock: chain must start at offset 0");
static_assert(offsetof(BasicBlock, entry) == 8, "BasicBlock: entry must start at offset 8");
static_assert(sizeof(BasicBlockChainPrefix) == 8, "BasicBlockChainPrefix must be exactly 8 bytes");

static_assert(offsetof(BasicBlock, end_eip) == 16);
static_assert(offsetof(BasicBlock, slot_count) == 20);
static_assert(offsetof(BasicBlock, branch_target_eip) == 28);
static_assert(offsetof(BasicBlock, exec_count) == 40);
static_assert(offsetof(BasicBlock, slots) == 48, "BasicBlock: slots must start at offset 48");

// Decoder Logic
enum class InstructionDecodeResult : uint8_t { Success, NeedMoreBytes, Invalid, FetchFault };
enum class DecodeHandlerMode : uint8_t { Native, Generic };
// Never reads beyond available bytes or the architectural 15-byte limit.
InstructionDecodeResult DecodeInstructionBounded(const uint8_t* code, size_t available, DecodedInstTmp* inst,
                                                 uint16_t* handler_index, uint16_t* raw_opcode = nullptr,
                                                 DecodeHandlerMode mode = DecodeHandlerMode::Native);
bool DecodeInstruction(const uint8_t* code, DecodedInstTmp* inst, uint16_t* handler_index);
// buffer must have room for 15 bytes; its contents are scratch except on invalid
// decoding, where they hold the fetched bytes for diagnostics. Fetches another
// page only when decoding requires it. raw_opcode, when requested, is the
// architectural opcode before any handler-specific remapping.
InstructionDecodeResult FetchAndDecodeInstruction(EmuState* state, uint32_t eip, uint8_t* buffer, DecodedInstTmp* inst,
                                                  uint16_t* handler_index, uint16_t* raw_opcode = nullptr,
                                                  DecodeHandlerMode mode = DecodeHandlerMode::Native);
// Shared Step/block preparation, without dead-flags analysis.
void FinalizeDecodedInstruction(EmuState* state, uint32_t eip, uint16_t handler_index, DecodedInstruction* op,
                                DecodeHandlerMode mode = DecodeHandlerMode::Native);
bool RedecodeGenericInstruction(EmuState* state, const DecodedInstruction& published, DecodedInstruction& generic);
// Snapshot a published slot, including any absorbed NOPs, without invoking
// guest callbacks. Generic re-decode of those bytes can then run on a worker.
bool CopyDecodedInstructionBytes(EmuState* state, const DecodedInstruction& published, std::span<uint8_t> bytes);
bool RedecodeGenericInstruction(std::span<const uint8_t> bytes, const DecodedInstruction& published,
                                DecodedInstruction& generic);

// Start EIP, Limit EIP, Max Instructions -> Returns Pointer to allocated block or nullptr
BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts,
                        BasicBlock* invalidated_candidate = nullptr, bool* use_single_step = nullptr);

}  // namespace fiberish
