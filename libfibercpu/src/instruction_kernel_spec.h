#pragma once

#include "decoder.h"

namespace fiberish {

enum class OpsizeSpec : uint8_t { Dynamic, Opsize16, Opsize32 };
enum class AddrModeSpec : uint8_t { Dynamic, ModReg, Memory };
enum class FlagsSpec : uint8_t { Dynamic, Live, Dead };
enum class GroupOpSpec : uint8_t { Ext0, Ext1, Ext2, Ext3, Ext4, Ext5, Ext6, Ext7, Dynamic };
enum class AddrsizeSpec : uint8_t { Dynamic, Addr16, Addr32 };
enum class AddrBaseRegSpec : int8_t { Dynamic = -2, Absent, Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi };
enum class AddrIndexSpec : int8_t { Dynamic = -2, Absent, Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi };
enum class AddrSegmentSpec : int8_t { Dynamic = -1, Default, Es, Cs, Ss, Ds, Fs, Gs };

// A register-form operand can constrain just the form or also the register.
enum class RmOperandSpec : uint8_t {
    Dynamic,
    Reg0,
    Reg1,
    Reg2,
    Reg3,
    Reg4,
    Reg5,
    Reg6,
    Reg7,
    RegEax = Reg0,
    RegEcx = Reg1,
    RegEdx = Reg2,
    RegEbx = Reg3,
    RegEsp = Reg4,
    RegEbp = Reg5,
    RegEsi = Reg6,
    RegEdi = Reg7,
    ModReg = 9,
};

template <OpsizeSpec Size>
FORCE_INLINE bool OperandIs16(const DecodedOp* op) {
    if constexpr (Size == OpsizeSpec::Dynamic) return op->prefixes.flags.opsize;
    return Size == OpsizeSpec::Opsize16;
}

template <AddrModeSpec Form>
FORCE_INLINE bool OperandIsReg(const DecodedOp* op) {
    if constexpr (Form == AddrModeSpec::Dynamic) return (op->modrm & 0xc0) == 0xc0;
    return Form == AddrModeSpec::ModReg;
}

template <AddrsizeSpec Size>
FORCE_INLINE bool AddressIs16(const DecodedOp* op) {
    if constexpr (Size == AddrsizeSpec::Dynamic) return op->prefixes.flags.addrsize;
    return Size == AddrsizeSpec::Addr16;
}

template <GroupOpSpec Operation>
FORCE_INLINE uint8_t GroupOperation(const DecodedOp* op) {
    if constexpr (Operation == GroupOpSpec::Dynamic) return (op->modrm >> 3) & 7;
    return static_cast<uint8_t>(Operation);
}

}  // namespace fiberish
