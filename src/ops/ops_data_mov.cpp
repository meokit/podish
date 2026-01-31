// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

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

void OpMov_EbGb(EmuState* state, DecodedOp* op) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    WriteModRM8(state, op, val);
}

void OpMov_GbEb(EmuState* state, DecodedOp* op) {
    // MOV r8, r/m8 (0x8A)
    // Load 8-bit ModRM into Reg
    uint8_t val = ReadModRM8(state, op);
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

void OpMov_EbIb(EmuState* state, DecodedOp* op) {
    // MOV r/m8, imm8 (0xC6)
    uint8_t val = (uint8_t)op->imm;
    WriteModRM8(state, op, val);
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

template<typename T>
void Helper_Movs(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400); // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);
    
    // REP handling
    if (op->prefixes.flags.rep) {
        uint32_t ecx = GetReg(state, ECX);
        while (ecx > 0) {
            uint32_t esi = GetReg(state, ESI);
            uint32_t edi = GetReg(state, EDI);
            
            // DS:ESI -> ES:EDI
            // For now assume flat model (DS=0, ES=0)
            uint32_t src_addr = esi + GetSegmentBase(state, op);
            
            T val = state->mmu.read<T>(src_addr);
            state->mmu.write<T>(edi, val);
            
            SetReg(state, ESI, esi + step);
            SetReg(state, EDI, edi + step);
            
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        
        T val = state->mmu.read<T>(src_addr);
        state->mmu.write<T>(edi, val);
        
        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    }
}

void OpMovs_Byte(EmuState* state, DecodedOp* op) {
    Helper_Movs<uint8_t>(state, op);
}

void OpMovs_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {
        Helper_Movs<uint16_t>(state, op);
    } else {
        Helper_Movs<uint32_t>(state, op);
    }
}

template<typename T>
void Helper_Stos(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400); // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);
    
    // Get Value (AL/AX/EAX)
    T val;
    if constexpr (sizeof(T) == 1) val = (T)GetReg(state, EAX); // AL
    else if constexpr (sizeof(T) == 2) val = (T)(GetReg(state, EAX) & 0xFFFF); // AX
    else val = (T)GetReg(state, EAX); // EAX
    
    if (op->prefixes.flags.rep) {
        uint32_t ecx = GetReg(state, ECX);
        while (ecx > 0) {
            uint32_t edi = GetReg(state, EDI);
            // Dest ES:EDI
            state->mmu.write<T>(edi, val);
            
            SetReg(state, EDI, edi + step);
            
            ecx--;
            SetReg(state, ECX, ecx);
        }
    } else {
        uint32_t edi = GetReg(state, EDI);
        state->mmu.write<T>(edi, val);
        SetReg(state, EDI, edi + step);
    }
}

void OpStos_Byte(EmuState* state, DecodedOp* op) {
    Helper_Stos<uint8_t>(state, op);
}

void OpStos_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {
        Helper_Stos<uint16_t>(state, op);
    } else {
        Helper_Stos<uint32_t>(state, op);
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

void OpXchg_EvGv(EmuState* state, DecodedOp* op) {
    // XCHG r/m32, r32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t reg_val = GetReg(state, reg);
    uint32_t rm_val = ReadModRM32(state, op);
    
    WriteModRM32(state, op, reg_val);
    SetReg(state, reg, rm_val);
}

} // namespace x86emu

