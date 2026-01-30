#include "ops.h"
#include "state.h"
#include "exec_utils.h"
#include <cstdio>
#include <iostream>
#include <cmath>
#include <simde/x86/sse.h>
#include "dispatch.h"

namespace x86emu {

uint8_t GetReg8(EmuState* state, uint8_t reg_idx) {
    uint32_t val = GetReg(state, reg_idx & 3);
    if (reg_idx < 4) return val & 0xFF;
    else return (val >> 8) & 0xFF;
}

void OpUd2(EmuState* state, DecodedOp* op) {
    // #UD is a Fault, so EIP should point to the faulting instruction.
    // DispatchWrapper already advanced EIP, so we must restore it.
    state->ctx.eip -= op->length;
    
    if (!state->hooks.on_invalid_opcode(state)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 6; // #UD
    }
}

void OpNop(EmuState* state, DecodedOp* op) {
    // No Operation
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
            OpUd2(state, op);
            return;
        case 3: // RCR
            OpUd2(state, op);
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
            OpUd2(state, op);
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
             OpUd2(state, op);
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
        OpUd2(state, op);
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
        OpUd2(state, op);
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
        default: OpUd2(state, op); return;
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
// FPU Helpers
// ------------------------------------------------------------------------------------------------

void FpuPush(EmuState* state, const float80* val) {
    state->ctx.fpu_top = (state->ctx.fpu_top - 1) & 7;

    // Use memcpy
    std::memcpy(&state->ctx.fpu_regs[state->ctx.fpu_top], val, sizeof(float80));
    
    state->ctx.fpu_tw &= ~(3 << (state->ctx.fpu_top * 2)); // Mark valid (00)
}

float80 FpuPop(EmuState* state) {
    float80 val;
    std::memcpy(&val, &state->ctx.fpu_regs[state->ctx.fpu_top], sizeof(float80));
    state->ctx.fpu_tw |= (3 << (state->ctx.fpu_top * 2)); // Mark empty
    state->ctx.fpu_top = (state->ctx.fpu_top + 1) & 7;
    return val;
}

float80& FpuTop(EmuState* state, int index) {
    return state->ctx.fpu_regs[(state->ctx.fpu_top + index) & 7];
}

// ------------------------------------------------------------------------------------------------
// Integer Instructions (Group 3, 4, IMUL)
// ------------------------------------------------------------------------------------------------

void OpImul_GvEv(EmuState* state, DecodedOp* op) {
    // 0F AF: IMUL r32, r/m32
    int32_t val1 = (int32_t)GetReg(state, (op->modrm >> 3) & 7);
    int32_t val2 = (int32_t)ReadModRM32(state, op);
    
    int64_t res = (int64_t)val1 * (int64_t)val2;
    uint32_t res32 = (uint32_t)res;
    SetReg(state, (op->modrm >> 3) & 7, res32);
    
    // Set OF/CF if result truncated
    if (res != (int64_t)(int32_t)res) {
        state->ctx.eflags |= (OF_MASK | CF_MASK);
    } else {
        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
}

void OpImul_GvEvIz(EmuState* state, DecodedOp* op) {
    // 69: IMUL r32, r/m32, imm32
    // 6B: IMUL r32, r/m32, imm8
    int32_t val1 = (int32_t)ReadModRM32(state, op);
    int32_t val2 = (int32_t)op->imm;
    
    if ((op->handler_index & 0xFF) == 0x6B) {
        val2 = (int32_t)(int8_t)val2;
    }
    
    int64_t res = (int64_t)val1 * (int64_t)val2;
    uint32_t res32 = (uint32_t)res;
    SetReg(state, (op->modrm >> 3) & 7, res32);
    
    if (res != (int64_t)(int32_t)res) {
        state->ctx.eflags |= (OF_MASK | CF_MASK);
    } else {
        state->ctx.eflags &= ~(OF_MASK | CF_MASK);
    }
}

template<typename T>
void Helper_Group3(EmuState* state, DecodedOp* op, T val) {
    uint8_t subop = (op->modrm >> 3) & 7;
    
    switch (subop) {
        case 0: // TEST imm
        case 1: // TEST imm
        {
            AluAnd(state, val, (T)op->imm);
            break;
        }
        case 2: // NOT
            if constexpr (sizeof(T) == 1) WriteModRM8(state, op, ~val);
            else if constexpr (sizeof(T) == 2) WriteModRM16(state, op, ~val);
            else WriteModRM32(state, op, ~val);
            break;
        case 3: // NEG
        {
            T res = AluSub(state, (T)0, val);
            if (val != 0) state->ctx.eflags |= CF_MASK;
            else state->ctx.eflags &= ~CF_MASK;
            
            if constexpr (sizeof(T) == 1) WriteModRM8(state, op, res);
            else if constexpr (sizeof(T) == 2) WriteModRM16(state, op, res);
            else WriteModRM32(state, op, res);
            break;
        }
        case 4: // MUL (Unsigned)
        {
            if constexpr (sizeof(T) == 1) { // Byte: AX = AL * r/m8
                uint8_t al = GetReg8(state, EAX);
                uint16_t res = (uint16_t)al * (uint16_t)val;
                
                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | res;
                
                if ((res & 0xFF00) != 0) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            } else if constexpr (sizeof(T) == 2) { // Word: DX:AX = AX * r/m16
                uint16_t ax = (uint16_t)(GetReg(state, EAX) & 0xFFFF);
                uint32_t res = (uint32_t)ax * (uint32_t)val;
                
                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (res & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | ((res >> 16) & 0xFFFF);
                
                if ((res >> 16) != 0) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            } else { // Dword: EDX:EAX = EAX * r/m32
                uint32_t eax = GetReg(state, EAX);
                uint64_t res = (uint64_t)eax * (uint64_t)val;
                SetReg(state, EAX, (uint32_t)res);
                SetReg(state, EDX, (uint32_t)(res >> 32));
                
                if ((res >> 32) != 0) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            }
            break;
        }
        case 5: // IMUL (Signed)
        {
            if constexpr (sizeof(T) == 1) { // AX = AL * r/m8
                int8_t al = (int8_t)GetReg8(state, EAX);
                int16_t res = (int16_t)al * (int16_t)(int8_t)val;
                
                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | (uint16_t)res;
                
                if (res != (int16_t)(int8_t)res) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            } else if constexpr (sizeof(T) == 2) { // Word: DX:AX = AX * r/m16
                int16_t ax = (int16_t)(GetReg(state, EAX) & 0xFFFF);
                int32_t res = (int32_t)ax * (int32_t)(int16_t)val;
                
                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (res & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | ((res >> 16) & 0xFFFF);
                
                if (res != (int32_t)(int16_t)res) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            } else { // Dword: EDX:EAX = EAX * r/m32
                int32_t eax = (int32_t)GetReg(state, EAX);
                int64_t res = (int64_t)eax * (int64_t)(int32_t)val;
                SetReg(state, EAX, (uint32_t)res);
                SetReg(state, EDX, (uint32_t)(res >> 32));
                
                if (res != (int64_t)(int32_t)res) state->ctx.eflags |= (OF_MASK | CF_MASK);
                else state->ctx.eflags &= ~(OF_MASK | CF_MASK);
            }
            break;
        }
        case 6: // DIV (Unsigned)
        {
            if constexpr (sizeof(T) == 1) { // AX / r/m8
                 uint16_t ax = (uint16_t)GetReg(state, EAX) & 0xFFFF;
                 if (val == 0) { state->status = EmuStatus::Fault; return; }
                 uint16_t q = ax / val;
                 uint16_t r = ax % val;
                 
                 uint32_t* rax = GetRegPtr(state, EAX);
                 *rax = (*rax & 0xFFFF0000) | (r << 8) | (q & 0xFF);
            } else if constexpr (sizeof(T) == 2) { // DX:AX / r/m16
                 uint32_t dx_ax = ((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF);
                 if (val == 0) { state->status = EmuStatus::Fault; return; }
                 uint32_t q = dx_ax / val;
                 uint32_t r = dx_ax % val;
                 
                 if (q > 0xFFFF) { /* Overflow */ }
                 
                 uint32_t* rax = GetRegPtr(state, EAX);
                 uint32_t* rdx = GetRegPtr(state, EDX);
                 *rax = (*rax & 0xFFFF0000) | (q & 0xFFFF);
                 *rdx = (*rdx & 0xFFFF0000) | (r & 0xFFFF);
            } else { // EDX:EAX / r/m32
                 uint64_t edx_eax = ((uint64_t)GetReg(state, EDX) << 32) | GetReg(state, EAX);
                 if (val == 0) { state->status = EmuStatus::Fault; return; }
                 uint64_t q = edx_eax / val;
                 uint64_t r = edx_eax % val;
                 SetReg(state, EAX, (uint32_t)q);
                 SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        case 7: // IDIV (Signed)
        {
            if constexpr (sizeof(T) == 1) { // AX / r/m8
                int16_t ax = (int16_t)(GetReg(state, EAX) & 0xFFFF);
                int8_t v = (int8_t)val;
                if (v == 0) { state->status = EmuStatus::Fault; return; }
                int16_t q = ax / v;
                int16_t r = ax % v;
                
                uint32_t* rax = GetRegPtr(state, EAX);
                *rax = (*rax & 0xFFFF0000) | ((uint8_t)r << 8) | ((uint8_t)q);
            } else if constexpr (sizeof(T) == 2) { // DX:AX / r/m16
                int32_t dx_ax = (int32_t)(((uint32_t)(GetReg(state, EDX) & 0xFFFF) << 16) | (GetReg(state, EAX) & 0xFFFF));
                int16_t v = (int16_t)val;
                if (v == 0) { state->status = EmuStatus::Fault; return; }
                int32_t q = dx_ax / v;
                int32_t r = dx_ax % v;
                
                if (q > 32767 || q < -32768) { /* Overflow */ }
                
                uint32_t* rax = GetRegPtr(state, EAX);
                uint32_t* rdx = GetRegPtr(state, EDX);
                *rax = (*rax & 0xFFFF0000) | (q & 0xFFFF);
                *rdx = (*rdx & 0xFFFF0000) | (r & 0xFFFF);
            } else { // EDX:EAX / r/m32
                int64_t edx_eax = ((int64_t)GetReg(state, EDX) << 32) | GetReg(state, EAX);
                int32_t v = (int32_t)val;
                if (v == 0) { state->status = EmuStatus::Fault; return; }
                int64_t q = edx_eax / v;
                int64_t r = edx_eax % v;
                
                SetReg(state, EAX, (uint32_t)q);
                SetReg(state, EDX, (uint32_t)r);
            }
            break;
        }
        default: OpUd2(state, op);
    }
}

void OpGroup3_Ev(EmuState* state, DecodedOp* op) {
    // F6 (Byte) or F7 (Dword)
    bool is_byte = (op->handler_index == 0xF6);
    if (is_byte) {
        uint8_t val = ReadModRM8(state, op);
        Helper_Group3<uint8_t>(state, op, val);
    } else {
        if (op->prefixes.flags.opsize) {
            uint16_t val = ReadModRM16(state, op);
            Helper_Group3<uint16_t>(state, op, val);
        } else {
            uint32_t val = ReadModRM32(state, op);
            Helper_Group3<uint32_t>(state, op, val);
        }
    }
}

void OpGroup4_Eb(EmuState* state, DecodedOp* op) {
    // FE: Group 4 (Byte)
    uint8_t subop = (op->modrm >> 3) & 7;
    uint8_t val = ReadModRM8(state, op);
    uint32_t old_cf = state->ctx.eflags & CF_MASK;
    
    switch (subop) {
        case 0: // INC
        {
            uint8_t res = AluAdd(state, val, (uint8_t)1);
            state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res);
            break;
        }
        case 1: // DEC
        {
            uint8_t res = AluSub(state, val, (uint8_t)1);
            state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res);
            break;
        }
        default: OpUd2(state, op);
    }

}

// ------------------------------------------------------------------------------------------------
// FPU Instructions
// ------------------------------------------------------------------------------------------------

// Helper to read float32 from memory and convert to float80
float80 ReadF32(EmuState* state, DecodedOp* op) {
    uint32_t val = state->mmu.read<uint32_t>(ComputeEAD(state, op));
    return f80_from_double((double)*(float*)&val);
}

// Helper to read float64 from memory and convert to float80
float80 ReadF64(EmuState* state, DecodedOp* op) {
    uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
    return f80_from_double(*(double*)&val);
}

void OpFpu_D8(EmuState* state, DecodedOp* op) {
    // D8: FPU Arith m32
    uint8_t subop = (op->modrm >> 3) & 7;
    float80 val = ReadF32(state, op);
    float80& st0 = FpuTop(state, 0);
    
    switch (subop) {
        case 0: st0 = f80_add(st0, val); break; // FADD
        case 1: st0 = f80_mul(st0, val); break; // FMUL
        case 2: // FCOM
            // Update FPU Status Word (C0, C2, C3)
            // Unordered/LT/EQ?
            // Simplified:
            if (f80_lt(st0, val)) state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100; // C0=1
            else if (f80_eq(st0, val)) state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000; // C3=1
            else state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500); // Greater
            break;
        case 3: // FCOMP (Compare and Pop)
            if (f80_lt(st0, val)) state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x0100;
            else if (f80_eq(st0, val)) state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500) | 0x4000;
            else state->ctx.fpu_sw = (state->ctx.fpu_sw & ~0x4500);
            FpuPop(state);
            break;
        case 4: st0 = f80_sub(st0, val); break; // FSUB
        case 5: st0 = f80_sub(val, st0); break; // FSUBR
        case 6: st0 = f80_div(st0, val); break; // FDIV
        case 7: st0 = f80_div(val, st0); break; // FDIVR
        default: OpUd2(state, op);
    }
}

void OpFpu_D9(EmuState* state, DecodedOp* op) {
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
        // D9 C0-FF: FPU Instructions with Regs
        // Map 0xD9C0 -> index
        uint8_t index = op->modrm & 0x3F; // C0-FF -> 00-3F? No.
        uint8_t op_byte = op->modrm;
        
        if (op_byte == 0xC0) { // FLD ST(0) (DUP) -> D9 C0
            float80 t = FpuTop(state, 0);
            FpuPush(state, &t);
        } else if (op_byte == 0xC9) { // FXCH ST(1)
             float80 t = FpuTop(state, 0);
             FpuTop(state, 0) = FpuTop(state, 1);
             FpuTop(state, 1) = t;
        } else if ((op_byte & 0xF8) == 0xC8) { // FXCH ST(i)
             int idx = op_byte & 7;
             float80 t = FpuTop(state, 0);
             FpuTop(state, 0) = FpuTop(state, idx);
             FpuTop(state, idx) = t;
        } else if (op_byte == 0xD0) { // FNOP
        } else if (op_byte == 0xE0) { // FCHS
             FpuTop(state, 0) = f80_neg(FpuTop(state, 0));
        } else if (op_byte == 0xE1) { // FABS
             FpuTop(state, 0) = f80_abs(FpuTop(state, 0));
        } else if (op_byte == 0xE8) { // FLD1
             float80 t = ConstF80_One(); FpuPush(state, &t);
        } else if (op_byte == 0xE9) { // FLDL2T
             float80 t = ConstF80_L2T(); FpuPush(state, &t);
        } else if (op_byte == 0xEA) { // FLDL2E
             float80 t = ConstF80_L2E(); FpuPush(state, &t);
        } else if (op_byte == 0xEB) { // FLDPI
             float80 t = ConstF80_Pi(); FpuPush(state, &t);
        } else if (op_byte == 0xEC) { // FLDLG2
             float80 t = ConstF80_LG2(); FpuPush(state, &t);
        } else if (op_byte == 0xED) { // FLDLN2
             float80 t = ConstF80_LN2(); FpuPush(state, &t);
        } else if (op_byte == 0xEE) { // FLDZ
             float80 t = ConstF80_Zero(); FpuPush(state, &t);
        } else {
            OpUd2(state, op);
        }
    } else {
        // Memory Access
        uint32_t addr = ComputeEAD(state, op);
        switch (subop) {
            case 0: // FLD m32
            {
                float80 t = ReadF32(state, op);
                FpuPush(state, &t);
                break;
            }
            case 2: // FST m32
            {
                float80 val = FpuTop(state, 0);
                double d = f80_to_double(val);
                float f = (float)d;
                state->mmu.write<uint32_t>(addr, *(uint32_t*)&f);
                break;
            }
            case 3: // FSTP m32
            {
                float80 val = FpuPop(state);
                double d = f80_to_double(val);
                float f = (float)d;
                state->mmu.write<uint32_t>(addr, *(uint32_t*)&f);
                break;
            }
            case 5: // FLDCW m16
                state->ctx.fpu_cw = state->mmu.read<uint16_t>(addr);
                break;
            case 7: // FNSTCW m16
                state->mmu.write<uint16_t>(addr, state->ctx.fpu_cw);
                break;
            default: OpUd2(state, op);
        }
    }
}

void OpFpu_DA(EmuState* state, DecodedOp* op) {
    // DA: Int Arith m32
    uint8_t subop = (op->modrm >> 3) & 7;
    int32_t val32 = (int32_t)state->mmu.read<uint32_t>(ComputeEAD(state, op));
    float80 val = f80_from_int(val32);
    float80& st0 = FpuTop(state, 0);
    
    switch (subop) {
        case 0: st0 = f80_add(st0, val); break; // FIADD
        case 1: st0 = f80_mul(st0, val); break; // FIMUL
        case 4: st0 = f80_sub(st0, val); break; // FISUB
        case 5: st0 = f80_sub(val, st0); break; // FISUBR
        case 6: st0 = f80_div(st0, val); break; // FIDIV
        case 7: st0 = f80_div(val, st0); break; // FIDIVR
        default: OpUd2(state, op);
    }
}

void OpFpu_DB(EmuState* state, DecodedOp* op) {
    // DB: FILD/FIST
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
         // FCMOV, etc?
         // DB E8-EF: FUCOMI ST(i)
         // DB F0-F7: FUCOMI ST(i) ?? P?
         if ((op->modrm & 0xF8) == 0xE8) { // FUCOMI
             // Compare ST0 with ST(i) and set EFLAGS
             int idx = op->modrm & 7;
             float80 st0 = FpuTop(state, 0);
             float80 sti = FpuTop(state, idx);
             
             // Set EFLAGS (ZF, PF, CF)
             // Unordered: ZF=1, PF=1, CF=1
             // LT: CF=1
             // EQ: ZF=1
             
             state->ctx.eflags &= ~(ZF_MASK | PF_MASK | CF_MASK);
             if (f80_uncomparable(st0, sti)) {
                  state->ctx.eflags |= (ZF_MASK | PF_MASK | CF_MASK);
             } else if (f80_eq(st0, sti)) {
                  state->ctx.eflags |= ZF_MASK;
             } else if (f80_lt(st0, sti)) {
                  state->ctx.eflags |= CF_MASK;
             }
         } else if ((op->modrm & 0xF8) == 0xF0) { // FCOMI (Same as FUCOMI basically but treats NAN diff? Using same for now)
             // Wait, DB F0 is FCOMI? No DB F0 is 'FCOMI ST, ST(i)'?
             // Actually DB E8+i is FUCOMI.
             // DB F0+i is ... FCOMI? documentation varies.
             // Let's assume unimplemented unless test hits it.
             // But wait, test case uses FUCOMI (DB E8).
             // And FUCOMPI (DF E9).
             OpUd2(state, op);
         } else {
             OpUd2(state, op);
         }
    } else {
        uint32_t addr = ComputeEAD(state, op);
        switch (subop) {
            case 0: // FILD m32
            {
                int32_t val = (int32_t)state->mmu.read<uint32_t>(addr);
                float80 t = f80_from_int(val);
                FpuPush(state, &t);
                break;
            }
            case 2: // FIST m32
            {
                int32_t val = (int32_t)f80_to_int(FpuTop(state, 0));
                state->mmu.write<uint32_t>(addr, (uint32_t)val);
                break;
            }
            case 3: // FISTP m32
            {
                int32_t val = (int32_t)f80_to_int(FpuPop(state));
                state->mmu.write<uint32_t>(addr, (uint32_t)val);
                break;
            }
            case 5: // FLD m80
            {
                // Read 10 bytes
                uint64_t low = state->mmu.read<uint64_t>(addr);
                uint16_t high = state->mmu.read<uint16_t>(addr + 8);
                float80 f;
                f.signif = low;
                f.signExp = high;
                FpuPush(state, &f);
                break;
            }
            case 7: // FSTP m80
            {
                float80 f = FpuPop(state);
                state->mmu.write<uint64_t>(addr, f.signif);
                state->mmu.write<uint16_t>(addr + 8, f.signExp);
                break;
            }
            default: OpUd2(state, op);
        }
    }
}

void OpFpu_DC(EmuState* state, DecodedOp* op) {
    // DC: FPU Arith m64 (double)
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
         // FADD ST(i), ST0 etc.
         // DC C0+i: FADD ST(i), ST0
         int idx = op->modrm & 7;
         float80& dest = FpuTop(state, idx);
         float80 src = FpuTop(state, 0);
         
         switch(subop) {
             case 0: dest = f80_add(dest, src); break; // FADD
             case 1: dest = f80_mul(dest, src); break; // FMUL
             case 4: dest = f80_sub(dest, src); break; // FSUB (dest - src)
             case 5: dest = f80_sub(src, dest); break; // FSUBR (src - dest)
             case 6: dest = f80_div(dest, src); break; // FDIV
             case 7: dest = f80_div(src, dest); break; // FDIVR
             default: OpUd2(state, op);
         }
    } else {
        float80 val = ReadF64(state, op);
        float80& st0 = FpuTop(state, 0);
        switch (subop) {
            case 0: st0 = f80_add(st0, val); break; // FADD
            case 1: st0 = f80_mul(st0, val); break; // FMUL
            case 4: st0 = f80_sub(st0, val); break; // FSUB
            case 5: st0 = f80_sub(val, st0); break; // FSUBR
            case 6: st0 = f80_div(st0, val); break; // FDIV
            case 7: st0 = f80_div(val, st0); break; // FDIVR
            default: OpUd2(state, op);
        }
    }
}

void OpFpu_DD(EmuState* state, DecodedOp* op) {
    // DD: Load/Store m64
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
        // DD D0+i: FST ST(i) (Store ST0 to STi)
        // DD D8+i: FSTP ST(i)
        if (subop == 2) { // FST ST(i)
             FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
        } else if (subop == 3) { // FSTP ST(i)
             FpuTop(state, op->modrm & 7) = FpuTop(state, 0);
             FpuPop(state);
        } else {
             OpUd2(state, op);
        }
    } else {
        uint32_t addr = ComputeEAD(state, op);
        switch (subop) {
            case 0: // FLD m64
            {
                float80 t = ReadF64(state, op);
                FpuPush(state, &t);
                break;
            }
            case 2: // FST m64
            {
                double d = f80_to_double(FpuTop(state, 0));
                state->mmu.write<uint64_t>(addr, *(uint64_t*)&d);
                break;
            }
            case 3: // FSTP m64
            {
                double d = f80_to_double(FpuPop(state));
                state->mmu.write<uint64_t>(addr, *(uint64_t*)&d);
                break;
            }
            default: OpUd2(state, op);
        }
    }
}

void OpFpu_DE(EmuState* state, DecodedOp* op) {
    // DE: Arith (Pop)
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
        // DE C0-F7
        // DE C1: FADDP ST(1), ST0 (Add ST0 to ST1 and Pop)
        int idx = op->modrm & 7;
        float80& dest = FpuTop(state, idx);
        float80 src = FpuTop(state, 0);
        
        switch(subop) {
             case 0: dest = f80_add(dest, src); break; // FADDP
             case 1: dest = f80_mul(dest, src); break; // FMULP
             case 4: dest = f80_sub(src, dest); break; // FSUBRP (dest = src - dest)
             case 5: dest = f80_sub(dest, src); break; // FSUBP (dest = dest - src)
             case 6: dest = f80_div(src, dest); break; // FDIVRP (dest = src / dest)
             case 7: dest = f80_div(dest, src); break; // FDIVP (dest = dest / src)
             default: OpUd2(state, op);
        }
        FpuPop(state);
    } else {
        // Memory ops (FIADD m16 etc.) -- Not needed for test_redis_002 presumably?
        // Let's implement basics
        OpUd2(state, op);
    }
}

void OpFpu_DF(EmuState* state, DecodedOp* op) {
    // DF: m16 Int / Misc
    uint8_t subop = (op->modrm >> 3) & 7;
    
    if ((op->modrm >> 6) == 3) {
        // DF E0 status
        // DF E8+i: FUCOMIP ST(i) (Unordered Compare ST0 with STi, set flags, pop)
        if ((op->modrm & 0xF8) == 0xE8) { // FUCOMIP
             int idx = op->modrm & 7;
             float80 st0 = FpuTop(state, 0);
             float80 sti = FpuTop(state, idx);
             
             state->ctx.eflags &= ~(ZF_MASK | PF_MASK | CF_MASK);
             if (f80_uncomparable(st0, sti)) {
                  state->ctx.eflags |= (ZF_MASK | PF_MASK | CF_MASK);
             } else if (f80_eq(st0, sti)) {
                  state->ctx.eflags |= ZF_MASK;
             } else if (f80_lt(st0, sti)) {
                  state->ctx.eflags |= CF_MASK;
             }
             FpuPop(state);
        } else {
             OpUd2(state, op);
        }
    } else {
        uint32_t addr = ComputeEAD(state, op);
        switch(subop) {
            case 0: // FILD m16
            {
                int16_t val = (int16_t)state->mmu.read<uint16_t>(addr);
                float80 t = f80_from_int(val);
                FpuPush(state, &t);
                break;
            }
            case 2: // FIST m16
            {
                int16_t val = (int16_t)f80_to_int(FpuTop(state, 0));
                state->mmu.write<uint16_t>(addr, (uint16_t)val);
                break;
            }
            case 3: // FISTP m16
            {
                int16_t val = (int16_t)f80_to_int(FpuPop(state));
                state->mmu.write<uint16_t>(addr, (uint16_t)val);
                break;
            }
            case 5: // FILD m64
            {
                int64_t val = (int64_t)state->mmu.read<uint64_t>(addr);
                float80 t = f80_from_int(val);
                FpuPush(state, &t);
                break;
            }
            case 7: // FISTP m64
            {
                int64_t val = f80_to_int(FpuPop(state));
                state->mmu.write<uint64_t>(addr, (uint64_t)val);
                break;
            }
            default: OpUd2(state, op);
        }
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
    // Push Return Address (Current EIP is already advanced to Next Insn by Wrapper/Step)
    Push32(state, state->ctx.eip);
    // Jump relative to Next Insn
    state->ctx.eip += op->imm;
}

void OpRet(EmuState* state, DecodedOp* op) {
    // C3: RET
    uint32_t ret_eip = Pop32(state);
    state->ctx.eip = ret_eip;
}





// ------------------------------------------------------------------------------------------------
// Initialization
// ------------------------------------------------------------------------------------------------

// ------------------------------------------------------------------------------------------------
// SSE / SSE2 Instructions
// ------------------------------------------------------------------------------------------------

void OpDiv_Sse(EmuState* state, DecodedOp* op) {
    // 5E: DIVPS (0F), DIVSS (F3), DIVPD (66), DIVSD (F2)
    uint32_t addr = 0;
    if (op->modrm < 0xC0) addr = ComputeEAD(state, op);
    
    if (op->prefixes.flags.repne) { // F2: DIVSD (Scalar Double)
         double b;
         if (op->modrm >= 0xC0) {
             b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
         } else {
             b = state->mmu.read<double>(addr);
         }
         // DIVSD xmm1, xmm2/m64
         // DEST[63:0] = DEST[63:0] / SRC[63:0]
         // DEST[127:64] Unmodified
         ((double*)&state->ctx.xmm[(op->modrm >> 3) & 7])[0] /= b;
    } else if (op->prefixes.flags.rep) { // F3: DIVSS (Scalar Single)
         float b;
         if (op->modrm >= 0xC0) {
             b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
         } else {
             b = state->mmu.read<float>(addr);
         }
         // DIVSS xmm1, xmm2/m32
         ((float*)&state->ctx.xmm[(op->modrm >> 3) & 7])[0] /= b;
    } else {
        OpUd2(state, op);
    }
}

void OpCvt_2C(EmuState* state, DecodedOp* op) {
    // 2C: CVTTSD2SI (F2), CVTTSS2SI (F3)
    uint32_t addr = 0;
    if (op->modrm < 0xC0) addr = ComputeEAD(state, op);
    int32_t res = 0;
    
    if (op->prefixes.flags.repne) { // F2: CVTTSD2SI (Double -> Int32)
         double b;
         if (op->modrm >= 0xC0) b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
         else b = state->mmu.read<double>(addr);
         // Truncate
         res = (int32_t)b;
    } else if (op->prefixes.flags.rep) { // F3: CVTTSS2SI (Single -> Int32)
         float b;
         if (op->modrm >= 0xC0) b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
         else b = state->mmu.read<float>(addr);
         res = (int32_t)b;
    } else {
        OpUd2(state, op);
        return;
    }
    SetReg(state, (op->modrm >> 3) & 7, (uint32_t)res);
}

// ------------------------------------------------------------------------------------------------
// Group 9 (0F C7)
// ------------------------------------------------------------------------------------------------

void OpGroup9(EmuState* state, DecodedOp* op) {
    uint8_t sub = (op->modrm >> 3) & 7;
    if (sub == 1) {
        // CMPXCHG8B m64
        // Compare EDX:EAX with m64.
        // If equal, ZF=1 and m64 = ECX:EBX.
        // Else, ZF=0 and EDX:EAX = m64.
        
        uint32_t addr = ComputeEAD(state, op);
        uint64_t mem_val = state->mmu.read<uint64_t>(addr);
        
        uint32_t eax = GetReg(state, EAX);
        uint32_t edx = GetReg(state, EDX);
        uint64_t edx_eax = ((uint64_t)edx << 32) | eax;
        
        if (mem_val == edx_eax) {
            state->ctx.eflags |= ZF_MASK;
            
            uint32_t ebx = GetReg(state, EBX);
            uint32_t ecx = GetReg(state, ECX);
            uint64_t ecx_ebx = ((uint64_t)ecx << 32) | ebx;
            
            state->mmu.write<uint64_t>(addr, ecx_ebx);
        } else {
            state->ctx.eflags &= ~ZF_MASK;
            
            SetReg(state, EAX, (uint32_t)mem_val);
            SetReg(state, EDX, (uint32_t)(mem_val >> 32));
        }
    } else {
        OpUd2(state, op);
    }
}

// ------------------------------------------------------------------------------------------------
// SSE / SSE2 Additional
// ------------------------------------------------------------------------------------------------

void OpMovAp_Sse(EmuState* state, DecodedOp* op) {
    // 0F 28: MOVAPS xmm1, xmm2/m128 (Load/Move)
    // 0F 29: MOVAPS xmm2/m128, xmm1 (Store)
    // 66 Prefix: MOVAPD (Double) - Same bitwise movement usually, but alignment might differ on real HW.
    // For specialized emulation, we treat them as 128-bit moves.
    
    // Check Direction:
    // 28: Load (Gv, Ev) -> Reg = ModRM
    // 29: Store (Ev, Gv) -> ModRM = Reg
    
    // Note: Handler index will separate 28/29.
    
    uint8_t opcode = op->handler_index & 0xFF; // 28 or 29
    
    if (opcode == 0x28) { // Load
        __m128 val = ReadModRM128(state, op);
        uint8_t reg = (op->modrm >> 3) & 7;
        state->ctx.xmm[reg] = val;
    } else { // Store 0x29
        uint8_t reg = (op->modrm >> 3) & 7;
        __m128 val = state->ctx.xmm[reg];
        // WriteModRM128 logic inline
        if (op->modrm >= 0xC0) {
            state->ctx.xmm[op->modrm & 7] = val;
        } else {
            uint32_t addr = ComputeEAD(state, op);
            // MOVAPS/MOVAPD require alignment check?
            // Ignoring for SoftMMU unless strict mode.
            // Using generic write128 helper if available or manual.
            if (op->modrm < 0xC0) {
                 // Check alignment?
                 // if ((addr & 15) != 0) { Fault(#GP); return; }
            }
            state->mmu.write<uint64_t>(addr, ((uint64_t*)&val)[0]);
            state->mmu.write<uint64_t>(addr+8, ((uint64_t*)&val)[1]);
        }
    }
}

void OpMaxMin_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5F: MAX (PD/SD/PS/SS)
    // 0F 5D: MIN (PD/SD/PS/SS)
    // Prefix determines type:
    // None: PS (Packed Single)
    // 66:   PD (Packed Double)
    // F3:   SS (Scalar Single)
    // F2:   SD (Scalar Double)
    
    bool is_min = (op->handler_index & 0xFF) == 0x5D;
    uint8_t reg_idx = (op->modrm >> 3) & 7;
    
    if (op->prefixes.flags.repne) { // F2: Scalar Double
        double b;
        if (op->modrm >= 0xC0) b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        else b = state->mmu.read<double>(ComputeEAD(state, op));
        
        double* dest = (double*)&state->ctx.xmm[reg_idx]; // [0]
        if (is_min) *dest = (*dest < b) ? *dest : b;
        else        *dest = (*dest > b) ? *dest : b;
        // High 64 bits unmodified
        
    } else if (op->prefixes.flags.rep) { // F3: Scalar Single
        float b;
        if (op->modrm >= 0xC0) b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        else b = state->mmu.read<float>(ComputeEAD(state, op));
        
        float* dest = (float*)&state->ctx.xmm[reg_idx]; // [0]
        if (is_min) *dest = (*dest < b) ? *dest : b;
        else        *dest = (*dest > b) ? *dest : b;
        // High 96 bits unmodified
        
    } else if (op->prefixes.flags.opsize) { // 66: Packed Double
        __m128 val = ReadModRM128(state, op);
        simde__m128d a = simde_mm_castps_pd(state->ctx.xmm[reg_idx]);
        simde__m128d b = simde_mm_castps_pd(val);
        simde__m128d res = is_min ? simde_mm_min_pd(a, b) : simde_mm_max_pd(a, b);
        state->ctx.xmm[reg_idx] = simde_mm_castpd_ps(res);
        
    } else { // None: Packed Single
        __m128 val = ReadModRM128(state, op);
        __m128 a = state->ctx.xmm[reg_idx];
        // Using SIMDe directly on __m128 (aliased to simde__m128)
        state->ctx.xmm[reg_idx] = is_min ? simde_mm_min_ps(a, val) : simde_mm_max_ps(a, val);
    }
}

// Initialize Loop
HandlerFunc g_Handlers[1024] = {0};

void OpXadd_Rm_R(EmuState* state, DecodedOp* op) {
    // XADD r/m, r: Exchange and Add
    
    uint32_t width = 4;
    if (op->handler_index == 0x1C0) { // 0F C0 -> Byte
        width = 1;
    } else { // 0F C1
        if (op->prefixes.flags.opsize) width = 2;
        else width = 4;
    }
    
    // ---------------------------------------------------------
    // 1. Read Dest (E: R/M)
    // ---------------------------------------------------------
    uint32_t dest_val = 0;
    if (width == 1) dest_val = ReadModRM8(state, op);
    else if (width == 2) dest_val = ReadModRM16(state, op);
    else dest_val = ReadModRM32(state, op);
    
    // ---------------------------------------------------------
    // 2. Read Src (G: Reg)
    // ---------------------------------------------------------
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src_val = 0;
    if (width == 1) src_val = GetReg8(state, reg);
    else if (width == 2) src_val = GetReg(state, reg) & 0xFFFF;
    else src_val = GetReg(state, reg);
    
    // ---------------------------------------------------------
    // 3. ALU Add
    // ---------------------------------------------------------
    uint32_t res = 0;
    if (width == 1) res = AluAdd(state, (uint8_t)dest_val, (uint8_t)src_val);
    else if (width == 2) res = AluAdd(state, (uint16_t)dest_val, (uint16_t)src_val);
    else res = AluAdd(state, (uint32_t)dest_val, (uint32_t)src_val);
    
    // ---------------------------------------------------------
    // 4. Write Old Dest to Src (Reg)
    // ---------------------------------------------------------
    if (width == 1) {
        uint32_t* rptr = GetRegPtr(state, reg & 3);
        uint32_t curr = *rptr;
        if (reg < 4) curr = (curr & 0xFFFFFF00) | (dest_val & 0xFF);
        else curr = (curr & 0xFFFF00FF) | ((dest_val & 0xFF) << 8);
        *rptr = curr;
    } else if (width == 2) {
        uint32_t* rptr = GetRegPtr(state, reg);
        uint32_t curr = *rptr;
        curr = (curr & 0xFFFF0000) | (dest_val & 0xFFFF);
        *rptr = curr;
    } else {
        SetReg(state, reg, dest_val);
    }
    
    // ---------------------------------------------------------
    // 5. Write Result to Dest (E: R/M)
    // ---------------------------------------------------------
    // Since WriteModRM8 might not exist or be exported, use manual logic
    // But Wait, OpMov_EvGv uses WriteModRM32.
    // Ops like OpMovzx don't write.
    // Let's rely on manual write using ComputeEAD if memory.
    
    if (op->modrm >= 0xC0) {
        // Register Check
        uint8_t rm = op->modrm & 7;
        if (width == 1) {
             uint32_t* rptr = GetRegPtr(state, rm & 3);
             uint32_t curr = *rptr;
             if (rm < 4) curr = (curr & 0xFFFFFF00) | (res & 0xFF);
             else curr = (curr & 0xFFFF00FF) | ((res & 0xFF) << 8);
             *rptr = curr;
        } else if (width == 2) {
             uint32_t* rptr = GetRegPtr(state, rm);
             uint32_t curr = *rptr;
             curr = (curr & 0xFFFF0000) | (res & 0xFFFF);
             *rptr = curr;
        } else {
             SetReg(state, rm, res);
        }
    } else {
        // Memory
        uint32_t addr = ComputeEAD(state, op);
        if (width == 1) state->mmu.write<uint8_t>(addr, (uint8_t)res);
        else if (width == 2) state->mmu.write<uint16_t>(addr, (uint16_t)res);
        else state->mmu.write<uint32_t>(addr, res);
    }
}

struct HandlerInit {
    HandlerInit() {
        // 1. Clear All
        for (int i=0; i<1024; ++i) g_Handlers[i] = nullptr;
        
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
        g_Handlers[0xCD] = DispatchWrapper<OpInt>;      // INT imm8
        g_Handlers[0xCC] = DispatchWrapper<OpInt3>;     // INT3
        
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
        // UD2 (0F 0B) -> #UD
        g_Handlers[0x10B] = DispatchWrapper<OpUd2>;
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

        // Batch 002 (Integer)
        g_Handlers[0x69] = DispatchWrapper<OpImul_GvEvIz>;
        g_Handlers[0x6B] = DispatchWrapper<OpImul_GvEvIz>;
        g_Handlers[0x1AF] = DispatchWrapper<OpImul_GvEv>;
        g_Handlers[0xF6] = DispatchWrapper<OpGroup3_Ev>;
        g_Handlers[0xF7] = DispatchWrapper<OpGroup3_Ev>;
        g_Handlers[0xFE] = DispatchWrapper<OpGroup4_Eb>;
        
        // Batch 002 (FPU)
        g_Handlers[0xD8] = DispatchWrapper<OpFpu_D8>;
        g_Handlers[0xD9] = DispatchWrapper<OpFpu_D9>;
        g_Handlers[0xDA] = DispatchWrapper<OpFpu_DA>;
        g_Handlers[0xDB] = DispatchWrapper<OpFpu_DB>;
        g_Handlers[0xDC] = DispatchWrapper<OpFpu_DC>;
        g_Handlers[0xDD] = DispatchWrapper<OpFpu_DD>;
        g_Handlers[0xDE] = DispatchWrapper<OpFpu_DE>;
        g_Handlers[0xDF] = DispatchWrapper<OpFpu_DF>;
        
        // SSE New
        g_Handlers[0x12C] = DispatchWrapper<OpCvt_2C>;
        g_Handlers[0x15E] = DispatchWrapper<OpDiv_Sse>;
        
        // Batch 003
        g_Handlers[0x1C7] = DispatchWrapper<OpGroup9>;
        g_Handlers[0x128] = DispatchWrapper<OpMovAp_Sse>;
        g_Handlers[0x129] = DispatchWrapper<OpMovAp_Sse>;
        g_Handlers[0x15F] = DispatchWrapper<OpMaxMin_Sse>;
        g_Handlers[0x15D] = DispatchWrapper<OpMaxMin_Sse>;
        
        // XADD
        g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Rm_R>;
        g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Rm_R>;
    }
};

static HandlerInit _init;

} // namespace x86emu

namespace x86emu {
void OpInt(EmuState* state, DecodedOp* op) {
    // CD ib: INT imm8
    // Note: Decoder puts imm8 in op->imm
    uint8_t vector = (uint8_t)op->imm;
    // printf("[Sim] OpInt: Vector %02X\n", vector);
    if (!state->hooks.on_interrupt(state, vector)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector; // Fault with vector
        // NOTE: Real hardware might GPF if IDT descriptor is bad, 
        // but for us "Unhandled Interrupt" is a Fault.
    }
}

void OpInt3(EmuState* state, DecodedOp* op) {
    // CC: INT3 (Vector 3, Breakpoint)
    if (!state->hooks.on_interrupt(state, 3)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 3;
    }
}

void OpDecodeFault(EmuState* state, DecodedOp* op) {
    // Trigger Decode Fault #UD
    if (!state->hooks.on_decode_fault(state)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 6;
    }
}
} // namespace x86emu
