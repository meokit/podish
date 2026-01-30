#include "ops.h"
#include "ops.h"
#include "state.h"
#include "exec_utils.h"
#include <cstdio>
#include <iostream>

namespace x86emu {

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

void OpMov_EvIz(EmuState* state, DecodedOp* op) {
    // C7: MOV r/m32, imm32
    WriteModRM32(state, op, op->imm);
}

// ------------------------------------------------------------------------------------------------
// Arithmetic
// ------------------------------------------------------------------------------------------------

// ------------------------------------------------------------------------------------------------
// Arithmetic & Logic
// ------------------------------------------------------------------------------------------------

void OpAdd_EvGv(EmuState* state, DecodedOp* op) {
    // 01: ADD r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAdd(state, dest, src);
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

void OpAnd_EvGv(EmuState* state, DecodedOp* op) {
    // 21: AND r/m32, r32
    uint32_t dest = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src = GetReg(state, reg);
    
    uint32_t res = AluAnd(state, dest, src);
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

void OpGroup1_EvIz(EmuState* state, DecodedOp* op) {
    // 81: Arith r/m32, imm32
    // 83: Arith r/m32, imm8 (sign-extended)
    
    uint32_t dest = ReadModRM32(state, op);
    uint32_t src = op->imm;
    
    // Sign-extend if 83
    if ((op->handler_index & 0xFF) == 0x83) {
        src = (int32_t)(int8_t)src;
    }
    
    uint8_t subop = (op->modrm >> 3) & 7;
    uint32_t res = 0;
    
    switch (subop) {
        case 0: // ADD
            res = AluAdd(state, dest, src);
            WriteModRM32(state, op, res);
            break;
        case 1: // OR
            res = AluOr(state, dest, src);
            WriteModRM32(state, op, res);
            break;
        case 4: // AND
            res = AluAnd(state, dest, src);
            WriteModRM32(state, op, res);
            break;
        case 5: // SUB
            res = AluSub(state, dest, src);
            WriteModRM32(state, op, res);
            break;
        case 6: // XOR
            res = AluXor(state, dest, src);
            WriteModRM32(state, op, res);
            break;
        case 7: // CMP
            AluSub(state, dest, src); // Discard result
            break;
        default:
             // TODO: ADC(2), SBB(3)
             OpNotImplemented(state, op);
             break;
    }
}

void OpGroup5_Ev(EmuState* state, DecodedOp* op) {
    // FF: Group 5
    uint8_t subop = (op->modrm >> 3) & 7;
    uint32_t dest = 0;
    
    // Read dest for INC/DEC/PUSH? 
    // JMP/CALL use dest as Target Address (pointer).
    // PUSH uses dest as Value to Push.
    
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
             // Not validating Rank, but simple implementation:
             // Target is *in* Ev? No. Ev *is* the Target Address?
             // "CALL r/m32" -> Push EIP, EIP = [r/m32] ?? No.
             // "CALL r/m32" (FF /2).
             // Operand is r/m32.
             // If r/m32 is memory, we read it?
             // Blink: `LoadModrmMode(kModeNormal)`.
             // It loads the value from memory/reg.
             // That value IS the target EIP (Absolute).
             // Not Relative.
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

void OpHlt(EmuState* state, DecodedOp* op) {
    // HLT (0xF4)
    state->status = EmuStatus::Stopped;
}

void OpJmp_Rel(EmuState* state, DecodedOp* op) {
    // E9: JMP rel32
    // E9/EB: JMP rel32/rel8
    // EIP is already at the next instruction, so we add the offset.
    state->ctx.eip += op->imm;
}

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
        // 1. Set NOP
        g_Handlers[0x90] = DispatchWrapper<OpNop>;
        
        // 2. Set MOV
        g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
        g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;
        for(int i=0; i<8; ++i) g_Handlers[0xB8+i] = DispatchWrapper<OpMov_RegImm>;
        g_Handlers[0xC7] = DispatchWrapper<OpMov_EvIz>; // MOV r/m32, imm32
        
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
        g_Handlers[0x01] = DispatchWrapper<OpAdd_EvGv>;
        g_Handlers[0x09] = DispatchWrapper<OpOr_EvGv>;
        g_Handlers[0x21] = DispatchWrapper<OpAnd_EvGv>;
        g_Handlers[0x31] = DispatchWrapper<OpXor_EvGv>;
        g_Handlers[0x85] = DispatchWrapper<OpTest_EvGv>;
        
        g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz>;
        g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIz>;
        
        // Group 5
        g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev>;
        
        // Inc/Dec
        for (int i=0; i<8; ++i) {
            g_Handlers[0x40+i] = DispatchWrapper<OpInc_Reg>;
            g_Handlers[0x48+i] = DispatchWrapper<OpDec_Reg>;
        }
    }
};

static HandlerInit _init;

}
