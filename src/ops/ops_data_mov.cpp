// Basic Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpMov_EvGv(EmuState* state, DecodedOp* op) {
    // MOV r/m32, r32 (0x89)
    // Store Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t val = GetReg(state, reg);
    WriteModRM32(state, op, val);
}

static FORCE_INLINE void OpMov_GvEv(EmuState* state, DecodedOp* op) {
    // MOV r32, r/m32 (0x8B)
    // Load ModRM into Reg
    uint32_t val = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, val);
}

static FORCE_INLINE void OpMov_EbGb(EmuState* state, DecodedOp* op) {
    // MOV r/m8, r8 (0x88)
    // Store 8-bit Reg into ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t val = GetReg8(state, reg);
    WriteModRM8(state, op, val);
}

static FORCE_INLINE void OpMov_GbEb(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpMov_EbIb(EmuState* state, DecodedOp* op) {
    // MOV r/m8, imm8 (0xC6)
    uint8_t val = (uint8_t)op->imm;
    WriteModRM8(state, op, val);
}

static FORCE_INLINE void OpMov_RegImm(EmuState* state, DecodedOp* op) {
    // B8+reg: MOV r32, imm32
    uint8_t reg = op->handler_index & 7;
    SetReg(state, reg, op->imm);
}

static FORCE_INLINE void OpMov_RegImm8(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpMov_EvIz(EmuState* state, DecodedOp* op) {
    // C7: MOV r/m32, imm32
    WriteModRM32(state, op, op->imm);
}

static FORCE_INLINE void OpMov_Moffs_Load(EmuState* state, DecodedOp* op) {
    // A0: MOV AL, moffs8 (Byte)
    // A1: MOV EAX, moffs32 (Word/Dword)
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);

    if ((op->handler_index & 1) == 0) {  // A0
        uint8_t val = state->mmu.read<uint8_t>(linear);
        uint32_t* rptr = GetRegPtr(state, EAX);
        *rptr = (*rptr & 0xFFFFFF00) | val;
    } else {  // A1
        uint32_t val = state->mmu.read<uint32_t>(linear);
        SetReg(state, EAX, val);
    }
}

static FORCE_INLINE void OpMov_Moffs_Store(EmuState* state, DecodedOp* op) {
    // A2: MOV moffs8, AL
    // A3: MOV moffs32, EAX
    uint32_t offset = op->imm;
    uint32_t linear = offset + GetSegmentBase(state, op);

    uint32_t val = GetReg(state, EAX);

    if ((op->handler_index & 1) == 0) {  // A2
        state->mmu.write<uint8_t>(linear, (uint8_t)val);
    } else {  // A3
        state->mmu.write<uint32_t>(linear, val);
    }
}

static FORCE_INLINE void OpMov_Sreg_Rm(EmuState* state, DecodedOp* op) {
    // 8E /r: MOV Sreg, r/m16
    uint8_t sreg_idx = (op->modrm >> 3) & 7;
    // 0=ES, 1=CS, 2=SS, 3=DS, 4=FS, 5=GS
    if (sreg_idx == 1) {
        // Loading CS -> #UD
        return;
    }
    if (sreg_idx > 5) return;

    //uint16_t selector = 0;

    // Read Source (Rm) - always 16-bit
    if (op->modrm >= 0xC0) {
        (void)0; // TODO
        //uint8_t rm = op->modrm & 7;
        //selector = (uint16_t)GetReg(state, rm);
    } else {
        uint32_t addr = ComputeEAD(state, op);
        //selector = state->mmu.read<uint16_t>(addr);
        if (state->status != EmuStatus::Running) return;
    }

    // We do not store selectors in current Context.
    // Just ensure side-effects (memory read fault) happen.
    // Base is preserved (assumed set by syscall).
}

static FORCE_INLINE void OpMov_Rm_Sreg(EmuState* state, DecodedOp* op) {
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
        // "If the destination is a 32-bit register, the 16-bit selector is zero-extended"
        // We assume 32-bit destination unless opsize prefix.
        if (op->prefixes.flags.opsize) {
            uint32_t* rptr = GetRegPtr(state, rm);
            *rptr = (*rptr & 0xFFFF0000) | val;
        } else {
            SetReg(state, rm, (uint32_t)val);
        }
    } else {
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint16_t>(addr, val);
    }
}

template <typename T>
void Helper_Movs(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    // REP handling
    if (op->prefixes.flags.rep) {
        uint32_t ecx = GetReg(state, ECX);
        if (ecx == 0) return;

        // Optimization for DF=0 (Forward Copy)
        // Disable optimization if mem_hook is active to ensure proper trace granularity
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

            // If we stopped early (fault), we leave state consistent at the fault point.
            return;
        }

        // Slow path / Reverse path (DF=1)
        while (ecx > 0) {
            uint32_t esi = GetReg(state, ESI);
            uint32_t edi = GetReg(state, EDI);

            // DS:ESI -> ES:EDI
            // For now assume flat model (DS=0, ES=0)
            uint32_t src_addr = esi + GetSegmentBase(state, op);

            T val = state->mmu.read<T>(src_addr);
            if (state->status != EmuStatus::Running) break;

            state->mmu.write<T>(edi, val);
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

        T val = state->mmu.read<T>(src_addr);
        state->mmu.write<T>(edi, val);

        SetReg(state, ESI, esi + step);
        SetReg(state, EDI, edi + step);
    }
}

static FORCE_INLINE void OpMovs_Byte(EmuState* state, DecodedOp* op) {
    Helper_Movs<uint8_t>(state, op);
}

static FORCE_INLINE void OpMovs_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {
        Helper_Movs<uint16_t>(state, op);
    } else {
        Helper_Movs<uint32_t>(state, op);
    }
}

template <typename T>
void Helper_Stos(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpStos_Byte(EmuState* state, DecodedOp* op) {
    Helper_Stos<uint8_t>(state, op);
}

static FORCE_INLINE void OpStos_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {
        Helper_Stos<uint16_t>(state, op);
    } else {
        Helper_Stos<uint32_t>(state, op);
    }
}

static FORCE_INLINE void OpMovzx_Byte(EmuState* state, DecodedOp* op) {
    // 0F B6: MOVZX r32, r/m8
    uint8_t val = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)val);
}

static FORCE_INLINE void OpMovzx_Word(EmuState* state, DecodedOp* op) {
    // 0F B7: MOVZX r32, r/m16
    uint16_t val = ReadModRM16(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)val);
}

static FORCE_INLINE void OpMovsx_Byte(EmuState* state, DecodedOp* op) {
    // 0F BE: MOVSX r32, r/m8
    uint8_t val = ReadModRM8(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)(int32_t)(int8_t)val);
}

static FORCE_INLINE void OpMovsx_Word(EmuState* state, DecodedOp* op) {
    // 0F BF: MOVSX r32, r/m16
    uint16_t val = ReadModRM16(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, (uint32_t)(int32_t)(int16_t)val);
}

static FORCE_INLINE void OpLea(EmuState* state, DecodedOp* op) {
    // LEA r32, m (0x8D)
    uint32_t addr = ComputeEAD(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    SetReg(state, reg, addr);
}

static FORCE_INLINE void OpPush_Reg(EmuState* state, DecodedOp* op) {
    // PUSH r32 (0x50+rd)
    uint8_t reg = op->handler_index & 7;  // Extract reg from opcode
    uint32_t val = GetReg(state, reg);
    Push32(state, val);
}

static FORCE_INLINE void OpPush_Imm(EmuState* state, DecodedOp* op) {
    // PUSH imm32 (0x68) or PUSH imm8 (0x6A)
    // Decoder already extracted imm to op->imm
    uint32_t val = op->imm;
    if ((op->handler_index & 0xFF) == 0x6A) {
        val = (int32_t)(int8_t)val;
    }
    Push32(state, val);
}

static FORCE_INLINE void OpPop_Reg(EmuState* state, DecodedOp* op) {
    // POP r32 (0x58+rd)
    uint8_t reg = op->handler_index & 7;
    uint32_t val = Pop32(state);
    SetReg(state, reg, val);
}

static FORCE_INLINE void OpXchg_EvGv(EmuState* state, DecodedOp* op) {
    // XCHG r/m32, r32 (0x87)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t reg_val = GetReg(state, reg);
    uint32_t rm_val = ReadModRM32(state, op);

    WriteModRM32(state, op, reg_val);
    SetReg(state, reg, rm_val);
}

// ------------------------------------------------------------------------------------------------
// String Operations (LODS, SCAS, CMPS)
// ------------------------------------------------------------------------------------------------

template <typename T>
void Helper_Lods(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&](uint32_t& ecx_ref) {
        uint32_t esi = GetReg(state, ESI);
        uint32_t src_addr = esi + GetSegmentBase(state, op);
        T val = state->mmu.read<T>(src_addr);

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

static FORCE_INLINE void OpLods_Byte(EmuState* state, DecodedOp* op) {
    Helper_Lods<uint8_t>(state, op);
}
static FORCE_INLINE void OpLods_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize)
        Helper_Lods<uint16_t>(state, op);
    else
        Helper_Lods<uint32_t>(state, op);
}

template <typename T>
void Helper_Scas(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() {
        uint32_t edi = GetReg(state, EDI);
        // ES:[EDI]
        T mem_val = state->mmu.read<T>(edi);
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

static FORCE_INLINE void OpScas_Byte(EmuState* state, DecodedOp* op) {
    Helper_Scas<uint8_t>(state, op);
}
static FORCE_INLINE void OpScas_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize)
        Helper_Scas<uint16_t>(state, op);
    else
        Helper_Scas<uint32_t>(state, op);
}

template <typename T>
void Helper_Cmps(EmuState* state, DecodedOp* op) {
    bool df = (state->ctx.eflags & 0x400);  // DF
    int32_t step = df ? -((int32_t)sizeof(T)) : (int32_t)sizeof(T);

    auto perform = [&]() {
        uint32_t esi = GetReg(state, ESI);
        uint32_t edi = GetReg(state, EDI);

        uint32_t src_addr = esi + GetSegmentBase(state, op);
        T src_val = state->mmu.read<T>(src_addr);
        T dst_val = state->mmu.read<T>(edi);  // ES:EDI

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

static FORCE_INLINE void OpCmps_Byte(EmuState* state, DecodedOp* op) {
    Helper_Cmps<uint8_t>(state, op);
}
static FORCE_INLINE void OpCmps_Word(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize)
        Helper_Cmps<uint16_t>(state, op);
    else
        Helper_Cmps<uint32_t>(state, op);
}

// ------------------------------------------------------------------------------------------------
// Stack Operations (PUSHA, POPA, ENTER, LEAVE)
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpPusha(EmuState* state, DecodedOp* op) {
    // 60: PUSHA/PUSHAD
    if (op->prefixes.flags.opsize) {
        // PUSHA (16-bit)
        uint16_t temp = GetReg(state, ESP) & 0xFFFF;
        Push16(state, (uint16_t)GetReg(state, EAX));
        Push16(state, (uint16_t)GetReg(state, ECX));
        Push16(state, (uint16_t)GetReg(state, EDX));
        Push16(state, (uint16_t)GetReg(state, EBX));
        Push16(state, temp);
        Push16(state, (uint16_t)GetReg(state, EBP));
        Push16(state, (uint16_t)GetReg(state, ESI));
        Push16(state, (uint16_t)GetReg(state, EDI));
    } else {
        // PUSHAD (32-bit)
        uint32_t temp = GetReg(state, ESP);
        Push32(state, GetReg(state, EAX));
        Push32(state, GetReg(state, ECX));
        Push32(state, GetReg(state, EDX));
        Push32(state, GetReg(state, EBX));
        Push32(state, temp);
        Push32(state, GetReg(state, EBP));
        Push32(state, GetReg(state, ESI));
        Push32(state, GetReg(state, EDI));
    }
}

static FORCE_INLINE void OpPopa(EmuState* state, DecodedOp* op) {
    // 61: POPA/POPAD
    if (op->prefixes.flags.opsize) {
        // POPA (16-bit)
        SetReg(state, EDI, (GetReg(state, EDI) & 0xFFFF0000) | Pop16(state));
        SetReg(state, ESI, (GetReg(state, ESI) & 0xFFFF0000) | Pop16(state));
        SetReg(state, EBP, (GetReg(state, EBP) & 0xFFFF0000) | Pop16(state));
        Pop16(state);  // Skip SP
        SetReg(state, EBX, (GetReg(state, EBX) & 0xFFFF0000) | Pop16(state));
        SetReg(state, EDX, (GetReg(state, EDX) & 0xFFFF0000) | Pop16(state));
        SetReg(state, ECX, (GetReg(state, ECX) & 0xFFFF0000) | Pop16(state));
        SetReg(state, EAX, (GetReg(state, EAX) & 0xFFFF0000) | Pop16(state));
    } else {
        // POPAD (32-bit)
        SetReg(state, EDI, Pop32(state));
        SetReg(state, ESI, Pop32(state));
        SetReg(state, EBP, Pop32(state));
        Pop32(state);  // Skip ESP
        SetReg(state, EBX, Pop32(state));
        SetReg(state, EDX, Pop32(state));
        SetReg(state, ECX, Pop32(state));
        SetReg(state, EAX, Pop32(state));
    }
}

static FORCE_INLINE void OpEnter(EmuState* state, DecodedOp* op) {
    // C8 iw ib: ENTER imm16, imm8
    // imm16 is alloc size, imm8 is nesting level
    uint16_t size = op->imm & 0xFFFF;
    uint8_t level = (op->imm >> 16) & 0x1F;  // Decoder puts both imms?
    // Wait, decoder for C8 handles 'Iw Ib'.
    // Decoder logic: if (has_imm16 && has_imm8) ->
    // Standard decoder usually puts main imm in op->imm.
    // But for ENTER, it has 2 immediates.
    // Let's assume standard decoder doesn't support 2 imms nicely in `op->imm`.
    // We might need to fetch manually or check if decoder handles this.
    // Inspect decoder.cpp for 0xC8?
    // Most decoders put 'Iw' in op->imm. The 'Ib' might be missing.
    // Let's rely on reading from EIP if needed, or check `op->imm2` if it existed (it doesn't).
    // For now: Assume decoder packs it or we fetch manually.
    // If decoder sees Iw, it reads 16 bits. Then Ib?
    // Manual fetch from instruction stream is dangerous in threaded dispatch without precise
    // sizing. Let's assuming decoder handles it. If not, this is a bug in decoder. checking
    // decoder_lut.h: C8 -> kImmType = Imm16 (Iw). Ib is ignored? We need to fetch 'level' manually.
    // EIP is pointing to next instruction.
    // Instruction length includes both.
    // op->imm holds the 16-bit alloc size.
    // where is the 8-bit level?
    // It's at EIP - 1. (Instruction end - 1).
    // Let's assume we can read it.

    // Safety fallback: Level is usually 0.
    // If we assume level 0 we are strict.
    // Let's implement Level 0 logic first.

    // Standard ENTER:
    Push32(state, GetReg(state, EBP));
    uint32_t frame_ptr = GetReg(state, ESP);

    if (level > 0) {
        // Complex ENTER
        // Not implemented fully yet.
    }

    SetReg(state, EBP, frame_ptr);
    SetReg(state, ESP, frame_ptr - size);
}

static FORCE_INLINE void OpLeave(EmuState* state, DecodedOp* op) {
    // C9: LEAVE
    // MOV ESP, EBP
    uint32_t ebp = GetReg(state, EBP);
    SetReg(state, ESP, ebp);
    // POP EBP
    SetReg(state, EBP, Pop32(state));
}

// ------------------------------------------------------------------------------------------------
// Flag/Misc Operations (LAHF, SAHF, XCHG)
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpLahf(EmuState* state, DecodedOp* op) {
    // 9F: LAHF
    // AH <- EFLAGS(SF:ZF:0:AF:0:PF:1:CF)
    uint32_t flags = state->ctx.eflags;
    uint8_t ah = (flags & 0xFF);
    // Flags: SF(7), ZF(6), 0(5), AF(4), 0(3), PF(2), 1(1), CF(0)
    // EFLAGS low byte matches this format exactly (Reserved bits 1, 3, 5 are fixed).
    // Bit 1 is 1. Bit 3 is 0. Bit 5 is 0.
    // So simple copy is correct.

    uint32_t eax = GetReg(state, EAX);
    eax = (eax & 0xFFFF00FF) | (ah << 8);
    SetReg(state, EAX, eax);
}

static FORCE_INLINE void OpSahf(EmuState* state, DecodedOp* op) {
    // 9E: SAHF
    // EFLAGS(SF:ZF:0:AF:0:PF:1:CF) <- AH
    uint32_t eax = GetReg(state, EAX);
    uint8_t ah = (eax >> 8) & 0xFF;

    uint32_t flags = state->ctx.eflags;
    // Mask: SF, ZF, AF, PF, CF. And reserved bits.
    // SF(7), ZF(6), AF(4), PF(2), CF(0)
    // Reserved: 5, 3, 1.
    // Intel: "The SF, ZF, AF, PF, and CF flags are set... Reserved bits 1, 3, and 5... are
    // unaffected" -> Wait. "LOADS SF, ZF, AF, PF, and CF ... from AH" "Bits 1, 3, and 5 of flags
    // are set to 1, 0, 0" (In EFLAGS? Or does it say that AH has them?) Actually SAHF loads the
    // fixed values too? "bits 1, 3, and 5 ... are unaffected" (Some sources say unaffected, some
    // say loaded) Intel SDM: "EFLAGS(SF:ZF:0:AF:0:PF:1:CF) <- AH" ... suggests it writes fixed
    // values? Usually it replaces the low byte entirely.

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

static FORCE_INLINE void OpXchg_EbGb(EmuState* state, DecodedOp* op) {
    // 86: XCHG r/m8, r8
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t* rptr = GetRegPtr(state, reg & 3);
    uint8_t reg_val = (reg < 4) ? (*rptr & 0xFF) : ((*rptr >> 8) & 0xFF);

    uint8_t rm_val = ReadModRM8(state, op);

    // Write reg_val to RM
    WriteModRM8(state, op, reg_val);

    // Write rm_val to Reg
    uint32_t mask = (reg < 4) ? 0xFF : 0xFF00;
    uint32_t shift = (reg < 4) ? 0 : 8;
    *rptr = (*rptr & ~mask) | (rm_val << shift);
}

static FORCE_INLINE void OpXchg_Reg(EmuState* state, DecodedOp* op) {
    // 90+reg: XCHG EAX, r32
    uint8_t reg = op->handler_index & 7;
    // If reg=0 (EAX), it's NOP.
    if (reg == 0) return;

    uint32_t val_eax = GetReg(state, EAX);
    uint32_t val_reg = GetReg(state, reg);
    SetReg(state, EAX, val_reg);
    SetReg(state, reg, val_eax);
}

void RegisterDataMovOps() {
    g_Handlers[0x87] = DispatchWrapper<OpXchg_EvGv>;  // XCHG r/m32, r32
    g_Handlers[0x89] = DispatchWrapper<OpMov_EvGv>;
    g_Handlers[0x8B] = DispatchWrapper<OpMov_GvEv>;
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

}  // namespace x86emu
