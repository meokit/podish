#include "ops.h"
#include "state.h"
#include "exec_utils.h"
#include <cstdio>
#include <iostream>
#include <cmath>
#include <simde/x86/sse.h>

namespace x86emu {

uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4) return val & 0xFF;
    else return (val >> 8) & 0xFF;
}

void OpNop(EmuState* state, DecodedOp* op) {
    // No Operation
}

void OpNotImplemented(EmuState* state, DecodedOp* op) {
    // Log failure
    fprintf(stderr, "[Sim] Opcode Not Implemented (Idx: %04X)\n", op->handler_index);
}

void OpMov_EvGv(EmuState* state, DecodedOp* op) {
    // MOV r/m32, r32 (0x89)
    // Store Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t val = GetReg(state, reg);
    WriteModRM32(state, op, val);
}

void OpMov_GvEv(EmuState* state, DecodedOp* op) {
    // MOV r32, r/m32 (0x8B)
    // Load ModRM into Reg
    uint32_t val = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, val);
}

void OpMov_RegImm(EmuState* state, DecodedOp* op) {
    // B8+reg: MOV r32, imm32
    uint8_t reg = op->handler_index & 7;
    SetReg(state, reg, op->imm);
}

void OpMov_RegImm8(EmuState* state, DecodedOp* op) {
    // B0+reg: MOV r8, imm8
    // Reg coding: 0=AL, 1=CL, 2=DL, 3=BL, 4=AH, 5=CH, 6=DH, 7=BH
    uint8_t reg = op->handler_index & 7;
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

void OpMov_EvIz(EmuState* state, DecodedOp* op) {
    // C7: MOV r/m32, imm32
    WriteModRM32(state, op, op->imm);
}

void OpMov_Moffs_Load(EmuState* state, DecodedOp* op) {
    // A0: MOV AL, moffs8 (Byte)
    // A1: MOV EAX, moffs32 (Word/Dword)
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    
    if ((op->handler_index & 1) == 0) { // A0
        uint8_t val = state->mmu.read<uint8_t>(linear);
        uint32_t* rptr = GetRegPtr(state, EAX);
        *rptr = (*rptr & 0xFFFFFF00) | val;
    } else { // A1
        uint32_t val = state->mmu.read<uint32_t>(linear);
        SetReg(state, EAX, val);
    }
}

void OpMov_Moffs_Store(EmuState* state, DecodedOp* op) {
    // A2: MOV moffs8, AL
    // A3: MOV moffs32, EAX
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);
    
    uint32_t val = GetReg(state, EAX);
    
    if ((op->handler_index & 1) == 0) { // A2
        state->mmu.write<uint8_t>(linear, (uint8_t)val);
    } else { // A3
        state->mmu.write<uint32_t>(linear, val);
    }
}

void OpMovzx_Byte(EmuState* state, DecodedOp* op) {
    // 0F B6: MOVZX r32, r/m8
    uint8_t val = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)val);
}

void OpMovzx_Word(EmuState* state, DecodedOp* op) {
    // 0F B7: MOVZX r32, r/m16
    uint16_t val = ReadModRM16(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)val);
}

void OpMovsx_Byte(EmuState* state, DecodedOp* op) {
    // 0F BE: MOVSX r32, r/m8
    uint8_t val = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)(int32_t)(int8_t)val);
}

void OpMovsx_Word(EmuState* state, DecodedOp* op) {
    // 0F BF: MOVSX r32, r/m16
    uint16_t val = ReadModRM16(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)(int32_t)(int16_t)val);
}

// ------------------------------------------------------------------------------------------------
// Arithmetic
// ------------------------------------------------------------------------------------------------

void OpAdd_EvGv(EmuState* state, DecodedOp* op) {
    // 01: ADD r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAdd(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpAnd_EvGv(EmuState* state, DecodedOp* op) {
    // 21: AND r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAnd(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpOr_EvGv(EmuState* state, DecodedOp* op) {
    // 09: OR r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluOr(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpSub_EvGv(EmuState* state, DecodedOp* op) {
    // 29: SUB r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluSub(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpXor_EvGv(EmuState* state, DecodedOp* op) {
    // 31: XOR r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluXor(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpJmp_Rel(EmuState* state, DecodedOp* op) {
    // E9: JMP rel32
    state->ctx.eip += op->imm;
}

void OpCmp_EvGv(EmuState* state, DecodedOp* op) {
    // 39: CMP r/m32, r32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        AluSub(state, dest, src);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
        AluSub(state, dest, src);
    }
}

void OpCmp_EbGb(EmuState* state, DecodedOp* op) {
    // 38: CMP r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluSub(state, dest, src);
}

void OpCmp_GbEb(EmuState* state, DecodedOp* op) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t src = ReadModRM8(state, op);
    AluSub(state, dest, src);
}

void OpCmp_GvEv(EmuState* state, DecodedOp* op) {
    // 3B: CMP r32, r/m32
    if (op->prefixes.flags.opsize) {
        uint16_t dest = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
        uint16_t src = ReadModRM16(state, op);
        AluSub(state, dest, src);
    } else {
        uint32_t dest = GetReg(state, (op->modrm >> 3) & 7);
        uint32_t src = ReadModRM32(state, op);
        AluSub(state, dest, src);
    }
}

void OpTest_EvGv(EmuState* state, DecodedOp* op) {
    // 85: TEST r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    AluAnd(state, dest, src); // Discard result
}

void OpInc_Reg(EmuState* state, DecodedOp* op) {
    // 40+rd: INC r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    
    // INC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    uint32_t res = AluAdd(state, val, 1U);
    state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
    
    SetReg(state, reg, res);
}

void OpDec_Reg(EmuState* state, DecodedOp* op) {
    // 48+rd: DEC r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    
    // DEC does not affect CF
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    uint32_t res = AluSub(state, val, 1U);
    state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
    
    SetReg(state, reg, res);
}

void Helper_Group2(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count, bool is_byte) {
    uint8_t subop = (op->modrm >> 3) & 7;
    // printf("[Group2] Sub=%d Dest=%08X Count=%d Byte=%d\n", subop, dest, count, is_byte);
    uint32_t res = dest;
    
    // Mask count
    if (count == 0) return; // Nothing
    
    // Perform Op
    switch (subop) {
        case 0: // ROL
            if (is_byte) res = AluRol<uint8_t>(state, (uint8_t)dest, count);
            else         res = AluRol<uint32_t>(state, dest, count);
            break;
        case 1: // ROR
            if (is_byte) res = AluRor<uint8_t>(state, (uint8_t)dest, count);
            else         res = AluRor<uint32_t>(state, dest, count);
            break;
        case 2: // RCL
            OpNotImplemented(state, op);
            return;
        case 3: // RCR
            OpNotImplemented(state, op);
            return;
        case 4: // SHL/SAL
            if (is_byte) res = AluShl<uint8_t>(state, (uint8_t)dest, count);
            else         res = AluShl<uint32_t>(state, dest, count);
            break;
        case 5: // SHR
            if (is_byte) res = AluShr<uint8_t>(state, (uint8_t)dest, count);
            else         res = AluShr<uint32_t>(state, dest, count);
            break;
        case 7: // SAR
            if (is_byte) res = AluSar<uint8_t>(state, (uint8_t)dest, count);
            else         res = AluSar<uint32_t>(state, dest, count);
            break;
        default:
            OpNotImplemented(state, op);
            return;
    }
    
    // Write Back
    if (is_byte) {
         uint8_t mod = (op->modrm >> 6) & 3;
         uint8_t rm = op->modrm & 7;
         if (mod == 3) {
             uint32_t* rptr = GetRegPtr(state, rm & 3);
             uint32_t val = *rptr;
             if (rm < 4) {
                 val = (val & 0xFFFFFF00) | (res & 0xFF);
             } else {
                 val = (val & 0xFFFF00FF) | ((res & 0xFF) << 8);
             }
             *rptr = val;
             
         } else {
             uint32_t addr = ComputeEAD(state, op);
             state->mmu.write<uint8_t>(addr, (uint8_t)res);
         }
    } else {
        WriteModRM32(state, op, res);
    }
}

// Helper to read operand
uint32_t ReadModRM(EmuState* state, DecodedOp* op, bool is_byte) {
    if (is_byte) {
         // Read 8-bit
         uint8_t mod = (op->modrm >> 6) & 3;
         uint8_t rm = op->modrm & 7;
         if (mod == 3) {
             uint32_t val = GetReg(state, rm & 3);
             if (rm < 4) return val & 0xFF;
             else return (val >> 8) & 0xFF;
         } else {
             uint32_t addr = ComputeEAD(state, op);
             return state->mmu.read<uint8_t>(addr);
         }
    } else {
        return ReadModRM32(state, op);
    }
}

void OpGroup2_EvIb(EmuState* state, DecodedOp* op) {
    // C0: r/m8, imm8
    // C1: r/m32, imm8
    bool is_byte = (op->handler_index == 0xC0);
    uint32_t dest = ReadModRM(state, op, is_byte);
    uint8_t count = (uint8_t)op->imm;
    Helper_Group2(state, op, dest, count, is_byte);
}

void OpGroup2_Ev1(EmuState* state, DecodedOp* op) {
    // D0: r/m8, 1
    // D1: r/m32, 1
    bool is_byte = (op->handler_index == 0xD0);
    uint32_t dest = ReadModRM(state, op, is_byte);
    Helper_Group2(state, op, dest, 1, is_byte);
}

void OpGroup2_EvCl(EmuState* state, DecodedOp* op) {
    // D2: r/m8, CL
    // D3: r/m32, CL
    bool is_byte = (op->handler_index == 0xD2);
    uint32_t dest = ReadModRM(state, op, is_byte);
    uint8_t count = GetReg(state, ECX) & 0xFF;
    Helper_Group2(state, op, dest, count, is_byte);
}

void OpGroup5_Ev(EmuState* state, DecodedOp* op) {
    // FF: Group 5
    uint8_t subop = (op->modrm >> 3) & 7;
    uint32_t dest = 0;
    
    switch (subop) {
        case 0: // INC Ev
            dest = ReadModRM32(state, op);
            {
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint32_t res = AluAdd(state, dest, 1U);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM32(state, op, res);
            }
            break;
        case 1: // DEC Ev
            dest = ReadModRM32(state, op);
            {
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint32_t res = AluSub(state, dest, 1U);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM32(state, op, res);
            }
            break;
        case 2: // CALL Ev (Near Indirect)
             dest = ReadModRM32(state, op);
             Push32(state, state->ctx.eip);
             state->ctx.eip = dest;
             break;
        case 4: // JMP Ev (Near Indirect)
             dest = ReadModRM32(state, op);
             state->ctx.eip = dest;
             break;
        case 6: // PUSH Ev
             dest = ReadModRM32(state, op);
             Push32(state, dest);
             break;
        default:
             OpNotImplemented(state, op);
             break;
    }
}

// ------------------------------------------------------------------------------------------------
// SSE / SSE2
// ------------------------------------------------------------------------------------------------

// Helpers for Immediate Comparison
simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_pd(a, b);
        case 1: return simde_mm_cmplt_pd(a, b);
        case 2: return simde_mm_cmple_pd(a, b);
        case 3: return simde_mm_cmpunord_pd(a, b);
        case 4: return simde_mm_cmpneq_pd(a, b);
        case 5: return simde_mm_cmpnlt_pd(a, b);
        case 6: return simde_mm_cmpnle_pd(a, b);
        case 7: return simde_mm_cmpord_pd(a, b);
    }
    return a;
}

simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_sd(a, b);
        case 1: return simde_mm_cmplt_sd(a, b);
        case 2: return simde_mm_cmple_sd(a, b);
        case 3: return simde_mm_cmpunord_sd(a, b);
        case 4: return simde_mm_cmpneq_sd(a, b);
        case 5: return simde_mm_cmpnlt_sd(a, b);
        case 6: return simde_mm_cmpnle_sd(a, b);
        case 7: return simde_mm_cmpord_sd(a, b);
    }
    return a;
}

simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_ps(a, b);
        case 1: return simde_mm_cmplt_ps(a, b);
        case 2: return simde_mm_cmple_ps(a, b);
        case 3: return simde_mm_cmpunord_ps(a, b);
        case 4: return simde_mm_cmpneq_ps(a, b);
        case 5: return simde_mm_cmpnlt_ps(a, b);
        case 6: return simde_mm_cmpnle_ps(a, b);
        case 7: return simde_mm_cmpord_ps(a, b);
    }
    return a;
}

simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_ss(a, b);
        case 1: return simde_mm_cmplt_ss(a, b);
        case 2: return simde_mm_cmple_ss(a, b);
        case 3: return simde_mm_cmpunord_ss(a, b);
        case 4: return simde_mm_cmpneq_ss(a, b);
        case 5: return simde_mm_cmpnlt_ss(a, b);
        case 6: return simde_mm_cmpnle_ss(a, b);
        case 7: return simde_mm_cmpord_ss(a, b);
    }
    return a;
}

void OpCmp_Sse(EmuState* state, DecodedOp* op) {
    uint8_t pred = (uint8_t)op->imm;
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    
    if (op->prefixes.flags.opsize) {
        // 66 0F C2: CMPPD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        __m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);
        
        simde__m128d res = Helper_CmpPD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);
        
    } else if (op->prefixes.flags.repne) {
        // F2 0F C2: CMPSD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d src_pd;
        
        if ((op->modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
            src_pd = simde_mm_set_sd(*(double*)&val);
        }
        
        simde__m128d res = Helper_CmpSD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);
        
    } else if (op->prefixes.flags.rep) {
        // F3 0F C2: CMPSS
        __m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t val = state->mmu.read<uint32_t>(ComputeEAD(state, op));
            src = simde_mm_set_ss(*(float*)&val);
        }
        *dest_ptr = Helper_CmpSS(*dest_ptr, src, pred);
        
    } else {
        // 0F C2: CMPPS
        __m128 src = ReadModRM128(state, op);
        *dest_ptr = Helper_CmpPS(*dest_ptr, src, pred);
    }
}

void OpCvt_2A(EmuState* state, DecodedOp* op) {
    // 0F 2A: CVTPI2PS (MMX) / CVTSI2SS (F3) / CVTSI2SD (F2)
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    
    int32_t val = (int32_t)ReadModRM32(state, op);
    
    if (op->prefixes.flags.repne) {
        // F2: CVTSI2SD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtsi32_sd(dest_pd, val);
        *dest_ptr = simde_mm_castpd_ps(res);
    } else if (op->prefixes.flags.rep) {
        // F3: CVTSI2SS
        *dest_ptr = simde_mm_cvtsi32_ss(*dest_ptr, val);
    } else {
        OpNotImplemented(state, op);
    }
}

void OpCvt_5A(EmuState* state, DecodedOp* op) {
    // 0F 5A: CVTPS2PD / CVTPD2PS (66) / CVTSD2SS (F2) / CVTSS2SD (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    
    if (op->prefixes.flags.opsize) {
        // 66: CVTPD2PS (xmm/m128 -> xmm)
        __m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);
        *dest_ptr = simde_mm_cvtpd_ps(src_pd);
        
    } else if (op->prefixes.flags.repne) {
        // F2: CVTSD2SS (xmm/m64 -> xmm)
        simde__m128d src_pd;
        if ((op->modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
            src_pd = simde_mm_set_sd(*(double*)&val);
        }
        *dest_ptr = simde_mm_cvtsd_ss(*dest_ptr, src_pd);
        
    } else if (op->prefixes.flags.rep) {
        // F3: CVTSS2SD (xmm/m32 -> xmm)
        __m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t val = state->mmu.read<uint32_t>(ComputeEAD(state, op));
            src = simde_mm_set_ss(*(float*)&val);
        }
        
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtss_sd(dest_pd, src);
        *dest_ptr = simde_mm_castpd_ps(res);
        
    } else {
        // 0F: CVTPS2PD (xmm/m64 -> xmm)
        __m128 src;
         if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
            uint32_t v0 = val & 0xFFFFFFFF;
            uint32_t v1 = val >> 32;
            src = simde_mm_set_ps(0.0f, 0.0f, *(float*)&v1, *(float*)&v0); 
        }
        simde__m128d res = simde_mm_cvtps_pd(src);
        *dest_ptr = simde_mm_castpd_ps(res);
    }
}

void OpCvt_5B(EmuState* state, DecodedOp* op) {
    // 0F 5B: CVTDQ2PS / CVTPS2DQ (66) / CVTTPS2DQ (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    __m128 src = ReadModRM128(state, op);
    
    if (op->prefixes.flags.opsize) {
        // 66: CVTPS2DQ
        simde__m128i res = simde_mm_cvtps_epi32(src);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else if (op->prefixes.flags.rep) {
        // F3: CVTTPS2DQ (Truncate)
        simde__m128i res = simde_mm_cvttps_epi32(src);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else {
        // 0F: CVTDQ2PS
        simde__m128i isrc = simde_mm_castps_si128(src);
        *dest_ptr = simde_mm_cvtepi32_ps(isrc);
    }
}

void OpCvt_E6(EmuState* state, DecodedOp* op) {
    // 0F E6: CVTPD2DQ (F2) / CVTDQ2PD (F3) / CVTTPD2DQ (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    
    if (op->prefixes.flags.opsize) {
        // 66: CVTTPD2DQ (Truncate xmm/m128 -> xmm)
        __m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);
        
        simde__m128i res = simde_mm_cvttpd_epi32(src_pd);
        *dest_ptr = simde_mm_castsi128_ps(res);
        
    } else if (op->prefixes.flags.rep) {
        // F3: CVTDQ2PD (xmm/m64 -> xmm)
        simde__m128i isrc;
        if ((op->modrm >> 6) == 3) {
            // Register: cast float to int
            __m128 src = state->ctx.xmm[op->modrm & 7];
            isrc = simde_mm_castps_si128(src);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
             uint32_t v0 = val & 0xFFFFFFFF;
             uint32_t v1 = val >> 32;
             isrc = simde_mm_set_epi32(0, 0, v1, v0);
        }
        
        simde__m128d res = simde_mm_cvtepi32_pd(isrc);
        *dest_ptr = simde_mm_castpd_ps(res);
        
    } else if (op->prefixes.flags.repne) {
        // F2: CVTPD2DQ (xmm/m128 -> xmm)
        __m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);
        
        simde__m128i res = simde_mm_cvtpd_epi32(src_pd);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else {
        OpNotImplemented(state, op);
    }
}

// ------------------------------------------------------------------------------------------------
// ADC
// ------------------------------------------------------------------------------------------------

void OpAdc_EbGb(EmuState* state, DecodedOp* op) {
    // 10: ADC r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdc(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAdc_EvGv(EmuState* state, DecodedOp* op) {
    // 11: ADC r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
    uint32_t res = AluAdc(state, dest, src);
    WriteModRM32(state, op, res);
}

void OpAdc_GbEb(EmuState* state, DecodedOp* op) {
    // 12: ADC r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdc(state, dest, src);
    
    // Write back to reg8
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAdc_GvEv(EmuState* state, DecodedOp* op) {
    // 13: ADC r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAdc(state, dest, src);
    SetReg(state, reg, res);
}

// ------------------------------------------------------------------------------------------------
// ADD Byte
// ------------------------------------------------------------------------------------------------

void OpAdd_EbGb(EmuState* state, DecodedOp* op) {
    // 00: ADD r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAdd(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAdd_GbEb(EmuState* state, DecodedOp* op) {
    // 02: ADD r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAdd(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAdd_GvEv(EmuState* state, DecodedOp* op) {
    // 03: ADD r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAdd(state, dest, src);
    SetReg(state, reg, res);
}

// ------------------------------------------------------------------------------------------------
// AND Byte
// ------------------------------------------------------------------------------------------------

void OpAnd_EbGb(EmuState* state, DecodedOp* op) {
    // 20: AND r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t res = AluAnd(state, dest, src);
    WriteModRM8(state, op, res);
}

void OpAnd_GbEb(EmuState* state, DecodedOp* op) {
    // 22: AND r8, r/m8
    uint8_t src = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t dest = GetReg8(state, reg);
    uint8_t res = AluAnd(state, dest, src);
    
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    if (reg < 4) *rptr = (*rptr & 0xFFFFFF00) | res;
    else *rptr = (*rptr & 0xFFFF00FF) | (res << 8);
}

void OpAnd_GvEv(EmuState* state, DecodedOp* op) {
    // 23: AND r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t dest = GetReg(state, reg);
    uint32_t res = AluAnd(state, dest, src);
    SetReg(state, reg, res);
}

// ------------------------------------------------------------------------------------------------
// Group 1 Byte
// ------------------------------------------------------------------------------------------------

template<typename T>
void Helper_Group1(EmuState* state, DecodedOp* op, T dest, T src) {
    uint8_t subop = (op->modrm >> 3) & 7;
    T res = 0;
    
    switch (subop) {
        case 0: res = AluAdd(state, dest, src); break;
        case 1: res = AluOr(state, dest, src);  break;
        case 2: res = AluAdc(state, dest, src); break;
        case 3: res = AluSbb(state, dest, src); break;
        case 4: res = AluAnd(state, dest, src); break;
        case 5: res = AluSub(state, dest, src); break;
        case 6: res = AluXor(state, dest, src); break;
        case 7: AluSub(state, dest, src); return; // CMP (No writeback)
        default: OpNotImplemented(state, op); return;
    }
    
    if constexpr (sizeof(T) == 1) {
        WriteModRM8(state, op, (uint8_t)res);
    } else if constexpr (sizeof(T) == 2) {
        WriteModRM16(state, op, (uint16_t)res);
    } else {
        WriteModRM32(state, op, (uint32_t)res);
    }
}

void OpGroup1_EbIb(EmuState* state, DecodedOp* op) {
    // 80: Arith r/m8, imm8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = (uint8_t)op->imm;
    Helper_Group1<uint8_t>(state, op, dest, src);
}

void OpGroup1_EvIz(EmuState* state, DecodedOp* op) {
    // 81: Arith r/m32, imm32
    // 83: Arith r/m32, imm8 (sign-extended)
    
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = (uint16_t)op->imm;
        if ((op->handler_index & 0xFF) == 0x83) {
            src = (int16_t)(int8_t)src;
        }
        Helper_Group1<uint16_t>(state, op, dest, src);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = op->imm;
        if ((op->handler_index & 0xFF) == 0x83) {
            src = (int32_t)(int8_t)src;
        }
        Helper_Group1<uint32_t>(state, op, dest, src);
    }
}

// ------------------------------------------------------------------------------------------------
// Bit Instructions
// ------------------------------------------------------------------------------------------------

void OpBt_EvGv(EmuState* state, DecodedOp* op) {
    // 0F A3: BT r/m32, r32
    uint32_t offset = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    uint8_t bit_val = 0;
    if (mod == 3) {
        uint32_t base = GetReg(state, rm);
        offset &= 31;
        bit_val = (base >> offset) & 1;
    } else {
        uint32_t addr = ComputeEAD(state, op);
        int32_t signed_offset = (int32_t)offset;
        addr += (signed_offset >> 3);
        uint8_t bit_idx = signed_offset & 7;
        bit_val = (state->mmu.read<uint8_t>(addr) >> bit_idx) & 1;
    }
    
    if (bit_val) state->ctx.eflags |= CF_MASK;
    else state->ctx.eflags &= ~CF_MASK;
}

void OpBtr_EvGv(EmuState* state, DecodedOp* op) {
    // 0F B3: BTR r/m32, r32
    uint32_t offset = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;
    
    uint8_t bit_val = 0;
    if (mod == 3) {
        uint32_t base = GetReg(state, rm);
        uint32_t mask = 1 << (offset & 31);
        bit_val = (base & mask) ? 1 : 0;
        SetReg(state, rm, base & ~mask);
    } else {
        uint32_t addr = ComputeEAD(state, op);
        int32_t signed_offset = (int32_t)offset;
        addr += (signed_offset >> 3);
        uint8_t bit_idx = signed_offset & 7;
        
        uint8_t byte = state->mmu.read<uint8_t>(addr);
        bit_val = (byte >> bit_idx) & 1;
        state->mmu.write<uint8_t>(addr, byte & ~(1 << bit_idx));
    }
    
    if (bit_val) state->ctx.eflags |= CF_MASK;
    else state->ctx.eflags &= ~CF_MASK;
}

void OpBt_EvIb(EmuState* state, DecodedOp* op) {
    // 0F BA /4: BT r/m32, imm8
    uint8_t offset = op->imm & 31; // imm8 modulo 32
    // For imm8, it treats operand as 32-bit (or 16-bit), bit index is modulo width.
    // It does NOT do the memory offset thing.
    
    uint32_t base = ReadModRM32(state, op);
    uint8_t bit_val = (base >> offset) & 1;
    
    if (bit_val) state->ctx.eflags |= CF_MASK;
    else state->ctx.eflags &= ~CF_MASK;
}

void OpBsr_GvEv(EmuState* state, DecodedOp* op) {
    // 0F BD: BSR r32, r/m32
    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    
    if (src == 0) {
        state->ctx.eflags |= ZF_MASK;
        // Dest undefined. Keep it?
    } else {
        state->ctx.eflags &= ~ZF_MASK;
        // Find MSB
        // __builtin_clz(src) returns leading zeros.
        // 31 - clz = index.
        int idx = 31 - __builtin_clz(src);
        SetReg(state, reg, idx);
    }
}

void OpBswap_Reg(EmuState* state, DecodedOp* op) {
    // 0F C8+rd: BSWAP r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    uint32_t res = __builtin_bswap32(val);
    SetReg(state, reg, res);
}

// ------------------------------------------------------------------------------------------------
// Misc
// ------------------------------------------------------------------------------------------------

void OpCdq(EmuState* state, DecodedOp* op) {
    // 99: CDQ
    uint32_t eax = GetReg(state, EAX);
    uint32_t edx = ((int32_t)eax < 0) ? 0xFFFFFFFF : 0;
    SetReg(state, EDX, edx);
}

// ------------------------------------------------------------------------------------------------
// Stack & LEA
// ------------------------------------------------------------------------------------------------

void OpLea(EmuState* state, DecodedOp* op) {
    // LEA r32, m (0x8D)
    uint32_t addr = ComputeEAD(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, addr);
}

void OpPush_Reg(EmuState* state, DecodedOp* op) {
    // PUSH r32 (0x50+rd)
    uint8_t reg = op->handler_index & 7; // Extract reg from opcode
    uint32_t val = GetReg(state, reg);
    Push32(state, val);
}

void OpPush_Imm(EmuState* state, DecodedOp* op) {
    // PUSH imm32 (0x68) or PUSH imm8 (0x6A)
    // Decoder already extracted imm to op->imm
    uint32_t val = op->imm;
    if ((op->handler_index & 0xFF) == 0x6A) {
        val = (int32_t)(int8_t)val;
    }
    Push32(state, val);
}

void OpPop_Reg(EmuState* state, DecodedOp* op) {
    // POP r32 (0x58+rd)
    uint8_t reg = op->handler_index & 7;
    uint32_t val = Pop32(state);
    SetReg(state, reg, val);
}

// ------------------------------------------------------------------------------------------------
// Control & System
// ------------------------------------------------------------------------------------------------

bool CheckCondition(EmuState* state, uint8_t cond) {
    uint32_t flags = state->ctx.eflags;
    bool cf = (flags & CF_MASK);
    bool zf = (flags & ZF_MASK);
    bool sf = (flags & SF_MASK);
    bool of = (flags & OF_MASK);
    bool pf = (flags & PF_MASK);
    
    switch (cond) {
        case 0: return of;          // JO
        case 1: return !of;         // JNO
        case 2: return cf;          // JB/JNAE
        case 3: return !cf;         // JNB/JAE
        case 4: return zf;          // JE/JZ
        case 5: return !zf;         // JNE/JNZ
        case 6: return cf || zf;    // JBE/JNA
        case 7: return !cf && !zf;  // JNBE/JA
        case 8: return sf;          // JS
        case 9: return !sf;         // JNS
        case 10: return pf;         // JP/JPE
        case 11: return !pf;        // JNP/JPO
        case 12: return sf != of;   // JL/JNGE
        case 13: return sf == of;   // JNL/JGE
        case 14: return zf || (sf != of); // JLE/JNG
        case 15: return !zf && (sf == of); // JNLE/JG
        default: return false;
    }
}

void OpCmov_GvEv(EmuState* state, DecodedOp* op) {
    // 0F 4x: CMOVcc r32, r/m32
    uint8_t cond = op->handler_index & 0xF;
    bool pass = CheckCondition(state, cond);
    printf("CMOV cond=%d pass=%d eflags=%x\n", cond, pass, state->ctx.eflags);
    if (pass) {
        if (op->prefixes.flags.opsize) {
            uint16_t val = ReadModRM16(state, op);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, (GetReg(state, reg) & 0xFFFF0000) | val);
        } else {
            uint32_t val = ReadModRM32(state, op);
            uint8_t reg = (op->modrm >> 3) & 7;
            SetReg(state, reg, val);
        }
    }
}

void OpHlt(EmuState* state, DecodedOp* op) {
    // HLT (0xF4)
    state->status = EmuStatus::Stopped;
}

void OpJcc_Rel(EmuState* state, DecodedOp* op) {
    // 0F 8x: Jcc rel32
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    uint8_t cond = op->handler_index & 0xF;
    
    if (CheckCondition(state, cond)) {
        state->ctx.eip += op->imm;
    }
    // If not taken, EIP is already at next insn (fallthrough).
}

void OpCall_Rel(EmuState* state, DecodedOp* op) {
    // E8: CALL rel32
    // Push Return Address (Current EIP, which is Next Insn due to DispatchWrapper)
    Push32(state, state->ctx.eip);
    // Jump
    state->ctx.eip += op->imm;
}

void OpRet(EmuState* state, DecodedOp* op) {
    // C3: RET
    uint32_t ret_eip = Pop32(state);
    state->ctx.eip = ret_eip;
}

// ------------------------------------------------------------------------------------------------
// Dispatch Wrapper
// ------------------------------------------------------------------------------------------------

template<LogicFunc Target>
ATTR_PRESERVE_NONE
void DispatchWrapper(EmuState* state, DecodedOp* op) {
    state->ctx.eip += op->length;
    Target(state, op);
    
    // Tail Call Dispatch
    if (!op->meta.flags.is_last && state->status == EmuStatus::Running) {
        DecodedOp* next = op + 1;
        ATTR_MUSTTAIL return next->handler(state, next);
    }
}

// ------------------------------------------------------------------------------------------------
// Initialization
// ------------------------------------------------------------------------------------------------

// Initialize Loop
HandlerFunc g_Handlers[1024] = {0};

struct HandlerInit {
    HandlerInit() {
        // 0. Default all to Not Implemented
        for(int i=0; i<1024; ++i) {
            g_Handlers[i] = DispatchWrapper<OpNotImplemented>;
        }
        
        // 1. Set NOP
        g_Handlers[0x90] = DispatchWrapper<OpNop>;
        
        // 2. Set MOV
        g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
        g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;
        for(int i=0; i<8; ++i) {
            g_Handlers[0xB0+i] = DispatchWrapper<OpMov_RegImm8>;
            g_Handlers[0xB8+i] = DispatchWrapper<OpMov_RegImm>;
        }
        g_Handlers[0xC7] = DispatchWrapper<OpMov_EvIz>; // MOV r/m32, imm32
        
        // MOV moffs (A0-A3)
        g_Handlers[0xA0] = DispatchWrapper<OpMov_Moffs_Load>;
        g_Handlers[0xA1] = DispatchWrapper<OpMov_Moffs_Load>;
        g_Handlers[0xA2] = DispatchWrapper<OpMov_Moffs_Store>;
        g_Handlers[0xA3] = DispatchWrapper<OpMov_Moffs_Store>;
        
        // 3. Set LEA
        g_Handlers[0x8D] = DispatchWrapper<OpLea>;
        
        // 4. Set PUSH
        for (int i=0; i<8; ++i) g_Handlers[0x50+i] = DispatchWrapper<OpPush_Reg>;
        g_Handlers[0x68] = DispatchWrapper<OpPush_Imm>;
        g_Handlers[0x6A] = DispatchWrapper<OpPush_Imm>;
        
        // 5. Set POP
        for (int i=0; i<8; ++i) g_Handlers[0x58+i] = DispatchWrapper<OpPop_Reg>;
        
        // 6. Set HLT
        g_Handlers[0xF4] = DispatchWrapper<OpHlt>;
        
        // Control Flow
        g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel>; // JMP rel32
        g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel>; // JMP rel8
        g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>; // CALL rel32
        g_Handlers[0xC3] = DispatchWrapper<OpRet>;      // RET
        
        for (int i=0; i<16; ++i) {
            g_Handlers[0x70+i] = DispatchWrapper<OpJcc_Rel>; // Jcc rel8
            g_Handlers[0x180+i] = DispatchWrapper<OpJcc_Rel>; // Jcc rel32 (0F 8x)
        }

        // 7. Arithmetic & Logic
        g_Handlers[0x00] = DispatchWrapper<OpAdd_EbGb>;
        g_Handlers[0x01] = DispatchWrapper<OpAdd_EvGv>;
        g_Handlers[0x02] = DispatchWrapper<OpAdd_GbEb>;
        g_Handlers[0x03] = DispatchWrapper<OpAdd_GvEv>;
        
        g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv>;
        
        g_Handlers[0x10] = DispatchWrapper<OpAdc_EbGb>;
        g_Handlers[0x11] = DispatchWrapper<OpAdc_EvGv>;
        g_Handlers[0x12] = DispatchWrapper<OpAdc_GbEb>;
        g_Handlers[0x13] = DispatchWrapper<OpAdc_GvEv>;
        
        g_Handlers[0x20] = DispatchWrapper<OpAnd_EbGb>;
        g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv>;
        g_Handlers[0x22] = DispatchWrapper<OpAnd_GbEb>;
        g_Handlers[0x23] = DispatchWrapper<OpAnd_GvEv>;

        g_Handlers[0x29] = DispatchWrapper<OpSub_EvGv>;
        g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv>;
        g_Handlers[0x39] = DispatchWrapper<OpCmp_EvGv>;
        g_Handlers[0x85] = DispatchWrapper<OpTest_EvGv>;
        
        g_Handlers[0x80] = DispatchWrapper<OpGroup1_EbIb>;
        g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz>;
        g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIz>;
        
        g_Handlers[0x99] = DispatchWrapper<OpCdq>;
        
        // Group 2 (Shift/Rotate)
        g_Handlers[0xC0] = DispatchWrapper<OpGroup2_EvIb>;
        g_Handlers[0xC1] = DispatchWrapper<OpGroup2_EvIb>;
        g_Handlers[0xD0] = DispatchWrapper<OpGroup2_Ev1>;
        g_Handlers[0xD1] = DispatchWrapper<OpGroup2_Ev1>;
        g_Handlers[0xD2] = DispatchWrapper<OpGroup2_EvCl>;
        g_Handlers[0xD3] = DispatchWrapper<OpGroup2_EvCl>;
        
        // Group 5
        g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev>;
        
        // Inc/Dec
        for (int i=0; i<8; ++i) {
            g_Handlers[0x40+i] = DispatchWrapper<OpInc_Reg>;
            g_Handlers[0x48+i] = DispatchWrapper<OpDec_Reg>;
        }
        
        // Map 1 (0F xx) -> Index 0x100 + xx
        g_Handlers[0x1A3] = DispatchWrapper<OpBt_EvGv>;
        g_Handlers[0x1B3] = DispatchWrapper<OpBtr_EvGv>;
        g_Handlers[0x1B6] = DispatchWrapper<OpMovzx_Byte>;
        g_Handlers[0x1B7] = DispatchWrapper<OpMovzx_Word>;
        g_Handlers[0x1BA] = DispatchWrapper<OpBt_EvIb>;
        g_Handlers[0x1BD] = DispatchWrapper<OpBsr_GvEv>;
        g_Handlers[0x1BE] = DispatchWrapper<OpMovsx_Byte>;
        g_Handlers[0x1BF] = DispatchWrapper<OpMovsx_Word>;
        
        for (int i=0; i<8; ++i) {
            g_Handlers[0x1C8+i] = DispatchWrapper<OpBswap_Reg>;
        }

        // New Registrations
        // CMP
        g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;
        g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;
        g_Handlers[0x3B] = DispatchWrapper<OpCmp_GvEv>;

        // CMOVcc (0F 4x)
        for (int i=0; i<16; ++i) {
            g_Handlers[0x140+i] = DispatchWrapper<OpCmov_GvEv>;
        }

        // SSE / SSE2
        g_Handlers[0x1C2] = DispatchWrapper<OpCmp_Sse>;
        g_Handlers[0x12A] = DispatchWrapper<OpCvt_2A>;
        g_Handlers[0x15A] = DispatchWrapper<OpCvt_5A>;
        g_Handlers[0x15B] = DispatchWrapper<OpCvt_5B>;
        g_Handlers[0x1E6] = DispatchWrapper<OpCvt_E6>;
    }
};

static HandlerInit _init;

} // namespace x86emu