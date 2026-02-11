// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_data_mov.h"

namespace fiberish {

// ------------------------------------------------------------------------------------------------
// Basic MOV / XCHG
// ------------------------------------------------------------------------------------------------

template <Specialized SrcSpec = Specialized::None, Specialized DstSpec = Specialized::None>
static FORCE_INLINE LogicFlow OpMov_EvGv_Reg_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source (ModRM.Reg)
    if constexpr (SrcSpec >= Specialized::Reg0 && SrcSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedSrc == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t src = (op->modrm >> 3) & 7;
        val = GetReg(state, src);
    }

    // Write Destination (ModRM.RM)
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t dst = op->modrm & 7;
        SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpMov_EvGv_Mem_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV [mem], r32
    // ModRM.Reg is the Source Register
    uint32_t val;
    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        if constexpr (FixedReg == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedReg == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedReg == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedReg == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedReg == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedReg == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedReg == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedReg == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        val = GetReg(state, reg);
    }

    uint32_t addr = ComputeLinearAddress(state, op);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <Specialized DstSpec = Specialized::None, Specialized SrcSpec = Specialized::None>
static FORCE_INLINE LogicFlow OpMov_GvEv_Reg_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV r32, r32
    // Dst = Src
    uint32_t val;

    // Read Source
    if constexpr (SrcSpec >= Specialized::Reg0 && SrcSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedSrc = (uint8_t)SrcSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedSrc == 0)
            val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1)
            val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2)
            val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3)
            val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4)
            val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5)
            val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6)
            val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7)
            val = state->ctx.regs[7];
    } else {
        uint8_t src = op->modrm & 7;
        val = GetReg(state, src);
    }

    // Write Destination
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t dst = (op->modrm >> 3) & 7;
        SetReg(state, dst, val);
    }
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpMov_OaMa_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A3: MOV m32, EAX
    uint32_t addr = ComputeLinearAddress(state, op);
    uint32_t val = GetReg(state, EAX);
    // Restart on TLB Miss
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

template <Specialized S = Specialized::None>
static FORCE_INLINE LogicFlow OpMov_GvEv_Mem_Internal(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV r32, [mem]
    uint32_t addr = ComputeLinearAddress(state, op);
    // Restart on TLB Miss
    auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t val = *val_res;

    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        if constexpr (FixedReg == 0)
            state->ctx.regs[0] = val;
        else if constexpr (FixedReg == 1)
            state->ctx.regs[1] = val;
        else if constexpr (FixedReg == 2)
            state->ctx.regs[2] = val;
        else if constexpr (FixedReg == 3)
            state->ctx.regs[3] = val;
        else if constexpr (FixedReg == 4)
            state->ctx.regs[4] = val;
        else if constexpr (FixedReg == 5)
            state->ctx.regs[5] = val;
        else if constexpr (FixedReg == 6)
            state->ctx.regs[6] = val;
        else if constexpr (FixedReg == 7)
            state->ctx.regs[7] = val;
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, val);
    }
    return LogicFlow::Continue;
}

namespace op {

FORCE_INLINE LogicFlow OpMov_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m16/32, r16/32 (0x89)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        uint32_t val = GetReg(state, reg);
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// Named wrappers for OpMov_EvGv_Reg (0x89 Mod=3) - Specializing Source
#define MOV_EVGV_REG_WRAPPER(RegName, Spec)                                                    \
    FORCE_INLINE LogicFlow OpMov_EvGv_##RegName(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        return OpMov_EvGv_Reg_Internal<Spec>(s, o, u);                                         \
    }

MOV_EVGV_REG_WRAPPER(Eax, Specialized::RegEax)
MOV_EVGV_REG_WRAPPER(Ecx, Specialized::RegEcx)
MOV_EVGV_REG_WRAPPER(Edx, Specialized::RegEdx)
MOV_EVGV_REG_WRAPPER(Ebx, Specialized::RegEbx)
MOV_EVGV_REG_WRAPPER(Esp, Specialized::RegEsp)
MOV_EVGV_REG_WRAPPER(Ebp, Specialized::RegEbp)
MOV_EVGV_REG_WRAPPER(Esi, Specialized::RegEsi)
MOV_EVGV_REG_WRAPPER(Edi, Specialized::RegEdi)

FORCE_INLINE LogicFlow OpMov_EvGv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_EvGv_Reg_Internal<>(state, op, utlb);
}

// Named wrappers for OpMov_EvGv_Mem (Store) - Specializing Source
#define MOV_STORE_WRAPPER(RegName, Spec)                                                        \
    FORCE_INLINE LogicFlow OpMov_Store_##RegName(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        return OpMov_EvGv_Mem_Internal<Spec>(s, o, u);                                          \
    }

MOV_STORE_WRAPPER(Eax, Specialized::RegEax)
MOV_STORE_WRAPPER(Ecx, Specialized::RegEcx)
MOV_STORE_WRAPPER(Edx, Specialized::RegEdx)
MOV_STORE_WRAPPER(Ebx, Specialized::RegEbx)
MOV_STORE_WRAPPER(Esp, Specialized::RegEsp)
MOV_STORE_WRAPPER(Ebp, Specialized::RegEbp)
MOV_STORE_WRAPPER(Esi, Specialized::RegEsi)
MOV_STORE_WRAPPER(Edi, Specialized::RegEdi)

FORCE_INLINE LogicFlow OpMov_EvGv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_EvGv_Mem_Internal<>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpMov_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r16/32, r/m16/32 (0x8B)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint16_t val = *val_res;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        uint32_t val = *val_res;
        SetReg(state, reg, val);
    }
    return LogicFlow::Continue;
}

// Named wrappers for profiling visibility
FORCE_INLINE LogicFlow OpMov_Ebp_Esp(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEbp, Specialized::RegEsp>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpMov_Ecx_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEcx, Specialized::RegEax>(state, op, utlb);
}
FORCE_INLINE LogicFlow OpMov_Edx_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_GvEv_Reg_Internal<Specialized::RegEdx, Specialized::RegEax>(state, op, utlb);
}

// Generic fallback
FORCE_INLINE LogicFlow OpMov_GvEv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_GvEv_Reg_Internal<>(state, op, utlb);
}

// Named wrappers for OpMov_GvEv_Mem (Load) - Specializing Destination
#define MOV_LOAD_WRAPPER(RegName, Spec)                                                        \
    FORCE_INLINE LogicFlow OpMov_Load_##RegName(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        return OpMov_GvEv_Mem_Internal<Spec>(s, o, u);                                         \
    }

MOV_LOAD_WRAPPER(Eax, Specialized::RegEax)
MOV_LOAD_WRAPPER(Ecx, Specialized::RegEcx)
MOV_LOAD_WRAPPER(Edx, Specialized::RegEdx)
MOV_LOAD_WRAPPER(Ebx, Specialized::RegEbx)
MOV_LOAD_WRAPPER(Esp, Specialized::RegEsp)
MOV_LOAD_WRAPPER(Ebp, Specialized::RegEbp)
MOV_LOAD_WRAPPER(Esi, Specialized::RegEsi)
MOV_LOAD_WRAPPER(Edi, Specialized::RegEdi)

FORCE_INLINE LogicFlow OpMov_GvEv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    return OpMov_GvEv_Mem_Internal<>(state, op, utlb);
}

FORCE_INLINE LogicFlow OpXchg_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 86: XCHG r/m8, r8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t rm_val = *val_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t reg_val = GetReg8(state, reg);

    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, reg_val, utlb)) return LogicFlow::RetryMemoryOp;
    SetReg8(state, reg, rm_val);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r8, r/m8 (0x8A)
    // Load 8-bit ModRM into Reg
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;

    // Write to 8-bit register
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint32_t curr = *rptr;
    if (reg < 4) {
        curr = (curr & 0xFFFFFF00) | val;
    } else {
        curr = (curr & 0xFFFF00FF) | (val << 8);
    }
    *rptr = curr;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m8, imm8 (0xC6)
    uint8_t val = (uint8_t)op->imm;
    if (!WriteModRM<uint8_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_EvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m16/32, imm16/32 (0xC7)
    if (op->prefixes.flags.opsize) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, (uint16_t)op->imm, utlb))
            return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)op->imm, utlb))
            return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_RegImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // B8+reg: MOV r16/32, imm16/32
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)op->imm);
    } else {
        SetReg(state, reg, op->imm);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_RegImm8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // B0+reg: MOV r8, imm8
    uint8_t reg = op->modrm & 7;
    uint32_t val = op->imm & 0xFF;

    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint32_t curr = *rptr;

    if (reg < 4) {
        curr = (curr & 0xFFFFFF00) | val;
    } else {
        curr = (curr & 0xFFFF00FF) | (val << 8);
    }
    *rptr = curr;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A0: MOV AL, moffs8
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    auto val_res = ReadMem<uint8_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t* rptr = GetRegPtr(state, EAX);
    *rptr = (*rptr & 0xFFFFFF00) | *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Load_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A1: MOV EAX, moffs32
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadMem<uint16_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, linear, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, EAX, *val_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A2: MOV moffs8, AL
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    uint8_t val = GetReg8(state, EAX);
    if (!WriteMem<uint8_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Moffs_Store_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A3: MOV moffs32, EAX
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, EAX);
        if (!WriteMem<uint16_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    } else {
        uint32_t val = GetReg(state, EAX);
        if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, linear, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Rm_Sreg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8C: MOV r/m16, Sreg
    uint16_t val = 0;
    if (op->prefixes.flags.opsize) {
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)val, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Sreg_Rm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8E: MOV Sreg, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    (void)*val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovzx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B6: MOVZX r16/32, r/m8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovzx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B7: MOVZX r32, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint16_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovsx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BE: MOVSX r16/32, r/m8
    auto val_res = ReadModRM<uint8_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    int8_t val = (int8_t)*val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)(int16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovsx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BF: MOVSX r32, r/m16
    auto val_res = ReadModRM<uint16_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    int16_t val = (int16_t)*val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLea(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8D: LEA r16/32, m
    uint32_t offset = op->mem.disp;
    if (op->mem.base_offset != 32) {
        offset += state->ctx.regs[op->mem.base_offset >> 2];
    }
    if (op->mem.index_offset != 32) {
        offset += state->ctx.regs[op->mem.index_offset >> 2] << op->mem.scale;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (offset & 0xFFFF));
    } else {
        SetReg(state, reg, offset);
    }
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Simple)
// ------------------------------------------------------------------------------------------------

FORCE_INLINE LogicFlow OpPush_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 50+rd: PUSH r16/32
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        if (!Push<uint16_t, true>(state, (uint16_t)GetReg(state, reg), utlb, op)) return LogicFlow::RestartMemoryOp;
    } else {
        if (!Push<uint32_t, true>(state, GetReg(state, reg), utlb, op)) return LogicFlow::RestartMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPush_Imm32(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 68: PUSH imm32
    if (!Push<uint32_t, true>(state, op->imm, utlb, op)) return LogicFlow::RestartMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPush_Imm8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 6A: PUSH imm8
    int32_t val = (int32_t)(int8_t)op->imm;
    if (!Push<uint32_t, true>(state, val, utlb, op)) return LogicFlow::RestartMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPop_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // POP r16/32 (0x58+rd)
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        SetReg(state, reg, *val_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPop_Ev(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8F: POP r/m16/32
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop<uint16_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        if (!WriteModRM<uint16_t, OpOnTLBMiss::Retry>(state, op, *val_res, utlb)) return LogicFlow::RetryMemoryOp;
    } else {
        auto val_res = Pop<uint32_t, true>(state, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, *val_res, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (Complex) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

FORCE_INLINE LogicFlow OpPusha(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 60: PUSHA/PUSHAD
    // Failures (faults) will set status to Fault.
    // Return ExitOnCurrentEIP if fatal error, though status check loop should handle it.
    // Atomiciy: Do not update ESP until all memory writes succeed.
    if (op->prefixes.flags.opsize) {
        uint32_t esp = GetReg(state, ESP);
        uint16_t temp = (uint16_t)esp;
        // Pushes are: AX, CX, DX, BX, SP(temp), BP, SI, DI
        // Stack grows down: ESP-2, ESP-4, ...
        // We use mmu.write<T, false> directly to avoid updating ESP incrementally.

        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 2, (uint16_t)GetReg(state, EAX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 4, (uint16_t)GetReg(state, ECX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 6, (uint16_t)GetReg(state, EDX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 8, (uint16_t)GetReg(state, EBX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 10, temp, utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 12, (uint16_t)GetReg(state, EBP), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 14, (uint16_t)GetReg(state, ESI), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp - 16, (uint16_t)GetReg(state, EDI), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;

        SetReg(state, ESP, esp - 16);
    } else {
        uint32_t esp = GetReg(state, ESP);
        uint32_t temp = esp;
        // Pushes: EAX, ECX, EDX, EBX, ESP(temp), EBP, ESI, EDI
        // Stack: ESP-4, ESP-8, ...

        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 4, GetReg(state, EAX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 8, GetReg(state, ECX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 12, GetReg(state, EDX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 16, GetReg(state, EBX), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 20, temp, utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 24, GetReg(state, EBP), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 28, GetReg(state, ESI), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 32, GetReg(state, EDI), utlb, op))
            return LogicFlow::ExitOnCurrentEIP;

        SetReg(state, ESP, esp - 32);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpPopa(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 61: POPA/POPAD
    // Pops: DI, SI, BP, SP(skip), BX, DX, CX, AX
    // Atomic update implies we shouldn't change registers if memory access fails,
    // AND we shouldn't update ESP if it fails.

    if (op->prefixes.flags.opsize) {
        // POPA (16-bit)
        uint32_t esp = GetReg(state, ESP);

        auto di = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp, utlb, op);
        if (!di) return LogicFlow::ExitOnCurrentEIP;
        auto si = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 2, utlb, op);
        if (!si) return LogicFlow::ExitOnCurrentEIP;
        auto bp = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 4, utlb, op);
        if (!bp) return LogicFlow::ExitOnCurrentEIP;
        // sp (esp+6) is skipped
        auto bx = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 8, utlb, op);
        if (!bx) return LogicFlow::ExitOnCurrentEIP;
        auto dx = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 10, utlb, op);
        if (!dx) return LogicFlow::ExitOnCurrentEIP;
        auto cx = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 12, utlb, op);
        if (!cx) return LogicFlow::ExitOnCurrentEIP;
        auto ax = ReadMem<uint16_t, OpOnTLBMiss::Blocking>(state, esp + 14, utlb, op);
        if (!ax) return LogicFlow::ExitOnCurrentEIP;

        SetReg(state, EDI, (GetReg(state, EDI) & 0xFFFF0000) | *di);
        SetReg(state, ESI, (GetReg(state, ESI) & 0xFFFF0000) | *si);
        SetReg(state, EBP, (GetReg(state, EBP) & 0xFFFF0000) | *bp);
        SetReg(state, EBX, (GetReg(state, EBX) & 0xFFFF0000) | *bx);
        SetReg(state, EDX, (GetReg(state, EDX) & 0xFFFF0000) | *dx);
        SetReg(state, ECX, (GetReg(state, ECX) & 0xFFFF0000) | *cx);
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | *ax);

        SetReg(state, ESP, esp + 16);
    } else {
        // POPAD (32-bit)
        uint32_t esp = GetReg(state, ESP);

        auto di = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp, utlb, op);
        if (!di) return LogicFlow::ExitOnCurrentEIP;
        auto si = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 4, utlb, op);
        if (!si) return LogicFlow::ExitOnCurrentEIP;
        auto bp = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 8, utlb, op);
        if (!bp) return LogicFlow::ExitOnCurrentEIP;
        // sp (esp+12) is skipped
        auto bx = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 16, utlb, op);
        if (!bx) return LogicFlow::ExitOnCurrentEIP;
        auto dx = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 20, utlb, op);
        if (!dx) return LogicFlow::ExitOnCurrentEIP;
        auto cx = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 24, utlb, op);
        if (!cx) return LogicFlow::ExitOnCurrentEIP;
        auto ax = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp + 28, utlb, op);
        if (!ax) return LogicFlow::ExitOnCurrentEIP;

        SetReg(state, EDI, *di);
        SetReg(state, ESI, *si);
        SetReg(state, EBP, *bp);
        SetReg(state, EBX, *bx);
        SetReg(state, EDX, *dx);
        SetReg(state, ECX, *cx);
        SetReg(state, EAX, *ax);

        SetReg(state, ESP, esp + 32);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpEnter(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C8 iw ib: ENTER imm16, imm8
    // imm16 is alloc size, imm8 is nesting level
    uint16_t size = (uint16_t)op->imm;
    uint8_t level = (uint8_t)(op->imm >> 16);

    // We need to be careful with atomicity.
    uint32_t esp = GetReg(state, ESP);
    uint32_t ebp = GetReg(state, EBP);

    // 1. Push EBP
    if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, esp - 4, ebp, utlb, op)) return LogicFlow::ExitOnCurrentEIP;

    uint32_t frame_temp = esp - 4;   // Point to pushed EBP
    uint32_t current_esp = esp - 4;  // Track ESP virtually

    if (level > 0) {
        // level implies we push (level-1) pointers from previous frame
        // AND then push frame_temp.

        // Caution: logic loop copies from previous frame.
        // If we fail mid-way, ESP shouldn't be updated?
        // But we already wrote EBP. If we restart, we will write EBP again.
        // It's idempotent-ish if we haven't updated ESP yet.

        uint32_t iter_ebp = ebp;
        for (int i = 1; i < level; ++i) {
            iter_ebp -= 4;
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, iter_ebp, utlb, op);
            if (!val_res) return LogicFlow::ExitOnCurrentEIP;

            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, current_esp - 4, *val_res, utlb, op))
                return LogicFlow::ExitOnCurrentEIP;
            current_esp -= 4;
        }
        // Push FrameTemp
        if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, current_esp - 4, frame_temp, utlb, op))
            return LogicFlow::ExitOnCurrentEIP;
        current_esp -= 4;
    }

    // 3. MOV EBP, FrameTemp
    SetReg(state, EBP, frame_temp);

    // 4. SUB ESP, Size
    // Final ESP = current_esp - size
    SetReg(state, ESP, current_esp - size);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpLeave(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C9: LEAVE
    // MOV ESP, EBP
    // POP EBP

    uint32_t ebp = GetReg(state, EBP);
    // Read old EBP from stack at EBP
    auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Blocking>(state, ebp, utlb, op);
    if (!val_res) return LogicFlow::ExitOnCurrentEIP;

    SetReg(state, ESP, ebp + 4);
    SetReg(state, EBP, *val_res);
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------
FORCE_INLINE LogicFlow OpLahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9F: LAHF
    uint32_t flags = state->ctx.eflags;
    uint8_t ah = (flags & 0xD5) | 0x02;  // 0xD5 = 1101 0101 (Mask valid flags)
    uint32_t eax = GetReg(state, EAX);
    eax = (eax & 0xFFFF00FF) | (ah << 8);
    SetReg(state, EAX, eax);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9E: SAHF
    uint32_t eax = GetReg(state, EAX);
    uint8_t ah = (eax >> 8) & 0xFF;
    uint32_t flags = state->ctx.eflags;
    flags = (flags & ~0xFF) | (ah & 0xD5) | 0x02;
    state->ctx.eflags = flags;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 90+reg: XCHG EAX, r16/32
    uint8_t reg = op->modrm & 7;
    if (reg == 0) return LogicFlow::Continue;  // NOP

    if (op->prefixes.flags.opsize) {
        uint16_t val_eax = (uint16_t)GetReg(state, EAX);
        uint16_t val_reg = (uint16_t)GetReg(state, reg);
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | val_reg);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val_eax);
    } else {
        uint32_t val_eax = GetReg(state, EAX);
        uint32_t val_reg = GetReg(state, reg);
        SetReg(state, EAX, val_reg);
        SetReg(state, reg, val_eax);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // XCHG r/m16/32, r16/32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto rm_val_res = ReadModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!rm_val_res) return LogicFlow::ExitOnCurrentEIP;
        uint16_t rm_val = *rm_val_res;
        uint16_t reg_val = (uint16_t)GetReg(state, reg);

        if (!WriteModRM<uint16_t, OpOnTLBMiss::Blocking>(state, op, reg_val, utlb)) return LogicFlow::ExitOnCurrentEIP;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | rm_val);
    } else {
        auto rm_val_res = ReadModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, utlb);
        if (!rm_val_res) return LogicFlow::ExitOnCurrentEIP;
        uint32_t rm_val = *rm_val_res;
        uint32_t reg_val = GetReg(state, reg);

        if (!WriteModRM<uint32_t, OpOnTLBMiss::Blocking>(state, op, reg_val, utlb)) return LogicFlow::ExitOnCurrentEIP;
        SetReg(state, reg, rm_val);
    }
    return LogicFlow::Continue;
}

// ------------------------------------------------------------------------------------------------
// String Operations (LODS, SCAS, CMPS) - Use Blocking (fail_on_tlb_miss=false)
// ------------------------------------------------------------------------------------------------

template <typename T>
bool Helper_Movs(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&](uint32_t& ecx_ref) -> bool {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!val_res) return false;  // Fault
        T val = *val_res;

        if (!WriteMem<T, OpOnTLBMiss::Blocking>(state, edi, val, utlb, op)) return false;  // Fault

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
        return true;
    };

    if (op->prefixes.flags.rep) {
        while (GetReg(state, ECX) > 0) {
            uint32_t ecx = GetReg(state, ECX);
            if (!perform(ecx)) return false;

            if (state->status != EmuStatus::Running) return false;
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t ecx_dummy = 0;
        if (!perform(ecx_dummy)) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpMovs_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (!Helper_Movs<uint8_t>(state, op, utlb)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpMovs_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Movs<uint16_t>(state, op, utlb);
    else
        res = Helper_Movs<uint32_t>(state, op, utlb);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStos_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);
    int32_t step = df ? -1 : 1;
    auto perform = [&]() -> bool {
        uint32_t edi = GetReg(state, EDI);
        uint8_t val = (uint8_t)GetReg(state, EAX);
        if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, edi, val, utlb, op)) return false;
        SetReg(state, EDI, edi + step);
        return true;
    };
    if (op->prefixes.flags.rep) {
        while (GetReg(state, ECX) > 0) {
            if (!perform()) return LogicFlow::ExitOnCurrentEIP;
            if (state->status != EmuStatus::Running) break;
            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        if (!perform()) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpStos_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);
    int32_t step = (op->prefixes.flags.opsize ? 2 : 4);
    if (df) step = -step;

    auto perform = [&]() -> bool {
        uint32_t edi = GetReg(state, EDI);
        if (op->prefixes.flags.opsize) {
            uint16_t val = (uint16_t)GetReg(state, EAX);
            if (!WriteMem<uint16_t, OpOnTLBMiss::Blocking>(state, edi, val, utlb, op)) return false;
        } else {
            uint32_t val = GetReg(state, EAX);
            if (!WriteMem<uint32_t, OpOnTLBMiss::Blocking>(state, edi, val, utlb, op)) return false;
        }
        SetReg(state, EDI, edi + step);
        return true;
    };
    if (op->prefixes.flags.rep) {
        while (GetReg(state, ECX) > 0) {
            if (!perform()) return LogicFlow::ExitOnCurrentEIP;
            if (state->status != EmuStatus::Running) break;
            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        if (!perform()) return LogicFlow::ExitOnCurrentEIP;
    }
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Lods(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&](uint32_t& ecx_ref) -> bool {
        uint32_t esi = GetReg(state, ESI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!val_res) return false;
        T val = *val_res;

        if constexpr (sizeof(T) == 1) {
            uint32_t* rptr = GetRegPtr(state, EAX);
            *rptr = (*rptr & 0xFFFFFF00) | val;
        } else if constexpr (sizeof(T) == 2) {
            SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | val);
        } else {
            SetReg(state, EAX, val);
        }

        SetReg(state, ESI, esi + step);
        return true;
    };

    if (op->prefixes.flags.rep) {
        while (GetReg(state, ECX) > 0) {
            uint32_t ecx = GetReg(state, ECX);
            if (!perform(ecx)) return false;
            if (state->status != EmuStatus::Running) return false;
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t ecx_dummy = 0;
        if (!perform(ecx_dummy)) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpLods_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (!Helper_Lods<uint8_t>(state, op, utlb)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpLods_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Lods<uint16_t>(state, op, utlb);
    else
        res = Helper_Lods<uint32_t>(state, op, utlb);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Scas(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t edi = GetReg(state, EDI);
        // ES:[EDI]
        auto mem_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, edi, utlb, op);
        if (!mem_val_res) return false;
        T mem_val = *mem_val_res;

        T acc;
        if constexpr (sizeof(T) == 1)
            acc = (T)GetReg(state, EAX);
        else if constexpr (sizeof(T) == 2)
            acc = (T)(GetReg(state, EAX) & 0xFFFF);
        else
            acc = (T)GetReg(state, EAX);

        AluSub<T>(state, acc, mem_val);  // CMP uses Sub.

        SetReg(state, EDI, edi + step);
        return true;
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        while (GetReg(state, ECX) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);

            bool zf = (state->ctx.eflags & ZF_MASK);
            if (op->prefixes.flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (op->prefixes.flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpScas_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (!Helper_Scas<uint8_t>(state, op, utlb)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpScas_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Scas<uint16_t>(state, op, utlb);
    else
        res = Helper_Scas<uint32_t>(state, op, utlb);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

template <typename T>
bool Helper_Cmps(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() -> bool {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto src_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, src_addr, utlb, op);
        if (!src_val_res) return false;
        T src_val = *src_val_res;

        auto dst_val_res = ReadMem<T, OpOnTLBMiss::Blocking>(state, edi, utlb, op);  // ES:EDI
        if (!dst_val_res) return false;
        T dst_val = *dst_val_res;

        AluSub<T>(state, src_val, dst_val);

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
        return true;
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        while (GetReg(state, ECX) > 0) {
            if (!perform()) return false;
            if (state->status != EmuStatus::Running) return false;

            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);

            bool zf = (state->ctx.eflags & ZF_MASK);
            if (op->prefixes.flags.rep) {
                if (!zf) break;
            }  // REPE: Stop if ZF=0
            else if (op->prefixes.flags.repne) {
                if (zf) break;
            }  // REPNE: Stop if ZF=1
        }
    } else {
        if (!perform()) return false;
    }
    return true;
}

FORCE_INLINE LogicFlow OpCmps_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (!Helper_Cmps<uint8_t>(state, op, utlb)) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}
FORCE_INLINE LogicFlow OpCmps_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool res;
    if (op->prefixes.flags.opsize)
        res = Helper_Cmps<uint16_t>(state, op, utlb);
    else
        res = Helper_Cmps<uint32_t>(state, op, utlb);

    if (!res) return LogicFlow::ExitOnCurrentEIP;
    return LogicFlow::Continue;
}

}  // namespace op

void RegisterDataMovOps() {
    using namespace op;

    g_Handlers[0x87] = DispatchWrapper<OpXchg_EvGv>;  // XCHG r/m32, r32
    g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
    g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;

    // Specialized 32-bit MOV
    g_Handlers[OP_MOV_RR_STORE] = DispatchWrapper<OpMov_EvGv_Reg>;
    g_Handlers[OP_MOV_RM_STORE] = DispatchWrapper<OpMov_EvGv_Mem>;
    g_Handlers[OP_MOV_RR_LOAD] = DispatchWrapper<OpMov_GvEv_Reg>;
    g_Handlers[OP_MOV_MR_LOAD] = DispatchWrapper<OpMov_GvEv_Mem>;

    // Register Specialized Load/Store helpers
    // Store (EvGv_Mem)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        DispatchRegistrar<OpMov_Store_Eax>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        DispatchRegistrar<OpMov_Store_Ecx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        DispatchRegistrar<OpMov_Store_Edx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        DispatchRegistrar<OpMov_Store_Ebx>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpMov_Store_Esp>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpMov_Store_Ebp>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        DispatchRegistrar<OpMov_Store_Esi>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpMov_Store_Edi>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }

    // Load (GvEv_Mem)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        DispatchRegistrar<OpMov_Load_Eax>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        DispatchRegistrar<OpMov_Load_Ecx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        DispatchRegistrar<OpMov_Load_Edx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        DispatchRegistrar<OpMov_Load_Ebx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpMov_Load_Esp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpMov_Load_Ebp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        DispatchRegistrar<OpMov_Load_Esi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpMov_Load_Edi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }

    // Key MOV Patterns Specialization
    // 1. MOV EBP, ESP
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        c.rm_mask = 7;
        c.rm_val = 4;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Ebp_Esp>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }
    // 2. MOV ECX, EAX
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        c.rm_mask = 7;
        c.rm_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Ecx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }
    // 3. MOV EDX, EAX
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        c.rm_mask = 7;
        c.rm_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Edx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // EvGv_Reg Specializations (Dst=Reg, Src=Reg) - Store Reg to Reg?
    // OpMov_EvGv_Reg is for 0x89 (MOV r/m, r) -> if mod=3, it's Reg -> Reg.
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Eax>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 1;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ecx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 2;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Edx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 3;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ebx>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Esp>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Ebp>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 6;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Esi>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_EvGv_Edi>::RegisterSpecialized(OP_MOV_RR_STORE, c);
    }

    g_Handlers[0x88] = DispatchWrapper<OpMov_EbGb>;  // MOV r/m8, r8
    g_Handlers[0x8A] = DispatchWrapper<OpMov_GbEb>;  // MOV r8, r/m8
    g_Handlers[0xC6] = DispatchWrapper<OpMov_EbIb>;  // MOV r/m8, imm8
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0xB0 + i] = DispatchWrapper<OpMov_RegImm8>;
        g_Handlers[0xB8 + i] = DispatchWrapper<OpMov_RegImm>;
    }
    g_Handlers[0xC7] = DispatchWrapper<OpMov_EvIz>;  // MOV r/m32, imm32
    g_Handlers[0xA0] = DispatchWrapper<OpMov_Moffs_Load_Byte>;
    g_Handlers[0xA1] = DispatchWrapper<OpMov_Moffs_Load_Word>;
    g_Handlers[0xA2] = DispatchWrapper<OpMov_Moffs_Store_Byte>;
    g_Handlers[0xA3] = DispatchWrapper<OpMov_Moffs_Store_Word>;
    g_Handlers[0xA4] = DispatchWrapper<OpMovs_Byte>;
    g_Handlers[0xA5] = DispatchWrapper<OpMovs_Word>;
    g_Handlers[0xAA] = DispatchWrapper<OpStos_Byte>;
    g_Handlers[0xAB] = DispatchWrapper<OpStos_Word>;
    g_Handlers[0xAC] = DispatchWrapper<OpLods_Byte>;
    g_Handlers[0xAD] = DispatchWrapper<OpLods_Word>;
    g_Handlers[0xAE] = DispatchWrapper<OpScas_Byte>;
    g_Handlers[0xAF] = DispatchWrapper<OpScas_Word>;
    g_Handlers[0xA6] = DispatchWrapper<OpCmps_Byte>;
    g_Handlers[0xA7] = DispatchWrapper<OpCmps_Word>;
    g_Handlers[0x60] = DispatchWrapper<OpPusha>;
    g_Handlers[0x61] = DispatchWrapper<OpPopa>;
    g_Handlers[0xC8] = DispatchWrapper<OpEnter>;
    g_Handlers[0xC9] = DispatchWrapper<OpLeave>;
    g_Handlers[0x86] = DispatchWrapper<OpXchg_EbGb>;
    for (int i = 1; i < 8; ++i) g_Handlers[0x90 + i] = DispatchWrapper<OpXchg_Reg>;
    g_Handlers[0x9F] = DispatchWrapper<OpLahf>;
    g_Handlers[0x9E] = DispatchWrapper<OpSahf>;
    g_Handlers[0x8D] = DispatchWrapper<OpLea>;
    for (int i = 0; i < 8; ++i) g_Handlers[0x50 + i] = DispatchWrapper<OpPush_Reg>;
    g_Handlers[0x68] = DispatchWrapper<OpPush_Imm32>;
    g_Handlers[0x6A] = DispatchWrapper<OpPush_Imm8>;
    for (int i = 0; i < 8; ++i) g_Handlers[0x58 + i] = DispatchWrapper<OpPop_Reg>;
    g_Handlers[0x8C] = DispatchWrapper<OpMov_Rm_Sreg>;
    g_Handlers[0x8E] = DispatchWrapper<OpMov_Sreg_Rm>;
    g_Handlers[0x8F] = DispatchWrapper<OpPop_Ev>;
    g_Handlers[0x1B6] = DispatchWrapper<OpMovzx_Byte>;
    g_Handlers[0x1B7] = DispatchWrapper<OpMovzx_Word>;
    g_Handlers[0x1BE] = DispatchWrapper<OpMovsx_Byte>;
    g_Handlers[0x1BF] = DispatchWrapper<OpMovsx_Word>;
}

}  // namespace fiberish