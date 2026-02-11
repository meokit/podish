// Control Flow
// Auto-generated from ops.cpp refactoring

#pragma once

#include "../common.h"
#include "../decoder.h"

namespace fiberish {
namespace op {

LogicFlow OpJmp_Rel8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpJmp_Rel32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCall_Rel(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpRet(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpRet_Imm16(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPushf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpPopf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpStc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpClc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCmc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpStd(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCld(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpSti(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCli(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpCpuid(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpRdtsc(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpWait(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpGroup_0FAE(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpNop(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpHlt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpInt(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpInt3(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);
LogicFlow OpInto(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb);

#define DECL_JCC(name)                                                          \
    LogicFlow OpJcc_##name##_Rel8(EmuState* s, DecodedOp* o, mem::MicroTLB* u); \
    LogicFlow OpJcc_##name##_Rel32(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

DECL_JCC(O)
DECL_JCC(NO)
DECL_JCC(B) DECL_JCC(AE) DECL_JCC(E) DECL_JCC(NE) DECL_JCC(BE) DECL_JCC(A) DECL_JCC(S) DECL_JCC(NS) DECL_JCC(P)
    DECL_JCC(NP) DECL_JCC(L) DECL_JCC(GE) DECL_JCC(LE) DECL_JCC(G)
#undef DECL_JCC

#define DECL_CMOV(name)                                                   \
    LogicFlow OpCmov_##name(EmuState* s, DecodedOp* o, mem::MicroTLB* u); \
    LogicFlow OpCmov_##name##_ModReg(EmuState* s, DecodedOp* o, mem::MicroTLB* u);

        DECL_CMOV(O) DECL_CMOV(NO) DECL_CMOV(B) DECL_CMOV(AE) DECL_CMOV(E) DECL_CMOV(NE) DECL_CMOV(BE) DECL_CMOV(A)
            DECL_CMOV(S) DECL_CMOV(NS) DECL_CMOV(P) DECL_CMOV(NP) DECL_CMOV(L) DECL_CMOV(GE) DECL_CMOV(LE) DECL_CMOV(G)
#undef DECL_CMOV

}  // namespace op

void RegisterControlOps();
}  // namespace fiberish
