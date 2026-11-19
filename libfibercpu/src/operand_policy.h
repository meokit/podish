#pragma once
#include "decoder.h"

namespace fiberish {
namespace OperandSpec {
struct Dynamic {
    static constexpr unsigned keep_mask = 0;
    static FORCE_INLINE void Assume(const DecodedOp*) {}
    static FORCE_INLINE uint32_t Immediate(const DecodedOp* op) { return GetImm(op); }
};
template <uint64_t Shape>
FORCE_INLINE void AssumeOperandShape(const DecodedOp* op) {
    __builtin_assume(op->modrm == (Shape & 255));
    __builtin_assume(op->prefixes.all == ((Shape >> 8) & 255));
    if constexpr (Shape >> 16) {
        const auto* ext = GetExt(op);
        __builtin_assume(ext->data.ea_desc == uint32_t(Shape >> 16));
    }
}
template <uint64_t Shape, unsigned Keep = 0>
struct Exact : Dynamic {
    static constexpr unsigned keep_mask = Keep;
    static FORCE_INLINE void Assume(const DecodedOp* op) { AssumeOperandShape<Shape>(op); }
};

}  // namespace OperandSpec

// Fields that survive decoding and describe the instruction independently of
// its address. The matching code reads the same fields before selecting a clone.
struct DecodedOperandShape {
    uint32_t encoding;  // ModRM, prefixes, metadata, instruction length
    uint32_t ea_desc;
    uint32_t displacement;
    uint32_t immediate;

    static constexpr uint8_t metadata_mask = 0xdf;  // Exclude operand_lowered.

    bool operator==(const DecodedOperandShape&) const = default;

    static FORCE_INLINE DecodedOperandShape Read(const DecodedOp* op) {
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
template <DecodedOperandShape Shape, unsigned Keep = 0>
struct Profile {
    static constexpr unsigned keep_mask = Keep;
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        constexpr uint8_t metadata = uint8_t(Shape.encoding >> 16);
        __builtin_assume(op->modrm == uint8_t(Shape.encoding));
        __builtin_assume(op->prefixes.all == uint8_t(Shape.encoding >> 8));
        __builtin_assume((op->meta.all & DecodedOperandShape::metadata_mask) == metadata);
        __builtin_assume(op->len == uint8_t(Shape.encoding >> 24));
        if constexpr ((metadata & 0xc2) == 2) {
            __builtin_assume(op->ext.data.ea_desc == Shape.ea_desc);
            __builtin_assume(op->ext.data.disp == Shape.displacement);
        }
        if constexpr (metadata & 4) {
            __builtin_assume(op->ext.data.imm == Shape.immediate);
        }
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
    static constexpr unsigned keep_mask = FIBERCPU_PIN_KEEP_MASK & ~PinArgumentBit(Class);
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        __builtin_assume((op->modrm & 0xC0) == 0xC0);
        if constexpr (Role != PinWriteRole::None) {
            const unsigned field = Role == PinWriteRole::Rm ? op->modrm : op->modrm >> 3;
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
        if constexpr (OpcodeRegister)
            __builtin_assume(!op->meta.flags.has_modrm);
        else
            __builtin_assume((op->modrm & 0xc0) != 0xc0);
        if constexpr (Role != PinWriteRole::None) {
            const unsigned field = Role == PinWriteRole::Reg ? op->modrm >> 3 : op->modrm;
            const unsigned parent = field & (Byte ? 3 : 7);
            AssumePinClass<Class, Byte>(parent);
        }
    }
};
}  // namespace OperandSpec
}  // namespace fiberish
