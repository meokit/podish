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
        WriteModRM16(state, op, val, utlb);
    } else {
        uint32_t val = GetReg(state, reg);
        WriteModRM32(state, op, val, utlb);
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
        if constexpr (FixedSrc == 0) val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1) val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2) val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3) val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4) val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5) val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6) val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7) val = state->ctx.regs[7];
    } else {
        uint8_t src = (op->modrm >> 3) & 7;
        val = GetReg(state, src);
    }

    // Write Destination (ModRM.RM)
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0) state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1) state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2) state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3) state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4) state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5) state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6) state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7) state->ctx.regs[7] = val;
    } else {
        uint8_t dst = op->modrm & 7;
        SetReg(state, dst, val);
    }
}

// Named wrappers for OpMov_EvGv_Reg (0x89 Mod=3) - Specializing Source
static void OpMov_EvGv_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEax>(s, o, u); }
static void OpMov_EvGv_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEcx>(s, o, u); }
static void OpMov_EvGv_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEdx>(s, o, u); }
static void OpMov_EvGv_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEbx>(s, o, u); }
static void OpMov_EvGv_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEsp>(s, o, u); }
static void OpMov_EvGv_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEbp>(s, o, u); }
static void OpMov_EvGv_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEsi>(s, o, u); }
static void OpMov_EvGv_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Reg_Template<Specialized::RegEdi>(s, o, u); }

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
        if constexpr (FixedReg == 0) val = state->ctx.regs[0]; // EAX
        else if constexpr (FixedReg == 1) val = state->ctx.regs[1]; // ECX
        else if constexpr (FixedReg == 2) val = state->ctx.regs[2]; // EDX
        else if constexpr (FixedReg == 3) val = state->ctx.regs[3]; // EBX
        else if constexpr (FixedReg == 4) val = state->ctx.regs[4]; // ESP
        else if constexpr (FixedReg == 5) val = state->ctx.regs[5]; // EBP
        else if constexpr (FixedReg == 6) val = state->ctx.regs[6]; // ESI
        else if constexpr (FixedReg == 7) val = state->ctx.regs[7]; // EDI
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        val = GetReg(state, reg);
    }
    
    uint32_t addr = ComputeLinearAddress(state, op);
    state->mmu.write<uint32_t>(addr, val, utlb);
}

// Named wrappers for OpMov_EvGv_Mem (Store) - Specializing Source
static void OpMov_Store_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEax>(s, o, u); }
static void OpMov_Store_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEcx>(s, o, u); }
static void OpMov_Store_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEdx>(s, o, u); }
static void OpMov_Store_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEbx>(s, o, u); }
static void OpMov_Store_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEsp>(s, o, u); }
static void OpMov_Store_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEbp>(s, o, u); }
static void OpMov_Store_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEsi>(s, o, u); }
static void OpMov_Store_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_EvGv_Mem<Specialized::RegEdi>(s, o, u); }

static FORCE_INLINE void OpMov_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r16/32, r/m16/32 (0x8B)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t val = ReadModRM16(state, op, utlb);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        uint32_t val = ReadModRM32(state, op, utlb);
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
        if constexpr (FixedSrc == 0) val = state->ctx.regs[0];
        else if constexpr (FixedSrc == 1) val = state->ctx.regs[1];
        else if constexpr (FixedSrc == 2) val = state->ctx.regs[2];
        else if constexpr (FixedSrc == 3) val = state->ctx.regs[3];
        else if constexpr (FixedSrc == 4) val = state->ctx.regs[4];
        else if constexpr (FixedSrc == 5) val = state->ctx.regs[5];
        else if constexpr (FixedSrc == 6) val = state->ctx.regs[6];
        else if constexpr (FixedSrc == 7) val = state->ctx.regs[7];
    } else {
        uint8_t src = op->modrm & 7;
        val = GetReg(state, src);
    }

    // Write Destination
    if constexpr (DstSpec >= Specialized::Reg0 && DstSpec <= Specialized::Reg7) {
        constexpr uint8_t FixedDst = (uint8_t)DstSpec - (uint8_t)Specialized::Reg0;
        if constexpr (FixedDst == 0) state->ctx.regs[0] = val;
        else if constexpr (FixedDst == 1) state->ctx.regs[1] = val;
        else if constexpr (FixedDst == 2) state->ctx.regs[2] = val;
        else if constexpr (FixedDst == 3) state->ctx.regs[3] = val;
        else if constexpr (FixedDst == 4) state->ctx.regs[4] = val;
        else if constexpr (FixedDst == 5) state->ctx.regs[5] = val;
        else if constexpr (FixedDst == 6) state->ctx.regs[6] = val;
        else if constexpr (FixedDst == 7) state->ctx.regs[7] = val;
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


static FORCE_INLINE void OpMov_GvEv_Mem(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // Specialized: MOV r32, [mem]
    // ModRM.Reg is the Destination Register
    uint32_t addr = ComputeLinearAddress(state, op);
    uint32_t val = state->mmu.read<uint32_t>(addr, utlb);
    
    if constexpr (S >= Specialized::Reg0 && S <= Specialized::Reg7) {
        constexpr uint8_t FixedReg = (uint8_t)S - (uint8_t)Specialized::Reg0;
        // Compiler optimization: GetReg(state, FixedReg) becomes pure structure access
        if constexpr (FixedReg == 0) state->ctx.regs[0] = val; // EAX
        else if constexpr (FixedReg == 1) state->ctx.regs[1] = val; // ECX
        else if constexpr (FixedReg == 2) state->ctx.regs[2] = val; // EDX
        else if constexpr (FixedReg == 3) state->ctx.regs[3] = val; // EBX
        else if constexpr (FixedReg == 4) state->ctx.regs[4] = val; // ESP
        else if constexpr (FixedReg == 5) state->ctx.regs[5] = val; // EBP
        else if constexpr (FixedReg == 6) state->ctx.regs[6] = val; // ESI
        else if constexpr (FixedReg == 7) state->ctx.regs[7] = val; // EDI
    } else {
        uint8_t reg = (op->modrm >> 3) & 7;
        SetReg(state, reg, val);
    }
}

// Named wrappers for OpMov_GvEv_Mem (Load) - Specializing Destination
static void OpMov_Load_Eax(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEax>(s, o, u); }
static void OpMov_Load_Ecx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEcx>(s, o, u); }
static void OpMov_Load_Edx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEdx>(s, o, u); }
static void OpMov_Load_Ebx(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEbx>(s, o, u); }
static void OpMov_Load_Esp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEsp>(s, o, u); }
static void OpMov_Load_Ebp(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEbp>(s, o, u); }
static void OpMov_Load_Esi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEsi>(s, o, u); }
static void OpMov_Load_Edi(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpMov_GvEv_Mem<Specialized::RegEdi>(s, o, u); }

static FORCE_INLINE void OpMov_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    WriteModRM8(state, op, val, utlb);
}

static FORCE_INLINE void OpMov_GbEb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // MOV r8, r/m8 (0x8A)
    // Load 8-bit ModRM into Reg
    uint8_t val = ReadModRM8(state, op, utlb);
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
    WriteModRM8(state, op, val, utlb);
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

static FORCE_INLINE void OpMov_EvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C7: MOV r/m16/32, imm16/32
    if (op->prefixes.flags.opsize) {
        WriteModRM16(state, op, (uint16_t)op->imm, utlb);
    } else {
        WriteModRM32(state, op, op->imm, utlb);
    }
}

static FORCE_INLINE void OpMov_Moffs_Load(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // A0: MOV AL, moffs8 (Byte)
    // A1: MOV EAX, moffs32 (Word/Dword)
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);

    // A0: extra=0, A1: extra=1
    if (op->extra == 0) {  // A0
        uint8_t val = state->mmu.read<uint8_t>(linear, utlb);
        uint32_t* rptr = GetRegPtr(state, EAX);
        *rptr = (*rptr & 0xFFFFFF00) | val;
    } else {  // A1
        if (op->prefixes.flags.opsize) {
            uint16_t val = state->mmu.read<uint16_t>(linear, utlb);
            SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | val);
        } else {
            uint32_t val = state->mmu.read<uint32_t>(linear, utlb);
            SetReg(state, EAX, val);
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
        state->mmu.write<uint8_t>(linear, val, utlb);
    } else {  // A3
        if (op->prefixes.flags.opsize) {
            uint16_t val = (uint16_t)GetReg(state, EAX);
            state->mmu.write<uint16_t>(linear, val, utlb);
        } else {
            uint32_t val = GetReg(state, EAX);
            state->mmu.write<uint32_t>(linear, val, utlb);
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
        // selector = state->mmu.read<uint16_t>(addr, utlb);
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
        state->mmu.write<uint16_t>(addr, val, utlb);
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

            uint32_t copied = state->mmu.copy_block(esi + src_base, edi, total_bytes);

            // Calculate how many FULL items were processed
            uint32_t items_processed = copied / sizeof(T);
            uint32_t bytes_processed = items_processed * sizeof(T);

            // Update Regs
            SetReg(state, ESI, esi + bytes_processed);
            SetReg(state, EDI, edi + bytes_processed);
            SetReg(state, ECX, ecx - items_processed);

            // If we stopped early (fault), we leave state consistent at the fault
            // point.
            return;
        }

        // Slow path / Reverse path (DF=1)
        while (ecx > 0) {
            uint32_t esi = GetReg(state, ESI);
            uint32_t edi = GetReg(state, EDI);

            // DS:ESI -> ES:EDI
            // For now assume flat model (DS=0, ES=0)
            uint32_t src_addr = esi + GetSegmentBase(state, op);

            T val = state->mmu.read<T>(src_addr, utlb);
            if (state->status != EmuStatus::Running) break;

            state->mmu.write<T>(edi, val, utlb);
            if (state->status != EmuStatus::Running) break;

            SetReg(state, ESI, esi + step);
            SetReg(state, EDI, edi + step);

            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);

        T val = state->mmu.read<T>(src_addr, utlb);
        state->mmu.write<T>(edi, val, utlb);

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    }
}

static FORCE_INLINE void OpMovs_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) { Helper_Movs<uint8_t>(state, op, utlb); }

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
        uint32_t ecx = GetReg(state, ECX);
        while (ecx > 0) {
            uint32_t edi = GetReg(state, EDI);
            // Dest ES:EDI
            state->mmu.write<T>(edi, val, utlb);

            SetReg(state, EDI, edi + step);

            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t edi = GetReg(state, EDI);
        state->mmu.write<T>(edi, val, utlb);
        SetReg(state, EDI, edi + step);
    }
}

static FORCE_INLINE void OpStos_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) { Helper_Stos<uint8_t>(state, op, utlb); }

static FORCE_INLINE void OpStos_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize) {
        Helper_Stos<uint16_t>(state, op, utlb);
    } else {
        Helper_Stos<uint32_t>(state, op, utlb);
    }
}

static FORCE_INLINE void OpMovzx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B6: MOVZX r16/32, r/m8
    uint8_t val = ReadModRM8(state, op, utlb);
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
}

static FORCE_INLINE void OpMovzx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B7: MOVZX r16/32, r/m16
    // Note: MOVZX r16, m16 is effectively MOV r16, m16
    uint16_t val = ReadModRM16(state, op, utlb);
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        SetReg(state, reg, (uint32_t)val);
    }
}

static FORCE_INLINE void OpMovsx_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BE: MOVSX r16/32, r/m8
    uint8_t val = ReadModRM8(state, op, utlb);
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | (uint16_t)(int16_t)(int8_t)val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)(int8_t)val);
    }
}

static FORCE_INLINE void OpMovsx_Word(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BF: MOVSX r16/32, r/m16
    // Note: MOVSX r16, m16 is effectively MOV r16, m16
    uint16_t val = ReadModRM16(state, op, utlb);
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        SetReg(state, reg, (uint32_t)(int32_t)(int16_t)val);
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
        Push16(state, (uint16_t)GetReg(state, reg), utlb);
    } else {
        Push32(state, GetReg(state, reg), utlb);
    }
}

static FORCE_INLINE void OpPush_Imm(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // PUSH imm32 (0x68) or PUSH imm8 (0x6A)
    // Decoder already extracted imm to op->imm
    uint32_t val = op->imm;
    if (op->extra == 0xA) {  // 6A
        val = (int32_t)(int8_t)val;
    }
    Push32(state, val, utlb);
}

static FORCE_INLINE void OpPop_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // POP r16/32 (0x58+rd)
    uint8_t reg = op->modrm & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t val = Pop16(state, utlb);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
    } else {
        uint32_t val = Pop32(state, utlb);
        SetReg(state, reg, val);
    }
}

static FORCE_INLINE void OpXchg_EvGv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // XCHG r/m16/32, r16/32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t reg_val = (uint16_t)GetReg(state, reg);
        uint16_t rm_val = ReadModRM16(state, op, utlb);
        WriteModRM16(state, op, reg_val, utlb);
        SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | rm_val);
    } else {
        uint32_t reg_val = GetReg(state, reg);
        uint32_t rm_val = ReadModRM32(state, op, utlb);
        WriteModRM32(state, op, reg_val, utlb);
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
        T val = state->mmu.read<T>(src_addr, utlb);

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
        uint32_t ecx = GetReg(state, ECX);
        while (ecx > 0) {
            perform(ecx);
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t ecx_dummy = 0;
        perform(ecx_dummy);
    }
}

static FORCE_INLINE void OpLods_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) { Helper_Lods<uint8_t>(state, op, utlb); }
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
        T mem_val = state->mmu.read<T>(edi, utlb);
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
        uint32_t ecx = GetReg(state, ECX);

        while (ecx > 0) {
            perform();
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

static FORCE_INLINE void OpScas_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) { Helper_Scas<uint8_t>(state, op, utlb); }
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
        T src_val = state->mmu.read<T>(src_addr, utlb);
        T dst_val = state->mmu.read<T>(edi, utlb);  // ES:EDI

        AluSub<T>(state, src_val, dst_val);

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    };

    if (op->prefixes.flags.rep || op->prefixes.flags.repne) {
        uint32_t ecx = GetReg(state, ECX);
        while (ecx > 0) {
            perform();
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

static FORCE_INLINE void OpCmps_Byte(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) { Helper_Cmps<uint8_t>(state, op, utlb); }
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
        // PUSHA (16-bit)
        uint16_t temp = GetReg(state, ESP) & 0xFFFF;
        Push16(state, (uint16_t)GetReg(state, EAX), utlb);
        Push16(state, (uint16_t)GetReg(state, ECX), utlb);
        Push16(state, (uint16_t)GetReg(state, EDX), utlb);
        Push16(state, (uint16_t)GetReg(state, EBX), utlb);
        Push16(state, temp, utlb);
        Push16(state, (uint16_t)GetReg(state, EBP), utlb);
        Push16(state, (uint16_t)GetReg(state, ESI), utlb);
        Push16(state, (uint16_t)GetReg(state, EDI), utlb);
    } else {
        // PUSHAD (32-bit)
        uint32_t temp = GetReg(state, ESP);
        Push32(state, GetReg(state, EAX), utlb);
        Push32(state, GetReg(state, ECX), utlb);
        Push32(state, GetReg(state, EDX), utlb);
        Push32(state, GetReg(state, EBX), utlb);
        Push32(state, temp, utlb);
        Push32(state, GetReg(state, EBP), utlb);
        Push32(state, GetReg(state, ESI), utlb);
        Push32(state, GetReg(state, EDI), utlb);
    }
}

static FORCE_INLINE void OpPopa(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 61: POPA/POPAD
    if (op->prefixes.flags.opsize) {
        // POPA (16-bit)
        SetReg(state, EDI, (GetReg(state, EDI) & 0xFFFF0000) | Pop16(state, utlb));
        SetReg(state, ESI, (GetReg(state, ESI) & 0xFFFF0000) | Pop16(state, utlb));
        SetReg(state, EBP, (GetReg(state, EBP) & 0xFFFF0000) | Pop16(state, utlb));
        Pop16(state, utlb);  // Skip SP
        SetReg(state, EBX, (GetReg(state, EBX) & 0xFFFF0000) | Pop16(state, utlb));
        SetReg(state, EDX, (GetReg(state, EDX) & 0xFFFF0000) | Pop16(state, utlb));
        SetReg(state, ECX, (GetReg(state, ECX) & 0xFFFF0000) | Pop16(state, utlb));
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | Pop16(state, utlb));
    } else {
        // POPAD (32-bit)
        SetReg(state, EDI, Pop32(state, utlb));
        SetReg(state, ESI, Pop32(state, utlb));
        SetReg(state, EBP, Pop32(state, utlb));
        Pop32(state, utlb);  // Skip ESP
        SetReg(state, EBX, Pop32(state, utlb));
        SetReg(state, EDX, Pop32(state, utlb));
        SetReg(state, ECX, Pop32(state, utlb));
        SetReg(state, EAX, Pop32(state, utlb));
    }
}

static FORCE_INLINE void OpEnter(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C8 iw ib: ENTER imm16, imm8
    // imm16 is alloc size, imm8 is nesting level
    uint16_t size = op->imm & 0xFFFF;
    uint8_t level = (op->imm >> 16) & 0x1F;
    
    // 1. Push EBP
    Push32(state, GetReg(state, EBP), utlb);
    uint32_t frame_temp = GetReg(state, ESP); // ESP after push

    // 2. If Level > 0, push previous frame pointers
    if (level > 0) {
        // We need to read from the *current* EBP (which points to start of previous frame)
        // and follow the chain 'level-1' times.
        uint32_t ebp = GetReg(state, EBP);
        
        for (uint8_t i = 1; i < level; ++i) {
            ebp -= 4; // Point to pointer to prev frame? 
            // On stack: [OldEBP] [RetAddr]
            // ENTER L, 1: Push EBP. FrameTemp = ESP. 
            // If Level > 0:
            //   For i=1 to Level-1:
            //     EBP = EBP - 4
            //     Push [EBP]
            //   Push FrameTemp
            
            ebp -= 4;
            uint32_t val = state->mmu.read<uint32_t>(ebp, utlb);
            Push32(state, val, utlb);
        }
        // Push FrameTemp
        Push32(state, frame_temp, utlb);
    }
    
    // 3. MOV EBP, FrameTemp
    SetReg(state, EBP, frame_temp);
    
    // 4. SUB ESP, Size
    SetReg(state, ESP, GetReg(state, ESP) - size);
}

static FORCE_INLINE void OpLeave(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C9: LEAVE
    // MOV ESP, EBP
    uint32_t ebp = GetReg(state, EBP);
    SetReg(state, ESP, ebp);
    // POP EBP
    SetReg(state, EBP, Pop32(state, utlb));
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpLahf(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 9F: LAHF
    // AH <- EFLAGS(SF:ZF:0:AF:0:PF:1:CF)
    uint32_t flags = state->ctx.eflags;
    uint8_t ah = (flags & 0xFF);
    // Flags: SF(7), ZF(6), 0(5), AF(4), 0(3), PF(2), 1(1), CF(0)
    // EFLAGS low byte matches this format exactly (Reserved bits 1, 3, 5 are
    // fixed). Bit 1 is 1. Bit 3 is 0. Bit 5 is 0. So simple copy is correct.

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
    // Mask: SF, ZF, AF, PF, CF. And reserved bits.
    // SF(7), ZF(6), AF(4), PF(2), CF(0)
    // Reserved: 5, 3, 1.
    // Intel: "The SF, ZF, AF, PF, and CF flags are set... Reserved bits 1, 3,
    // and 5... are unaffected" -> Wait. "LOADS SF, ZF, AF, PF, and CF ... from
    // AH" "Bits 1, 3, and 5 of flags are set to 1, 0, 0" (In EFLAGS? Or does it
    // say that AH has them?) Actually SAHF loads the fixed values too? "bits 1,
    // 3, and 5 ... are unaffected" (Some sources say unaffected, some say loaded)
    // Intel SDM: "EFLAGS(SF:ZF:0:AF:0:PF:1:CF) <- AH" ... suggests it writes
    // fixed values? Usually it replaces the low byte entirely.

    // uint32_t mask = 0xD5; // Unused
    // Update only these?
    flags = (flags & ~0xFF) | (ah & 0xFF);
    // Note: This overwrites reserved bits 1,3,5 with AH's values.
    // AH usually has them set correctly from LAHF.
    // If not, we might set bit 1 to 0.
    // Let's force bit 1 to 1.
    flags |= 2;
    state->ctx.eflags = flags;
}

static FORCE_INLINE void OpXchg_EbGb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 86: XCHG r/m8, r8
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint8_t reg_val = (reg < 4) ? (*rptr & 0xFF) : ((*rptr >> 8) & 0xFF);

    uint8_t rm_val = ReadModRM8(state, op, utlb);

    // Write reg_val to RM
    WriteModRM8(state, op, reg_val, utlb);

    // Write rm_val to Reg
    uint32_t mask = (reg < 4) ? 0xFF : 0xFF00;
    uint32_t shift = (reg < 4) ? 0 : 8;
    *rptr = (*rptr & ~mask) | (rm_val << shift);
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
    g_Handlers[OP_MOV_RR_LOAD]  = DispatchWrapper<OpMov_GvEv_Reg>;
    g_Handlers[OP_MOV_MR_LOAD]  = DispatchWrapper<OpMov_GvEv_Mem<>>;

    


    
    // Register for all 8 registers
    { SpecCriteria c; c.reg_mask=7; c.reg_val=0; DispatchRegistrar<OpMov_Load_Eax>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=0; DispatchRegistrar<OpMov_Store_Eax>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=1; DispatchRegistrar<OpMov_Load_Ecx>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=1; DispatchRegistrar<OpMov_Store_Ecx>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=2; DispatchRegistrar<OpMov_Load_Edx>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=2; DispatchRegistrar<OpMov_Store_Edx>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=3; DispatchRegistrar<OpMov_Load_Ebx>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=3; DispatchRegistrar<OpMov_Store_Ebx>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=4; DispatchRegistrar<OpMov_Load_Esp>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=4; DispatchRegistrar<OpMov_Store_Esp>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=5; DispatchRegistrar<OpMov_Load_Ebp>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=5; DispatchRegistrar<OpMov_Store_Ebp>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=6; DispatchRegistrar<OpMov_Load_Esi>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=6; DispatchRegistrar<OpMov_Store_Esi>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    { SpecCriteria c; c.reg_mask=7; c.reg_val=7; DispatchRegistrar<OpMov_Load_Edi>::RegisterSpecialized(OP_MOV_MR_LOAD, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=7; DispatchRegistrar<OpMov_Store_Edi>::RegisterSpecialized(OP_MOV_RM_STORE, c); }

    // --- Key MOV Patterns Specialization ---
    
    // 1. MOV EBP, ESP (Stack Frame Construction)
    // Opcode 8B /r. Mod=3. Reg=EBP(5), RM=ESP(4).
    {
        SpecCriteria c; 
        c.reg_mask = 0x7; c.reg_val = 5; // Dst = EBP
        c.rm_mask = 0x7;  c.rm_val = 4;  // Src = ESP
        c.mod_mask = 3;   c.mod_val = 3; // Register Mode
        DispatchRegistrar<OpMov_Ebp_Esp>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // 2. MOV ECX, EAX (Loop/Param)
    // Reg=ECX(1), RM=EAX(0)
    {
        SpecCriteria c;
        c.reg_mask = 0x7; c.reg_val = 1; // Dst = ECX
        c.rm_mask = 0x7;  c.rm_val = 0;  // Src = EAX
        c.mod_mask = 3;   c.mod_val = 3;
        DispatchRegistrar<OpMov_Ecx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }

    // 3. MOV EDX, EAX (Mul/Div Prep)
    // Reg=EDX(2), RM=EAX(0)
    {
        SpecCriteria c;
        c.reg_mask = 0x7; c.reg_val = 2; // Dst = EDX
        c.rm_mask = 0x7;  c.rm_val = 0;  // Src = EAX
        c.mod_mask = 3;   c.mod_val = 3;
        DispatchRegistrar<OpMov_Edx_Eax>::RegisterSpecialized(OP_MOV_RR_LOAD, c);
    }


    #define REG_EVGV(ridx, FuncName) \
    { \
        SpecCriteria c; \
        c.reg_mask = 0x7; c.reg_val = ridx; \
        c.mod_mask = 3;   c.mod_val = 3; \
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
    
    { SpecCriteria c; c.reg_mask=7; c.reg_val=0; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Eax>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=1; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Ecx>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=2; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Edx>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=3; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Ebx>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=4; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Esp>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=5; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Ebp>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=6; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Esi>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
    { SpecCriteria c; c.reg_mask=7; c.reg_val=7; c.mod_mask=3; c.mod_val=3; DispatchRegistrar<OpMov_EvGv_Edi>::RegisterSpecialized(OP_MOV_RR_STORE, c); }
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
    g_Handlers[0x8E] = DispatchWrapper<OpMov_Sreg_Rm>;
    g_Handlers[0x8C] = DispatchWrapper<OpMov_Rm_Sreg>;
    g_Handlers[0x1B6] = DispatchWrapper<OpMovzx_Byte>;
    g_Handlers[0x1B7] = DispatchWrapper<OpMovzx_Word>;
    g_Handlers[0x1BE] = DispatchWrapper<OpMovsx_Byte>;
    g_Handlers[0x1BF] = DispatchWrapper<OpMovsx_Word>;
}

}  // namespace fiberish
