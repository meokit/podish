#pragma once

#include "specialization.h"

namespace fiberish::spec {

enum class Width : uint8_t { Dynamic, W8, W16, W32 };
enum class AddressWidth : uint8_t { Dynamic, A16, A32 };
enum class WidthRule : uint8_t { Fixed8, Fixed32, ScalarEv, MandatoryPrefix };
enum class ByteLane : uint8_t { Low, High };
enum class Flags : uint8_t { Live, Dead, Any };
enum class Form : uint8_t { Dynamic, Register, Memory };
enum class Gpr : int8_t { Dynamic = -1, Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi };
enum class Base : int8_t { Dynamic = -2, Absent = -1, Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi };
enum class Index : int8_t { Dynamic = -2, Absent = -1, Eax, Ecx, Edx, Ebx, Esp, Ebp, Esi, Edi };
// Default means no override, not a zero architectural segment base.
enum class Segment : int8_t { Dynamic = -1, Default, Es, Cs, Ss, Ds, Fs, Gs };
enum class MandatoryPrefix : uint8_t { Dynamic, P66, F2, F3, F3Only };
enum class Layout : uint8_t { None, RmReg, RegRm, RmOnly, OpcodeReg, Accumulator };

// A structural C++23 value: all variant facts can be used as template arguments.
struct Spec {
    Width operand_width = Width::Dynamic;
    AddressWidth address_width = AddressWidth::Dynamic;
    Flags flag_mode = Flags::Live;
    Form rm_form = Form::Dynamic;
    Gpr destination = Gpr::Dynamic;
    Gpr source = Gpr::Dynamic;
    ByteLane destination_lane = ByteLane::Low;
    ByteLane source_lane = ByteLane::Low;
    Base address_base = Base::Dynamic;
    Index address_index = Index::Dynamic;
    Segment address_segment = Segment::Dynamic;
    MandatoryPrefix mandatory_prefix = MandatoryPrefix::Dynamic;
    int8_t address_scale = -1;

    constexpr Spec width(Width v) const {
        auto s = *this;
        s.operand_width = v;
        return s;
    }
    constexpr Spec addressWidth(AddressWidth v) const {
        auto s = *this;
        s.address_width = v;
        return s;
    }
    constexpr Spec flags(Flags v) const {
        auto s = *this;
        s.flag_mode = v;
        return s;
    }
    constexpr Spec regForm() const {
        auto s = *this;
        s.rm_form = Form::Register;
        return s;
    }
    constexpr Spec memoryForm() const {
        auto s = *this;
        s.rm_form = Form::Memory;
        return s;
    }
    constexpr Spec dst(Gpr v) const {
        auto s = *this;
        s.destination = v;
        return s;
    }
    constexpr Spec src(Gpr v) const {
        auto s = *this;
        s.source = v;
        return s;
    }
    constexpr Spec dst8(Gpr v, ByteLane lane) const {
        auto s = dst(v);
        s.destination_lane = lane;
        return s;
    }
    constexpr Spec src8(Gpr v, ByteLane lane) const {
        auto s = src(v);
        s.source_lane = lane;
        return s;
    }
    constexpr Spec base(Base v) const {
        auto s = *this;
        s.address_base = v;
        return s;
    }
    constexpr Spec index(Index v) const {
        auto s = *this;
        s.address_index = v;
        return s;
    }
    constexpr Spec segment(Segment v) const {
        auto s = *this;
        s.address_segment = v;
        return s;
    }
    constexpr Spec scale(int8_t v) const {
        auto s = *this;
        s.address_scale = v;
        return s;
    }
    constexpr Spec mandatory(MandatoryPrefix v) const {
        auto s = *this;
        s.mandatory_prefix = v;
        return s;
    }
};

// The decoder uses handler indices for split MOV forms as well as real opcodes.
// Group extension belongs to the encoding, never to an operand's GPR identity.
template <uint16_t HandlerIndex, Layout Operands, int Extension, WidthRule Rule>
struct Encoding {
    static constexpr uint16_t opcode = HandlerIndex;
    static constexpr Layout layout = Operands;
    static constexpr int extension = Extension;
    static constexpr WidthRule width_rule = Rule;
};

template <class Family>
constexpr bool Valid(Spec s) {
    if (Family::opcode >= 1024 || Family::extension < -1 || Family::extension > 7) return false;
    if (static_cast<int>(s.destination) < -1 || static_cast<int>(s.destination) > 7 ||
        static_cast<int>(s.source) < -1 || static_cast<int>(s.source) > 7)
        return false;
    if (static_cast<int>(s.address_base) < -2 || static_cast<int>(s.address_base) > 7 ||
        static_cast<int>(s.address_index) < -2 || static_cast<int>(s.address_index) > 7 || s.address_scale < -1 ||
        s.address_scale > 3)
        return false;
    if (static_cast<int>(s.operand_width) > static_cast<int>(Width::W32) ||
        static_cast<int>(s.address_width) > static_cast<int>(AddressWidth::A32) ||
        static_cast<int>(s.flag_mode) > static_cast<int>(Flags::Any) ||
        static_cast<int>(s.rm_form) > static_cast<int>(Form::Memory) || static_cast<int>(s.address_segment) < -1 ||
        static_cast<int>(s.address_segment) > 6 ||
        static_cast<int>(s.mandatory_prefix) > static_cast<int>(MandatoryPrefix::F3Only))
        return false;
    if (s.operand_width != Width::Dynamic) {
        if (Family::width_rule == WidthRule::MandatoryPrefix) return false;
        if (Family::width_rule == WidthRule::Fixed8 && s.operand_width != Width::W8) return false;
        if (Family::width_rule == WidthRule::Fixed32 && s.operand_width != Width::W32) return false;
        if (Family::width_rule == WidthRule::ScalarEv && s.operand_width == Width::W8) return false;
    }
    if (s.mandatory_prefix != MandatoryPrefix::Dynamic && Family::width_rule != WidthRule::MandatoryPrefix)
        return false;
    if (Family::layout == Layout::None &&
        (s.source != Gpr::Dynamic || s.destination != Gpr::Dynamic || s.rm_form != Form::Dynamic))
        return false;
    if (Family::extension >= 0 && Family::layout != Layout::RmOnly) return false;
    // Vector register identities need their own operand-bank policy, not a GPR label.
    if (Family::width_rule == WidthRule::MandatoryPrefix && (s.destination != Gpr::Dynamic || s.source != Gpr::Dynamic))
        return false;
    if (static_cast<int>(s.destination_lane) > 1 || static_cast<int>(s.source_lane) > 1) return false;
    if (Family::width_rule == WidthRule::Fixed8) {
        if (static_cast<int>(s.destination) > 3 || static_cast<int>(s.source) > 3) return false;
    } else if (s.destination_lane != ByteLane::Low || s.source_lane != ByteLane::Low)
        return false;
    if ((s.destination_lane == ByteLane::High && s.destination == Gpr::Dynamic) ||
        (s.source_lane == ByteLane::High && s.source == Gpr::Dynamic))
        return false;
    const bool rm_fixed = (Family::layout == Layout::RmReg || Family::layout == Layout::RmOnly)
                              ? s.destination != Gpr::Dynamic
                              : Family::layout == Layout::RegRm && s.source != Gpr::Dynamic;
    const bool ea = s.address_base != Base::Dynamic || s.address_index != Index::Dynamic ||
                    s.address_segment != Segment::Dynamic || s.address_scale != -1;
    if ((Family::layout == Layout::None || Family::layout == Layout::OpcodeReg ||
         Family::layout == Layout::Accumulator) &&
        (ea || s.address_width != AddressWidth::Dynamic || s.rm_form != Form::Dynamic))
        return false;
    if (s.address_width != AddressWidth::Dynamic && (rm_fixed || s.rm_form == Form::Register)) return false;
    if (s.address_width == AddressWidth::A16 &&
        (s.address_base == Base::Eax || s.address_base == Base::Ecx || s.address_base == Base::Edx ||
         s.address_base == Base::Esp || s.address_index == Index::Eax || s.address_index == Index::Ecx ||
         s.address_index == Index::Edx || s.address_index == Index::Esp))
        return false;
    if ((rm_fixed || s.rm_form == Form::Register) && (ea || s.rm_form == Form::Memory)) return false;
    if (Family::layout == Layout::RmOnly && s.source != Gpr::Dynamic) return false;
    // Opcode/implicit register identities already belong to the Family; don't silently ignore them.
    if ((Family::layout == Layout::OpcodeReg || Family::layout == Layout::Accumulator) &&
        (s.source != Gpr::Dynamic || s.destination != Gpr::Dynamic))
        return false;
    return true;
}

template <class Family>
constexpr SpecCriteria Criteria(Spec s) {
    SpecCriteria c;
    c.no_flags = s.flag_mode == Flags::Dead;
    c.any_flags = s.flag_mode == Flags::Any;
    if (Family::extension >= 0) {
        c.reg_mask = 7;
        c.reg_val = Family::extension;
    }
    auto encoded = [&](Gpr v, ByteLane lane) { return static_cast<uint8_t>(v) + (lane == ByteLane::High ? 4 : 0); };
    auto reg = [&](Gpr v, ByteLane lane) {
        if (v != Gpr::Dynamic) {
            c.reg_mask = 7;
            c.reg_val = encoded(v, lane);
        }
    };
    auto rm = [&](Gpr v, ByteLane lane) {
        if (v != Gpr::Dynamic) {
            c.rm_mask = 7;
            c.rm_val = encoded(v, lane);
            c.mod_mask = 3;
            c.mod_val = 3;
        }
    };
    if (Family::layout == Layout::RmReg) {
        rm(s.destination, s.destination_lane);
        reg(s.source, s.source_lane);
    }
    if (Family::layout == Layout::RegRm) {
        reg(s.destination, s.destination_lane);
        rm(s.source, s.source_lane);
    }
    if (Family::layout == Layout::RmOnly) rm(s.destination, s.destination_lane);
    if (s.rm_form == Form::Register) {
        c.mod_mask = 3;
        c.mod_val = 3;
    }
    // has_mem is a decoded fact; it must also be checked for an unconstrained memory form.
    c.memory_only = s.rm_form == Form::Memory;
    if (s.operand_width != Width::Dynamic && Family::width_rule == WidthRule::ScalarEv) {
        c.prefix_mask = prefix::OPSIZE;
        c.prefix_val = s.operand_width == Width::W16 ? prefix::OPSIZE : 0;
    }
    if (s.mandatory_prefix != MandatoryPrefix::Dynamic) {
        c.prefix_mask = s.mandatory_prefix == MandatoryPrefix::P66  ? prefix::OPSIZE
                        : s.mandatory_prefix == MandatoryPrefix::F2 ? prefix::REPNE
                                                                    : prefix::REP;
        c.prefix_val = c.prefix_mask;
        if (s.mandatory_prefix == MandatoryPrefix::F3Only) c.prefix_mask |= prefix::OPSIZE | prefix::REPNE;
    }
    if (s.address_width != AddressWidth::Dynamic) {
        c.prefix_mask |= prefix::ADDRSIZE;
        if (s.address_width == AddressWidth::A16) c.prefix_val |= prefix::ADDRSIZE;
    }
    if (s.address_base != Base::Dynamic) {
        c.base_offset_mask = 0x3F;
        c.base_offset_val = s.address_base == Base::Absent ? kNoRegOffset : static_cast<uint8_t>(s.address_base) * 4;
    }
    if (s.address_base == Base::Esp) {
        c.rm_mask = 7;
        c.rm_val = 4;
    }
    if (s.address_index != Index::Dynamic) {
        c.index_offset_mask = 0x3F;
        c.index_offset_val =
            s.address_index == Index::Absent ? kNoRegOffset : static_cast<uint8_t>(s.address_index) * 4;
    }
    if (s.address_scale != -1) {
        c.scale_mask = 3;
        c.scale_val = s.address_scale;
    }
    if (s.address_segment != Segment::Dynamic) {
        c.segment_mask = 7;
        c.segment_val = static_cast<uint8_t>(s.address_segment);
    }
    return c;
}

// Existing inline logic bodies remain behind this adapter during migration.
// A catalog declaration binds each (Family, Spec) to exactly one legacy entry.
template <class Family, Spec S>
struct LegacyBinding;
template <class Family, Spec S>
struct Variant : LegacyBinding<Family, S> {
    static_assert(Valid<Family>(S), "Invalid instruction specialization");
    static constexpr auto criteria = Criteria<Family>(S);
};

}  // namespace fiberish::spec
