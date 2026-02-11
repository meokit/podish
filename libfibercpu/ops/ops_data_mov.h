// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpMov_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

LogicFlow OpMov_EvGv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic
LogicFlow OpMov_EvGv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic
LogicFlow OpMov_GvEv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic
LogicFlow OpMov_GvEv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);  // Generic

// Specialized Stores (Reg -> Mem/Reg)
LogicFlow OpMov_Store_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Store_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

// Specialized Loads (Mem -> Reg)
LogicFlow OpMov_Load_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Load_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

// Specialized Reg-Reg Moves
LogicFlow OpMov_Ebp_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Ecx_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_Edx_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

// Specialized RR Store (Mod=3) - Source is Reg
LogicFlow OpMov_EvGv_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);
LogicFlow OpMov_EvGv_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

LogicFlow OpXchg_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_EvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_RegImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_RegImm8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Moffs_Load_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Moffs_Load_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Moffs_Store_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Moffs_Store_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Rm_Sreg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMov_Sreg_Rm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovzx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovzx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovsx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovsx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpLea(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPush_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPush_Imm32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPush_Imm8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPop_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPop_Ev(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPusha(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPopa(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpEnter(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpLeave(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpLahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXchg_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpXchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovs_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpMovs_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpStos_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpStos_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpLods_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpLods_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpScas_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpScas_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmps_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmps_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

}  // namespace op

void RegisterDataMovOps();
}  // namespace fiberish
