#pragma once
#include <compare>

#include "decoder.h"

namespace fiberish {
// The mandatory part of a runtime binding. Use the raw ModRM byte so exact
// templates and the assembler agree even for instructions that consume only part of it.
struct BoundOperandShape {
    uint8_t modrm;
    uint8_t slots;

    auto operator<=>(const BoundOperandShape&) const = default;

    static FORCE_INLINE BoundOperandShape Read(const DecodedInstruction& op) {
        return {op.modrm, uint8_t(1 + compact_imm::NeedsSeparateSlot(op))};
    }
};

namespace OperandSpec {
struct Dynamic {
    static constexpr unsigned keep_mask = 0;
    static constexpr unsigned slots = 0;
    static FORCE_INLINE uint8_t ModRM(const DecodedOp* op) { return GetModRM(op); }
    static FORCE_INLINE void Assume(const DecodedOp*) {}
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) { return GetImm(op); }
};
template <unsigned Slots>
struct SlotBound : Dynamic {
    static_assert(Slots == 1 || Slots == 2);
    static constexpr unsigned slots = Slots;
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) {
        if constexpr (Slots == 2) return (op + 1)->Primary();
        return op->HasMemory() ? GetSingleSlotMemoryImmediate(op) : op->Primary();
    }
};

template <uint32_t Value>
struct CompactImmediate : Dynamic {
    static constexpr unsigned slots = 1;
    static_assert(compact_imm::Encode(Value) != 0);

    static FORCE_INLINE void Assume(const DecodedOp* op) {
        const bool compact = op->HasCompactImmediate();
        const auto modrm = GetModRM(op);
        __builtin_assume(compact);
        __builtin_assume((modrm & 0xc0) != 0xc0);
    }
    static constexpr uint32_t Immediate(const DecodedOp*) { return Value; }
};

template <uint64_t Shape>
FORCE_INLINE void AssumeOperandShape(const DecodedOp* op) {
    const auto prefixes = GetPrefixes(op).all;
    __builtin_assume(prefixes == ((Shape >> 8) & 255));
    if constexpr (Shape >> 16) {
        const auto ea = GetAddressDescriptor(op);
        __builtin_assume(ea == uint32_t(Shape >> 16));
    }
}
template <uint64_t Shape, unsigned Keep = 0>
struct Exact : Dynamic {
    static constexpr unsigned keep_mask = Keep;
    static constexpr unsigned slots = 1;
    static constexpr uint8_t ModRM(const DecodedOp*) { return uint8_t(Shape); }
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) {
        if constexpr (Shape >> 16) return GetSingleSlotMemoryImmediate(op);
        return op->Primary();
    }
    static FORCE_INLINE void Assume(const DecodedOp* op) { AssumeOperandShape<Shape>(op); }
};

template <class Policy>
FORCE_INLINE DecodedOp* Advance(DecodedOp* op) {
    if constexpr (Policy::slots) return op + Policy::slots;
    return NextOp(op);
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

    constexpr unsigned SlotCount() const {
        return 1 + compact_imm::NeedsSeparateSlot((encoding >> 16) & 2, uint8_t(encoding >> 8), immediate);
    }

    auto operator<=>(const DecodedOperandShape&) const = default;

    static FORCE_INLINE DecodedOperandShape Read(const DecodedInstruction* op) {
        const bool data_memory = HasMem(op) && GetExtKind(op) == ExtKind::Data;
        return {
            uint32_t(op->modrm) | (uint32_t(op->prefixes.all) << 8) | (uint32_t(op->meta.all & metadata_mask) << 16) |
                (uint32_t(op->len) << 24),
            data_memory ? op->ext.data.ea_desc : 0,
            data_memory ? op->ext.data.disp : 0,
            GetImm(op),
        };
    }
};

namespace OperandSpec {
template <unsigned Base, bool Operand16>
struct BaseDisplacement : SlotBound<1> {
    static constexpr BakedAddress address{memdesc::PackEA(Base * 4, kNoRegOffset, 0, 0), 0, false, false};
    static constexpr const BakedAddress* baked_address = &address;
    static FORCE_INLINE uint8_t ModRM(const DecodedOp* op) { return op->RawModRM(); }
    static constexpr uint32_t Immediate(const DecodedOp*) { return 0; }
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        const auto prefixes = op->RawPrefixes();
        __builtin_assume(prefixes == (Operand16 ? prefix::OPSIZE : 0));
    }
};

template <DecodedOperandShape Shape, unsigned Keep = 0>
struct Profile {
    static constexpr unsigned keep_mask = Keep;
    static constexpr unsigned slots = Shape.SlotCount();
    static constexpr uint8_t ModRM(const DecodedOp*) { return uint8_t(Shape.encoding); }
    static constexpr uint8_t metadata = uint8_t(Shape.encoding >> 16);
    static constexpr uint8_t prefixes = uint8_t(Shape.encoding >> 8);
    static constexpr BakedAddress address{Shape.ea_desc, Shape.displacement, (prefixes & prefix::ADDRSIZE) != 0};
    static constexpr const BakedAddress* baked_address = (metadata & 0xc2) == 2 ? &address : nullptr;
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        const auto decoded_prefixes = GetPrefixes(op).all;
        __builtin_assume(decoded_prefixes == prefixes);
    }
    static constexpr uint32_t Immediate(const DecodedOp*) { return Shape.immediate; }
};
}  // namespace OperandSpec

// Other is a decoded operand class, not a per-GetReg cache-selection branch.
enum class PinWriteRole : uint8_t { None, Rm, Reg };
enum class PinWriteClass : int8_t { Other = -1 };

constexpr unsigned PinArgumentBit(PinWriteClass reg) {
#define FIBERCPU_PIN_ARGUMENT_BIT(id, name, slot, ...) \
    if (reg == static_cast<PinWriteClass>(id)) return 1u << slot;
    FIBERCPU_FOR_EACH_PIN(FIBERCPU_PIN_ARGUMENT_BIT)
#undef FIBERCPU_PIN_ARGUMENT_BIT
    return 0u;
}

template <PinWriteClass Class, bool Byte>
FORCE_INLINE void AssumePinClass(unsigned parent) {
    if constexpr (Class == PinWriteClass::Other) {
#define FIBERCPU_ASSUME_UNPINNED(id, name, slot, ...) \
    if constexpr (!Byte || id < 4) __builtin_assume(parent != id);
        FIBERCPU_FOR_EACH_PIN(FIBERCPU_ASSUME_UNPINNED)
#undef FIBERCPU_ASSUME_UNPINNED
    } else {
        __builtin_assume(parent == unsigned(Class));
    }
}
namespace OperandSpec {
template <PinWriteRole Role, PinWriteClass Class, bool Byte>
struct Register : Dynamic {
    static constexpr unsigned slots = 1;
    static FORCE_INLINE uint8_t ModRM(const DecodedOp* op) { return op->RawModRM() | 0xc0; }
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) { return op->Primary(); }
    static constexpr unsigned keep_mask = FIBERCPU_PIN_KEEP_MASK & ~PinArgumentBit(Class);
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        const auto modrm = ModRM(op);
        if constexpr (Role != PinWriteRole::None) {
            const unsigned field = Role == PinWriteRole::Rm ? modrm : modrm >> 3;
            const unsigned parent = field & (Byte ? 3 : 7);
            AssumePinClass<Class, Byte>(parent);
        }
    }
};
// Pure scalar encodings: opcode-register forms and LEA never call an observer.
// They need destination classification without claiming a ModRM register form.
template <PinWriteRole Role, PinWriteClass Class, bool Byte, bool OpcodeRegister>
struct Encoded : Dynamic {
    static constexpr unsigned keep_mask = FIBERCPU_PIN_KEEP_MASK & ~PinArgumentBit(Class);
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        const auto modrm = GetModRM(op);
        if constexpr (OpcodeRegister) {
            const auto meta = GetMeta(op);
            __builtin_assume(!meta.flags.has_modrm);
        } else {
            __builtin_assume((modrm & 0xc0) != 0xc0);
        }
        if constexpr (Role != PinWriteRole::None) {
            const unsigned field = Role == PinWriteRole::Reg ? modrm >> 3 : modrm;
            const unsigned parent = field & (Byte ? 3 : 7);
            AssumePinClass<Class, Byte>(parent);
        }
    }
};
}  // namespace OperandSpec
}  // namespace fiberish
