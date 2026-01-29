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
    // We can infer opcode from handler index or op data
    // For now simple print
    fprintf(stderr, "[Sim] Opcode Not Implemented (Idx: %04X)\n", op->handler_index);
    // Maybe trigger fault or exit?
    // For now just log
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
    // If imm8, decoder sign-extended it? 
    // Let's check decoder.cpp: "else if (imm_len == 1) op->imm = *reinterpret_cast<const uint8_t*>(ptr);"
    // It cast to uint8_t, so it wasn't sign extended in `imm`.
    // Wait, PUSH imm8 (6A) is signed extended to 32 bits.
    // My decoder might treat it as unsigned 8-bit in `op->imm`.
    // We should check `length` or `handler_index` to decide casting?
    // Or check `imm_len` from table?
    // For 6A, imm type is 5 (Byte Signed). GetImmLength returns 1.
    // Decoder: "op->imm = *reinterpret_cast<const uint8_t*>(ptr)".
    // So op->imm contains 0x000000XX.
    // We need to sign extend if opcode is 6A.
    
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
    // printf("[Sim] HLT Reached. Stopping.\n");
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
    }
};

static HandlerInit _init;

}
