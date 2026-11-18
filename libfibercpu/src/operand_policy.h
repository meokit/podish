#pragma once
#include "decoder.h"

namespace fiberish {
struct DynamicOperandPolicy {
    static constexpr unsigned keep_mask = 0;
    static FORCE_INLINE void Assume(const DecodedOp*) {}
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
struct ExactOperandPolicy {
    static constexpr unsigned keep_mask = Keep;
    static FORCE_INLINE void Assume(const DecodedOp* op) { AssumeOperandShape<Shape>(op); }
};
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
template <PinWriteRole Role, PinWriteClass Class, bool Byte>
struct RegisterOperandPolicy {
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
struct EncodedOperandPolicy {
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
}  // namespace fiberish
