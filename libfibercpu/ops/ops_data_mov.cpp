// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

static FORCE_INLINE void OpMov_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m16/32, r16/32 (0x89)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t val = (uint16_t)GetReg(state, reg);
        if (!WriteModRM16(state, op, val, utlb)) return;
    } else {
        uint32_t val = GetReg(state, reg);
        if (!WriteModRM32(state, op, val, utlb)) return;
    }
}

template <Specialized SrcSpec = Specialized::None, Specialized DstSpec = Specialized::None>
static FORCE_INLINE void OpMov_EvGv_Reg_Template(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
}

// Named wrappers for OpMov_EvGv_Reg (0x89 Mod=3) - Specializing Source
static void OpMov_EvGv_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEax>(s, o, u);
}
static void OpMov_EvGv_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEcx>(s, o, u);
}
static void OpMov_EvGv_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEdx>(s, o, u);
}
static void OpMov_EvGv_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEbx>(s, o, u);
}
static void OpMov_EvGv_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEsp>(s, o, u);
}
static void OpMov_EvGv_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEbp>(s, o, u);
}
static void OpMov_EvGv_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEsi>(s, o, u);
}
static void OpMov_EvGv_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Reg_Template<Specialized::RegEdi>(s, o, u);
}

static FORCE_INLINE void OpMov_EvGv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    OpMov_EvGv_Reg_Template<>(state, op, utlb);
}

template <Specialized S = Specialized::None>
static FORCE_INLINE void OpMov_EvGv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV [mem], r32
    // ModRM.Reg is the Source Register
    uint32_t val;
    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        // Compiler optimization: GetReg(state, FixedReg) becomes pure structure access
        if constexpr (FixedReg == 0)
            val = state->ctx.regs[0];  // EAX
        else if constexpr (FixedReg == 1)
            val = state->ctx.regs[1];  // ECX
        else if constexpr (FixedReg == 2)
            val = state->ctx.regs[2];  // EDX
        else if constexpr (FixedReg == 3)
            val = state->ctx.regs[3];  // EBX
        else if constexpr (FixedReg == 4)
            val = state->ctx.regs[4];  // ESP
        else if constexpr (FixedReg == 5)
            val = state->ctx.regs[5];  // EBP
        else if constexpr (FixedReg == 6)
            val = state->ctx.regs[6];  // ESI
        else if constexpr (FixedReg == 7)
            val = state->ctx.regs[7];  // EDI
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        val = GetReg(state, reg);
    }

    uint32_t addr = ComputeLinearAddress(state, op);
    if (!state->mmu.write<uint32_t>(state, addr, val, utlb, op)) return;
}

// Named wrappers for OpMov_EvGv_Mem (Store) - Specializing Source
static void OpMov_Store_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEax>(s, o, u);
}
static void OpMov_Store_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEcx>(s, o, u);
}
static void OpMov_Store_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEdx>(s, o, u);
}
static void OpMov_Store_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEbx>(s, o, u);
}
static void OpMov_Store_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEsp>(s, o, u);
}
static void OpMov_Store_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEbp>(s, o, u);
}
static void OpMov_Store_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEsi>(s, o, u);
}
static void OpMov_Store_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_EvGv_Mem<Specialized::RegEdi>(s, o, u);
}

static FORCE_INLINE void OpMov_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r16/32, r/m16/32 (0x8B)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = ReadModRM16(state, op, utlb);
        if (!val_res) return;
        uint16_t val = *val_res;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        auto val_res = ReadModRM32(state, op, utlb);
        if (!val_res) return;
        uint32_t val = *val_res;
        SetReg(state, reg, val);
    }
}

template <Specialized DstSpec = Specialized::None, Specialized SrcSpec = Specialized::None>
static FORCE_INLINE void OpMov_GvEv_Reg_Template(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
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
}

// Named wrappers for profiling visibility
static void OpMov_Ebp_Esp(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    OpMov_GvEv_Reg_Template<Specialized::RegEbp, Specialized::RegEsp>(state, op, utlb);
}

static void OpMov_Ecx_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    OpMov_GvEv_Reg_Template<Specialized::RegEcx, Specialized::RegEax>(state, op, utlb);
}

static void OpMov_Edx_Eax(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    OpMov_GvEv_Reg_Template<Specialized::RegEdx, Specialized::RegEax>(state, op, utlb);
}

// Generic fallback
static FORCE_INLINE void OpMov_GvEv_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    OpMov_GvEv_Reg_Template<>(state, op, utlb);
}

template <Specialized S = Specialized::None>
static FORCE_INLINE void OpMov_OaMa(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A3: MOV m32, EAX
    uint32_t addr = ComputeLinearAddress(state, op);
    uint32_t val = GetReg(state, EAX);
    if (!state->mmu.write<uint32_t>(state, addr, val, utlb, op)) return;
}

template <Specialized S = Specialized::None>
static FORCE_INLINE void OpMov_GvEv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV r32, [mem]
    uint32_t addr = ComputeLinearAddress(state, op);
    auto val_res = state->mmu.read<uint32_t>(state, addr, utlb, op);
    if (!val_res) return;
    uint32_t val = *val_res;

    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        // Compiler optimization: GetReg(state, FixedReg) becomes pure structure access
        if constexpr (FixedReg == 0)
            state->ctx.regs[0] = val;  // EAX
        else if constexpr (FixedReg == 1)
            state->ctx.regs[1] = val;  // ECX
        else if constexpr (FixedReg == 2)
            state->ctx.regs[2] = val;  // EDX
        else if constexpr (FixedReg == 3)
            state->ctx.regs[3] = val;  // EBX
        else if constexpr (FixedReg == 4)
            state->ctx.regs[4] = val;  // ESP
        else if constexpr (FixedReg == 5)
            state->ctx.regs[5] = val;  // EBP
        else if constexpr (FixedReg == 6)
            state->ctx.regs[6] = val;  // ESI
        else if constexpr (FixedReg == 7)
            state->ctx.regs[7] = val;  // EDI
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, val);
    }
}

// Named wrappers for OpMov_GvEv_Mem (Load) - Specializing Destination
static void OpMov_Load_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEax>(s, o, u);
}
static void OpMov_Load_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEcx>(s, o, u);
}
static void OpMov_Load_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEdx>(s, o, u);
}
static void OpMov_Load_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEbx>(s, o, u);
}
static void OpMov_Load_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEsp>(s, o, u);
}
static void OpMov_Load_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEbp>(s, o, u);
}
static void OpMov_Load_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEsi>(s, o, u);
}
static void OpMov_Load_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    OpMov_GvEv_Mem<Specialized::RegEdi>(s, o, u);
}

static FORCE_INLINE void OpXchg_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 86: XCHG r/m8, r8
    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
    uint8_t rm_val = *val_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t reg_val = GetReg8(state, reg);

    if (!WriteModRM8(state, op, reg_val, utlb)) return;
    SetReg8(state, reg, rm_val);
}

static FORCE_INLINE void OpMov_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    if (!WriteModRM8(state, op, val, utlb)) return;
}

static FORCE_INLINE void OpMov_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r8, r/m8 (0x8A)
    // Load 8-bit ModRM into Reg
    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
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
}

static FORCE_INLINE void OpMov_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m8, imm8 (0xC6)
    uint8_t val = (uint8_t)op->imm;
    if (!WriteModRM8(state, op, val, utlb)) return;
}

static FORCE_INLINE void OpMov_EvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m16/32, imm16/32 (0xC7)
    if (op->prefixes.flags.opsize) {
        if (!WriteModRM16(state, op, (uint16_t)op->imm, utlb)) return;
    } else {
        if (!WriteModRM32(state, op, (uint32_t)op->imm, utlb)) return;
    }
}

static FORCE_INLINE void OpMov_RegImm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // B8+reg: MOV r16/32, imm16/32
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)op->imm);
    } else {
        SetReg(state, reg, op->imm);
    }
}

static FORCE_INLINE void OpMov_RegImm8(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // B0+reg: MOV r8, imm8
    // Reg coding: 0=AL, 1=CL, 2=DL, 3=BL, 4=AH, 5=CH, 6=DH, 7=BH
    uint8_t reg = op->modrm & 7;
    uint32_t val = op->imm & 0xFF;

    // Read-Modify-Write 32-bit reg
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint32_t curr = *rptr;

    if (reg < 4) {
        curr = (curr & 0xFFFFFF00) | val;
    } else {
        curr = (curr & 0xFFFF00FF) | (val << 8);
    }
    *rptr = curr;
}

static FORCE_INLINE void OpMov_Moffs_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A0: MOV AL, moffs8 (Byte)
    // A1: MOV EAX, moffs32 (Word/Dword)
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);

    // A0: extra=0, A1: extra=1
    if (op->extra == 0) {  // A0
        auto val_res = state->mmu.read<uint8_t>(state, linear, utlb, op);
        if (!val_res) return;
        uint32_t* rptr = GetRegPtr(state, EAX);
        *rptr = (*rptr & 0xFFFFFF00) | *val_res;
    } else {  // A1
        if (op->prefixes.flags.opsize) {
            auto val_res = state->mmu.read<uint16_t>(state, linear, utlb, op);
            if (!val_res) return;
            SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | *val_res);
        } else {
            auto val_res = state->mmu.read<uint32_t>(state, linear, utlb, op);
            if (!val_res) return;
            SetReg(state, EAX, *val_res);
        }
    }
}

static FORCE_INLINE void OpMov_Moffs_Store(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A2: MOV moffs8, AL
    // A3: MOV moffs32, EAX
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);

    // A2: extra=2, A3: extra=3
    if (op->extra == 2) {  // A2
        uint8_t val = GetReg8(state, EAX);
        if (!state->mmu.write<uint8_t>(state, linear, val, utlb, op)) return;
    } else {  // A3
        if (op->prefixes.flags.opsize) {
            uint16_t val = (uint16_t)GetReg(state, EAX);
            if (!state->mmu.write<uint16_t>(state, linear, val, utlb, op)) return;
        } else {
            uint32_t val = GetReg(state, EAX);
            if (!state->mmu.write<uint32_t>(state, linear, val, utlb, op)) return;
        }
    }
}

static FORCE_INLINE void OpMov_Sreg_Rm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8E /r: MOV Sreg, r/m16
    uint8_t sreg_idx = (op->modrm >> 3) & 7;
    // 0=ES, 1=CS, 2=SS, 3=DS, 4=FS, 5=GS
    if (sreg_idx == 1) {
        // Loading CS -> #UD
        return;
    }
    if (sreg_idx > 5) return;

    // uint16_t selector = 0;

    // Read Source (Rm) - always 16-bit
    if (op->modrm >= 0xC0) {
        (void)0;  // TODO
                  // uint8_t rm = op->modrm & 7;
                  // selector = (uint16_t)GetReg(state, rm);
    } else {
        // uint32_t addr = ComputeLinearAddress(state, op);
        // auto selector_res = state->mmu.read<uint16_t>(addr, utlb);
        // if (!selector_res) return;
        // selector = *selector_res;
        if (state->status != EmuStatus::Running) return;
    }

    // We do not store selectors in current Context.
    // Just ensure side-effects (memory read fault) happen.
    // Base is preserved (assumed set by syscall).
}

static FORCE_INLINE void OpMov_Rm_Sreg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8C /r: MOV r/m16, Sreg
    uint8_t sreg_idx = (op->modrm >> 3) & 7;
    if (sreg_idx > 5) return;

    // Mock values since we don't store selectors
    uint16_t val = 0;
    switch (sreg_idx) {
        case 1:
            val = 0x73;
            break;  // CS
        case 2:
            val = 0x7B;
            break;  // SS
        case 3:
            val = 0x7B;
            break;  // DS
        case 0:
            val = 0x7B;
            break;  // ES
        default:
            val = 0;
            break;
    }

    if (op->modrm >= 0xC0) {
        uint8_t rm = op->modrm & 7;
        // Write to register (16-bit or 32-bit zero ext?)
        // Operand size attribute determines? default is 32-bit for 32-bit mode?
        // "If the destination is a 32-bit register, the 16-bit selector is
        // zero-extended" We assume 32-bit destination unless opsize prefix.
        if (op->prefixes.flags.opsize) {
            uint32_t* rptr = GetRegPtr(state, rm);
            *rptr = (*rptr & 0xFFFF0000) | val;
        } else {
            SetReg(state, rm, (uint32_t)val);
        }
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        if (!state->mmu.write<uint16_t>(state, addr, val, utlb, op)) return;
    }
}

template <typename T>
void Helper_Movs(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    // REP handling
    if (op->prefixes.flags.rep) {
        uint32_t ecx = GetReg(state, ECX);
        if (ecx == 0) return;

        // Optimization for DF=0 (Forward Copy)
        // Disable optimization if mem_hook is active to ensure proper trace
        // granularity
        if (!df && !state->mem_hook) {
            uint32_t total_bytes = ecx * sizeof(T);
            uint32_t esi = GetReg(state, ESI);
            uint32_t edi = GetReg(state, EDI);
            uint32_t src_base = GetSegmentBase(state, op);

            auto res = state->mmu.copy_block(state, esi + src_base, edi, total_bytes, op);
            if (!res) {
                // If optimized path fails, fallback to slow loop for precise fault handling
                // or just return if the fault is already recorded.
                // For now, let's just abort as the MMU already handled the fault state.
                return;
            }
            uint32_t bytes_processed = total_bytes;
            uint32_t items_processed = ecx;

            // Update Regs
            SetReg(state, ESI, esi + bytes_processed);
            SetReg(state, EDI, edi + bytes_processed);
            SetReg(state, ECX, ecx - items_processed);

            // If we stopped early (fault), we leave state consistent at the fault
            // point.
            if (items_processed != ecx) return;
        }

        // Slow path / Reverse path (DF=1) / Remainder
        while (GetReg(state, ECX) > 0) {
            uint32_t esi = GetReg(state, ESI);
            uint32_t edi = GetReg(state, EDI);
            uint32_t ecx = GetReg(state, ECX);

            // DS:ESI -> ES:EDI
            // For now assume flat model (DS=0, ES=0)
            uint32_t src_addr = esi + GetSegmentBase(state, op);

            auto val_res = state->mmu.read<T>(state, src_addr, utlb, op);
            if (!val_res) return;  // Abort on fault
            T val = *val_res;

            if (!state->mmu.write<T>(state, edi, val, utlb, op)) return;  // Abort on fault

            SetReg(state, ESI, esi + step);
            SetReg(state, EDI, edi + step);

            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);

        auto val_res = state->mmu.read<T>(state, src_addr, utlb, op);
        if (!val_res) return;  // Abort on fault
        T val = *val_res;

        if (!state->mmu.write<T>(state, edi, val, utlb, op)) return;  // Abort on fault

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    }
}

static FORCE_INLINE void OpMovs_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    Helper_Movs<uint8_t>(state, op, utlb);
}

static FORCE_INLINE void OpMovs_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize) {
        Helper_Movs<uint16_t>(state, op, utlb);
    } else {
        Helper_Movs<uint32_t>(state, op, utlb);
    }
}

template <typename T>
void Helper_Stos(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    // Get Value (AL/AX/EAX)
    T val;
    if constexpr (sizeof(T) == 1)
        val = (T)GetReg(state, EAX);  // AL
    else if constexpr (sizeof(T) == 2)
        val = (T)(GetReg(state, EAX) & 0xFFFF);  // AX
    else
        val = (T)GetReg(state, EAX);  // EAX

    if (op->prefixes.flags.rep) {
        // Optimization: Could use memset logic if T=byte/word/struct pattern
        // For now simple loop
        while (GetReg(state, ECX) > 0) {
            uint32_t ecx = GetReg(state, ECX);
            uint32_t edi = GetReg(state, EDI);
            // Dest ES:EDI
            if (!state->mmu.write<T>(state, edi, val, utlb, op)) return;  // Abort on fault

            SetReg(state, EDI, edi + step);

            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t edi = GetReg(state, EDI);
        if (!state->mmu.write<T>(state, edi, val, utlb, op)) return;  // Abort on fault
        SetReg(state, EDI, edi + step);
    }
}

static FORCE_INLINE void OpStos_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    Helper_Stos<uint8_t>(state, op, utlb);
}

static FORCE_INLINE void OpStos_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize) {
        Helper_Stos<uint16_t>(state, op, utlb);
    } else {
        Helper_Stos<uint32_t>(state, op, utlb);
    }
}

static FORCE_INLINE void OpMovzx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B6: MOVZX r16/32, r/m8
    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
    uint8_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
}

static FORCE_INLINE void OpMovzx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B7: MOVZX r16/32, r/m16
    auto val_res = ReadModRM16(state, op, utlb);
    if (!val_res) return;
    uint16_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
}

static FORCE_INLINE void OpMovsx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BE: MOVSX r16/32, r/m8
    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
    int8_t val = (int8_t)*val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)(int16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
}

static FORCE_INLINE void OpMovsx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BF: MOVSX r16/32, r/m16
    auto val_res = ReadModRM16(state, op, utlb);
    if (!val_res) return;
    int16_t val = (int16_t)*val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)val);
    }
}

static FORCE_INLINE void OpLea(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // LEA r16/32, m (0x8D)
    uint32_t addr = ComputeEA(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)addr);
    } else {
        SetReg(state, reg, addr);
    }
}

static FORCE_INLINE void OpPush_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PUSH r16/32 (0x50+rd)
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        if (!Push16(state, (uint16_t)GetReg(state, reg), utlb, op)) return;
    } else {
        if (!Push32(state, GetReg(state, reg), utlb, op)) return;
    }
}

static FORCE_INLINE void OpPush_Imm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PUSH imm32 (0x68) or PUSH imm8 (0x6A)
    // Decoder already extracted imm to op->imm
    uint32_t val = op->imm;
    if (op->extra == 0xA) {  // 6A
        val = (int32_t)(int8_t)val;
    }
    if (!Push32(state, val, utlb, op)) return;
}

static FORCE_INLINE void OpPop_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // POP r16/32 (0x58+rd)
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop16(state, utlb, op);
        if (!val_res) return;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | *val_res);
    } else {
        auto val_res = Pop32(state, utlb, op);
        if (!val_res) return;
        SetReg(state, reg, *val_res);
    }
}

static FORCE_INLINE void OpPop_Ev(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 8F: POP r/m16/32
    if (op->prefixes.flags.opsize) {
        auto val_res = Pop16(state, utlb, op);
        if (!val_res) return;
        if (!WriteModRM16(state, op, *val_res, utlb)) return;
    } else {
        auto val_res = Pop32(state, utlb, op);
        if (!val_res) return;
        if (!WriteModRM32(state, op, *val_res, utlb)) return;
    }
}

static FORCE_INLINE void OpXchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // XCHG r/m16/32, r16/32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        auto rm_val_res = ReadModRM16(state, op, utlb);
        if (!rm_val_res) return;
        uint16_t rm_val = *rm_val_res;
        uint16_t reg_val = (uint16_t)GetReg(state, reg);

        if (!WriteModRM16(state, op, reg_val, utlb)) return;
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | rm_val);
    } else {
        auto rm_val_res = ReadModRM32(state, op, utlb);
        if (!rm_val_res) return;
        uint32_t rm_val = *rm_val_res;
        uint32_t reg_val = GetReg(state, reg);

        if (!WriteModRM32(state, op, reg_val, utlb)) return;
        SetReg(state, reg, rm_val);
    }
}

// ------------------------------------------------------------------------------------------------
// String Operations (LODS, SCAS, CMPS)
// ------------------------------------------------------------------------------------------------

template <typename T>
void Helper_Lods(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&](uint32_t& ecx_ref) {
        uint32_t esi = GetReg(state, ESI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto val_res = state->mmu.read<T>(state, src_addr, utlb, op);
        if (!val_res) {
            state->status = EmuStatus::Fault;  // Set fault status
            return;
        }
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
    };

    if (op->prefixes.flags.rep) {
        // LODS with REP loads RCX times? Usually invalid but technically loopable
        while (GetReg(state, ECX) > 0) {
            uint32_t ecx = GetReg(state, ECX);
            perform(ecx);
            if (state->status != EmuStatus::Running) break;
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t ecx_dummy = 0;
        perform(ecx_dummy);
    }
}

static FORCE_INLINE void OpLods_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    Helper_Lods<uint8_t>(state, op, utlb);
}
static FORCE_INLINE void OpLods_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize)
        Helper_Lods<uint16_t>(state, op, utlb);
    else
        Helper_Lods<uint32_t>(state, op, utlb);
}

template <typename T>
void Helper_Scas(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() {
        uint32_t edi = GetReg(state, EDI);
        // ES:[EDI]
        auto mem_val_res = state->mmu.read<T>(state, edi, utlb, op);
        if (!mem_val_res) {
            state->status = EmuStatus::Fault;  // Set fault status
            return;
        }
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
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {  // REPE (F3) or REPNE (F2)
        while (GetReg(state, ECX) > 0) {
            perform();
            if (state->status != EmuStatus::Running) break;

            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);

            bool zf = (state->ctx.eflags & ZF_MASK);
            if (op->prefixes.flags.rep) {  // REPE: Stop if ZF=0
                if (!zf) break;
            } else if (op->prefixes.flags.repne) {  // REPNE: Stop if ZF=1
                if (zf) break;
            }
        }
    } else {
        perform();
    }
}

static FORCE_INLINE void OpScas_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    Helper_Scas<uint8_t>(state, op, utlb);
}
static FORCE_INLINE void OpScas_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize)
        Helper_Scas<uint16_t>(state, op, utlb);
    else
        Helper_Scas<uint32_t>(state, op, utlb);
}

template <typename T>
void Helper_Cmps(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        auto src_val_res = state->mmu.read<T>(state, src_addr, utlb, op);
        if (!src_val_res) {
            state->status = EmuStatus::Fault;  // Set fault status
            return;
        }
        T src_val = *src_val_res;

        auto dst_val_res = state->mmu.read<T>(state, edi, utlb, op);  // ES:EDI
        if (!dst_val_res) {
            state->status = EmuStatus::Fault;  // Set fault status
            return;
        }
        T dst_val = *dst_val_res;

        AluSub<T>(state, src_val, dst_val);

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        while (GetReg(state, ECX) > 0) {
            perform();
            if (state->status != EmuStatus::Running) break;

            uint32_t ecx = GetReg(state, ECX);
            ecx--;
            SetReg(state, ECX, ecx);

            bool zf = (state->ctx.eflags & ZF_MASK);
            if (op->prefixes.flags.rep) {  // REPE: Stop if ZF=0
                if (!zf) break;
            } else if (op->prefixes.flags.repne) {  // REPNE: Stop if ZF=1
                if (zf) break;
            }
        }
    } else {
        perform();
    }
}

static FORCE_INLINE void OpCmps_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    Helper_Cmps<uint8_t>(state, op, utlb);
}
static FORCE_INLINE void OpCmps_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize)
        Helper_Cmps<uint16_t>(state, op, utlb);
    else
        Helper_Cmps<uint32_t>(state, op, utlb);
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (PUSHA, POPA, ENTER, LEAVE)
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpPusha(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 60: PUSHA/PUSHAD
    if (op->prefixes.flags.opsize) {
        uint16_t temp = (uint16_t)GetReg(state, ESP);
        if (!Push16(state, (uint16_t)GetReg(state, EAX), utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, ECX), utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, EDX), utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, EBX), utlb, op)) return;
        if (!Push16(state, temp, utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, EBP), utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, ESI), utlb, op)) return;
        if (!Push16(state, (uint16_t)GetReg(state, EDI), utlb, op)) return;
    } else {
        uint32_t temp = GetReg(state, ESP);
        if (!Push32(state, GetReg(state, EAX), utlb, op)) return;
        if (!Push32(state, GetReg(state, ECX), utlb, op)) return;
        if (!Push32(state, GetReg(state, EDX), utlb, op)) return;
        if (!Push32(state, GetReg(state, EBX), utlb, op)) return;
        if (!Push32(state, temp, utlb, op)) return;
        if (!Push32(state, GetReg(state, EBP), utlb, op)) return;
        if (!Push32(state, GetReg(state, ESI), utlb, op)) return;
        if (!Push32(state, GetReg(state, EDI), utlb, op)) return;
    }
}

static FORCE_INLINE void OpPopa(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 61: POPA/POPAD
    if (op->prefixes.flags.opsize) {
        // POPA (16-bit)
        auto di = Pop16(state, utlb, op);
        if (!di) return;
        auto si = Pop16(state, utlb, op);
        if (!si) return;
        auto bp = Pop16(state, utlb, op);
        if (!bp) return;
        auto sp = Pop16(state, utlb, op);
        if (!sp) return;  // Skip SP
        auto bx = Pop16(state, utlb, op);
        if (!bx) return;
        auto dx = Pop16(state, utlb, op);
        if (!dx) return;
        auto cx = Pop16(state, utlb, op);
        if (!cx) return;
        auto ax = Pop16(state, utlb, op);
        if (!ax) return;

        SetReg(state, EDI, (GetReg(state, EDI) & 0xFFFF0000) | *di);
        SetReg(state, ESI, (GetReg(state, ESI) & 0xFFFF0000) | *si);
        SetReg(state, EBP, (GetReg(state, EBP) & 0xFFFF0000) | *bp);
        SetReg(state, EBX, (GetReg(state, EBX) & 0xFFFF0000) | *bx);
        SetReg(state, EDX, (GetReg(state, EDX) & 0xFFFF0000) | *dx);
        SetReg(state, ECX, (GetReg(state, ECX) & 0xFFFF0000) | *cx);
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | *ax);
    } else {
        // POPAD (32-bit)
        auto di = Pop32(state, utlb, op);
        if (!di) return;
        auto si = Pop32(state, utlb, op);
        if (!si) return;
        auto bp = Pop32(state, utlb, op);
        if (!bp) return;
        auto sp = Pop32(state, utlb, op);
        if (!sp) return;  // Skip ESP
        auto bx = Pop32(state, utlb, op);
        if (!bx) return;
        auto dx = Pop32(state, utlb, op);
        if (!dx) return;
        auto cx = Pop32(state, utlb, op);
        if (!cx) return;
        auto ax = Pop32(state, utlb, op);
        if (!ax) return;

        SetReg(state, EDI, *di);
        SetReg(state, ESI, *si);
        SetReg(state, EBP, *bp);
        SetReg(state, EBX, *bx);
        SetReg(state, EDX, *dx);
        SetReg(state, ECX, *cx);
        SetReg(state, EAX, *ax);
    }
}

static FORCE_INLINE void OpEnter(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C8 iw ib: ENTER imm16, imm8
    // imm16 is alloc size, imm8 is nesting level
    uint16_t size = (uint16_t)op->imm;
    uint8_t level = (uint8_t)(op->imm >> 16);

    // 1. Push EBP
    if (!Push32(state, GetReg(state, EBP), utlb, op)) return;

    uint32_t frame_temp = GetReg(state, ESP);

    if (level > 0) {
        uint32_t ebp = GetReg(state, EBP);
        for (int i = 1; i < level; ++i) {
            ebp -= 4;  // Move to previous frame pointer
            auto val_res = state->mmu.read<uint32_t>(state, ebp, utlb, op);
            if (!val_res) return;
            if (!Push32(state, *val_res, utlb, op)) return;
        }
        // Push FrameTemp
        if (!Push32(state, frame_temp, utlb, op)) return;
    }

    // 3. MOV EBP, FrameTemp
    SetReg(state, EBP, frame_temp);

    // 4. SUB ESP, Size
    SetReg(state, ESP, GetReg(state, ESP) - size);
}

static FORCE_INLINE void OpLeave(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C9: LEAVE
    // MOV ESP, EBP
    SetReg(state, ESP, GetReg(state, EBP));
    // POP EBP
    auto val_res = Pop32(state, utlb, op);
    if (!val_res) return;
    SetReg(state, EBP, *val_res);
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------
static FORCE_INLINE void OpLahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9F: LAHF
    // AH <- EFLAGS(SF:ZF:0:AF:0:PF:1:CF)

    uint32_t flags = state->ctx.eflags;

    uint8_t ah = (flags & 0xD5) | 0x02;  // 0xD5 = 1101 0101 (Mask valid flags)

    uint32_t eax = GetReg(state, EAX);
    eax = (eax & 0xFFFF00FF) | (ah << 8);
    SetReg(state, EAX, eax);
}

static FORCE_INLINE void OpSahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9E: SAHF
    // EFLAGS(SF:ZF:0:AF:0:PF:1:CF) <- AH

    uint32_t eax = GetReg(state, EAX);
    uint8_t ah = (eax >> 8) & 0xFF;

    uint32_t flags = state->ctx.eflags;

    flags = (flags & ~0xFF) | (ah & 0xD5) | 0x02;

    state->ctx.eflags = flags;
}

static FORCE_INLINE void OpXchg_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 90+reg: XCHG EAX, r16/32
    uint8_t reg = op->modrm & 7;
    if (reg == 0) return;  // NOP

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
}

void RegisterDataMovOps() {
    g_Handlers[0x87] = DispatchWrapper<OpXchg_EvGv>;  // XCHG r/m32, r32
    g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
    g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;

    // Specialized 32-bit MOV
    g_Handlers[OP_MOV_RR_STORE] = DispatchWrapper<OpMov_EvGv_Reg>;
    g_Handlers[OP_MOV_RM_STORE] = DispatchWrapper<OpMov_EvGv_Mem<>>;
    g_Handlers[OP_MOV_RR_LOAD] = DispatchWrapper<OpMov_GvEv_Reg>;
    g_Handlers[OP_MOV_MR_LOAD] = DispatchWrapper<OpMov_GvEv_Mem<>>;

    // Register for all 8 registers
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 0;
        DispatchRegistrar<OpMov_Load_Eax>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
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
        DispatchRegistrar<OpMov_Load_Ecx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Edx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Ebx>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Esp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Ebp>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Esi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
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
        DispatchRegistrar<OpMov_Load_Edi>::RegisterSpecialized(OP_MOV_MR_LOAD, c);
    }
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpMov_Store_Edi>::RegisterSpecialized(OP_MOV_RM_STORE, c);
    }

    // --- Key MOV Patterns Specialization ---

    // 1. MOV EBP, ESP (Stack Frame Construction)
    // Opcode 8B /r. Mod=3. Reg=EBP(5), RM=ESP(4).
    {
        SpecCriteria c;
        c.reg_mask = 0x7;
        c.reg_val = 5;  // Dst = EBP
        c.rm_mask = 0x7;
        c.rm_val = 4;  // Src = ESP
        c.mod_mask = 3;
        c.mod_val = 3;  // Register Mode
        DispatchRegistrar<OpMov_Ebp_Esp>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // 2. MOV ECX, EAX (Loop/Param)
    // Reg=ECX(1), RM=EAX(0)
    {
        SpecCriteria c;
        c.reg_mask = 0x7;
        c.reg_val = 1;  // Dst = ECX
        c.rm_mask = 0x7;
        c.rm_val = 0;  // Src = EAX
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Ecx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // 3. MOV EDX, EAX (Mul/Div Prep)
    // Reg=EDX(2), RM=EAX(0)
    {
        SpecCriteria c;
        c.reg_mask = 0x7;
        c.reg_val = 2;  // Dst = EDX
        c.rm_mask = 0x7;
        c.rm_val = 0;  // Src = EAX
        c.mod_mask = 3;
        c.mod_val = 3;
        DispatchRegistrar<OpMov_Edx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

#define REG_EVGV(ridx, FuncName)                                              \
    {                                                                         \
        SpecCriteria c;                                                       \
        c.reg_mask = 0x7;                                                     \
        c.reg_val = ridx;                                                     \
        c.mod_mask = 3;                                                       \
        c.mod_val = 3;                                                        \
        DispatchRegistrar<FuncName>::RegisterSpecialized(OP_MOV_RR_STORE, c); \
    }

    REG_EVGV(0, OpMov_EvGv_Eax);
    REG_EVGV(1, OpMov_EvGv_Ecx);
    REG_EVGV(2, OpMov_EvGv_Edx);
    REG_EVGV(3, OpMov_EvGv_Ebx);
    REG_EVGV(4, OpMov_EvGv_Esp);
    REG_EVGV(5, OpMov_EvGv_Ebp);
    REG_EVGV(6, OpMov_EvGv_Esi);
    REG_EVGV(7, OpMov_EvGv_Edi);

    // Fix index 2 and 7 duplication in macro above (wrote Edi twice)
    // Let's rewrite cleaner without macro to be safe and explicit.

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
    g_Handlers[0xA0] = DispatchWrapper<OpMov_Moffs_Load>;
    g_Handlers[0xA1] = DispatchWrapper<OpMov_Moffs_Load>;
    g_Handlers[0xA2] = DispatchWrapper<OpMov_Moffs_Store>;
    g_Handlers[0xA3] = DispatchWrapper<OpMov_Moffs_Store>;
    g_Handlers[0xA4] = DispatchWrapper<OpMovs_Byte>;
    g_Handlers[0xA5] = DispatchWrapper<OpMovs_Word>;
    g_Handlers[0xAA] = DispatchWrapper<OpStos_Byte>;
    g_Handlers[0xAB] = DispatchWrapper<OpStos_Word>;
    g_Handlers[0xAC] = DispatchWrapper<OpLods_Byte>;
    g_Handlers[0xAD] = DispatchWrapper<OpLods_Word>;
    g_Handlers[0xAE] = DispatchWrapper<OpScas_Byte>;
    g_Handlers[0xAF] = DispatchWrapper<OpScas_Word>;
    g_Handlers[0xA6] = DispatchWrapper<OpCmps_Byte>;
    g_Handlers[0xA7] = DispatchWrapper<OpCmps_Word>;  // Wait opcodes are A6/A7
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
    g_Handlers[0x68] = DispatchWrapper<OpPush_Imm>;
    g_Handlers[0x6A] = DispatchWrapper<OpPush_Imm>;
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
