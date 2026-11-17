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
enum class PinWriteClass : int8_t { Other = -1, Eax = 0, Esp = 4 };
template <PinWriteRole Role, PinWriteClass Class, bool Byte>
struct RegisterOperandPolicy {
    static constexpr unsigned keep_mask = Role == PinWriteRole::None || Class == PinWriteClass::Other ? 3
                                          : Class == PinWriteClass::Eax                               ? 2
                                                                                                      : 1;
    static FORCE_INLINE void Assume(const DecodedOp* op) {
        __builtin_assume((op->modrm & 0xC0) == 0xC0);
        if constexpr (Role != PinWriteRole::None) {
            const unsigned field = Role == PinWriteRole::Rm ? op->modrm : op->modrm >> 3;
            const unsigned parent = field & (Byte ? 3 : 7);
            if constexpr (Class == PinWriteClass::Other) {
                __builtin_assume(parent != 0);
                if constexpr (!Byte) __builtin_assume(parent != 4);
            } else
                __builtin_assume(parent == unsigned(Class));
        }
    }
};
}  // namespace fiberish
