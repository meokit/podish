// Compile-time contract tests run for every native target without a host test executable.
#include "instruction_families.h"

namespace fiberish::spec {
using Add = family::Add_EvGv;
constexpr auto eax = Spec{}.width(Width::W32).regForm().dst(Gpr::Eax).flags(Flags::Dead);
constexpr auto add = Criteria<Add>(eax);
static_assert(Valid<Add>(eax));
static_assert(add.mod_mask == 3 && add.mod_val == 3);  // Extracted ModRM.mod, not bits 6..7.
static_assert(add.rm_mask == 7 && add.rm_val == 0 && add.reg_mask == 0);
static_assert(add.prefix_mask == prefix::OPSIZE && add.prefix_val == 0 && add.no_flags);
static_assert(!Valid<Add>(eax.memoryForm()));
static_assert(!Valid<Add>(eax.base(Base::Esp)));
static_assert(!Valid<Add>(eax.width(Width::W8)));
static_assert(!Valid<Add>(eax.mandatory(MandatoryPrefix::P66)));

using Group = family::Group1_EvIb_Add;
constexpr auto group = Criteria<Group>(eax);
static_assert(group.reg_mask == 7 && group.reg_val == 0 && group.rm_val == 0);
static_assert(!Valid<Group>(Spec{}.src(Gpr::Ecx)));  // /0 is not a source GPR.

using Mov = family::Mov_GvEv_Mem;
constexpr auto stack = Spec{}.dst(Gpr::Eax).base(Base::Esp).index(Index::Absent).segment(Segment::Default);
static_assert(Valid<Mov>(stack));
constexpr auto mem = Criteria<Mov>(stack);
static_assert(mem.reg_mask == 7 && mem.reg_val == 0 && mem.rm_mask == 7 && mem.rm_val == 4);
static_assert(mem.base_offset_val == 16 && mem.index_offset_val == kNoRegOffset);
static_assert(mem.segment_mask == 7 && mem.segment_val == 0);
static_assert(Criteria<Mov>(stack.addressWidth(AddressWidth::A32)).prefix_mask == prefix::ADDRSIZE);
static_assert(Criteria<Mov>(Spec{}.flags(Flags::Any)).any_flags);
static_assert(Criteria<Add>(Spec{}.flags(Flags::Live)).GetScore() > Criteria<Add>(Spec{}.flags(Flags::Any)).GetScore());
static_assert(Criteria<Add>(Spec{}.memoryForm()).GetScore() > Criteria<Add>(Spec{}).GetScore());

using Byte = family::Group1_EbIb_Cmp_080;
static_assert(!Valid<Byte>(Spec{}.dst(Gpr::Esp)));
static_assert(Valid<Byte>(Spec{}.dst8(Gpr::Eax, ByteLane::High)));
static_assert(Criteria<Byte>(Spec{}.dst8(Gpr::Eax, ByteLane::High)).rm_val == 4);
static_assert(!Valid<Add>(Spec{}.dst8(Gpr::Eax, ByteLane::High)));

using Sse = family::Encoding_16F;
using VectorGroup = Encoding<0x171, Layout::RmOnly, 2, WidthRule::MandatoryPrefix>;
static_assert(!Valid<VectorGroup>(Spec{}.dst(Gpr::Eax)));
static_assert(Valid<Sse>(Spec{}.mandatory(MandatoryPrefix::P66)));
static_assert(!Valid<Sse>(Spec{}.width(Width::W16)));
static_assert(Criteria<Sse>(Spec{}.mandatory(MandatoryPrefix::F3)).prefix_val == prefix::REP);
}  // namespace fiberish::spec
