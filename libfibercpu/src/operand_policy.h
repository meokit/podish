#pragma once
#include <compare>
#include <type_traits>

#include "decoder.h"

namespace fiberish {
// The mandatory part of a runtime binding. Use the raw ModRM byte so exact
// templates and the assembler agree even for instructions that consume only part of it.
struct BoundOperandShape {
    uint8_t modrm;
    uint8_t slots;

    auto operator<=>(const BoundOperandShape&) const = default;

    static constexpr BoundOperandShape From(uint8_t modrm, uint8_t prefixes, uint8_t metadata, uint32_t immediate) {
        // Link-only RET has no ModRM. Its low opcode bits leave room to bind
        // the stack operand width in both the scalar and generic LLVM keys.
        if (IsLinkOnlyReturn(metadata)) modrm |= prefixes & prefix::OPSIZE;
        return {modrm, uint8_t(1 + HasExtraOperandSlot(metadata, prefixes, immediate))};
    }

    static FORCE_INLINE BoundOperandShape Read(const DecodedInstruction& op) {
        return From(op.modrm, op.prefixes.all, op.meta.all, GetImm(&op));
    }
};

namespace OperandSpec {
struct Dynamic {
    static constexpr unsigned keep_mask = 0;
    static constexpr unsigned slots = 0;
    static FORCE_INLINE uint8_t ModRM(const DecodedOp* op) { return uint8_t(op->payload >> DecodedOp::kModRMShift); }
    static FORCE_INLINE Prefixes ReadPrefixes(const DecodedOp* op) { return GetPrefixes(op); }
    static FORCE_INLINE void Assume(const DecodedOp*) {}
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) { return GetImm(op); }
};
template <unsigned Slots, bool SecondSlotImmediate = (Slots == 2)>
struct SlotBound : Dynamic {
    static_assert(Slots == 1 || Slots == 2);
    static constexpr unsigned slots = Slots;
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) {
        if constexpr (SecondSlotImmediate) return (op + 1)->Primary();
        if constexpr (Slots == 2) return op->Primary();
        return op->HasMemory() ? GetSingleSlotMemoryImmediate(op) : op->Primary();
    }
};

template <class Policy>
concept FixedSlots = Policy::slots == 1 || Policy::slots == 2;

// Slot layout and instruction operands are selected together before publication.
template <class Policy, uint8_t Value, unsigned Keep = Policy::keep_mask>
struct WithModRM : Policy {
    static constexpr unsigned keep_mask = Keep;
    static constexpr uint8_t modrm = Value;
    static constexpr uint8_t ModRM(const DecodedOp*) { return Value; }
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) {
        if constexpr (Policy::slots == 2) return (op + 1)->Primary();
        if constexpr (Value < 0xc0) return GetSingleSlotMemoryImmediate(op);
        return op->Primary();
    }
};

template <class Policy>
concept FixedModRM = requires { typename std::integral_constant<uint8_t, Policy::modrm>; };

template <class Policy>
concept Executable = FixedSlots<Policy>;

template <unsigned Mask, unsigned Index>
using FusionSlot = SlotBound<1 + ((Mask >> Index) & 1)>;

template <FixedSlots Policy>
FORCE_INLINE DecodedOp* Advance(DecodedOp* op) {
    return op + Policy::slots;
}

}  // namespace OperandSpec

// Fields that survive decoding and describe the instruction independently of
// its address. The matching code reads the same fields before selecting a clone.
struct DecodedOperandShape {
    uint32_t encoding;  // ModRM, prefixes, metadata, instruction length
    uint32_t ea_desc;
    uint32_t displacement;
    uint32_t immediate;

    static constexpr uint8_t metadata_mask = 0xdf;  // Exclude operand_lowered.

    static constexpr bool HasMemoryAddress(uint8_t metadata) { return (metadata & 0xc2) == 2; }

    constexpr BoundOperandShape Binding() const {
        return BoundOperandShape::From(uint8_t(encoding), uint8_t(encoding >> 8), uint8_t(encoding >> 16),
                                       immediate);
    }

    constexpr unsigned SlotCount() const { return Binding().slots; }

    auto operator<=>(const DecodedOperandShape&) const = default;

    static FORCE_INLINE DecodedOperandShape Read(const DecodedInstruction* op, bool include_relative_offset = false) {
        const auto kind = GetExtKind(op);
        const bool data_memory = HasMem(op) && kind == ExtKind::Data;
        // Relative branches consume the resolved target in the runtime Op.
        // RET imm16 uses the same storage kind and still needs its immediate.
        const bool omit_immediate = !include_relative_offset && kind == ExtKind::ControlFlow &&
                                   op->ext.control.relative_width != RelativeBranchWidth::None;
        return {
            uint32_t(op->modrm) | (uint32_t(op->prefixes.all) << 8) | (uint32_t(op->meta.all & metadata_mask) << 16) |
                (uint32_t(op->len) << 24),
            data_memory ? op->ext.data.ea_desc : 0,
            data_memory ? op->ext.data.disp : 0,
            omit_immediate ? 0 : GetImm(op),
        };
    }
};

namespace OperandSpec {
// The exact LLVM kernel supplies these values from the block recipe. The
// original Op still identifies the instruction when the MMU requests replay.
struct Baked {
    Prefixes prefixes;
    BakedAddress address;

    constexpr Baked(uint8_t prefix_byte, uint32_t ea_desc, uint32_t displacement)
        : prefixes{prefix_byte}, address{ea_desc, displacement, (prefix_byte & prefix::ADDRSIZE) != 0} {}

    constexpr Prefixes ReadPrefixes(const DecodedOp*) const { return prefixes; }
};

template <DecodedOperandShape Shape, unsigned Keep = 0>
struct Profile {
    static constexpr unsigned keep_mask = Keep;
    static constexpr unsigned slots = Shape.SlotCount();
    static constexpr uint8_t modrm = Shape.Binding().modrm;
    static constexpr uint8_t ModRM(const DecodedOp*) { return modrm; }
    static constexpr uint8_t metadata = uint8_t(Shape.encoding >> 16);
    static constexpr uint8_t prefixes = uint8_t(Shape.encoding >> 8);
    static constexpr Baked operands{prefixes, Shape.ea_desc, Shape.displacement};
    static constexpr const BakedAddress* baked_address =
        DecodedOperandShape::HasMemoryAddress(metadata) ? &operands.address : nullptr;
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        if constexpr (!IsLinkOnlyReturn(metadata)) {
            const auto decoded_prefixes = GetPrefixes(op).all;
            __builtin_assume(decoded_prefixes == prefixes);
        }
    }
    static constexpr uint32_t Immediate(const DecodedOp*) { return Shape.immediate; }
};
}  // namespace OperandSpec

}  // namespace fiberish
