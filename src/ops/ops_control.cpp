// Control Flow
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpJmp_Rel(EmuState* state, DecodedOp* op) {
    // E9: JMP rel32, EB: JMP rel8
    int32_t offset;
    if (op->handler_index == 0xEB) {
        // 8-bit relative jump
        offset = (int32_t)(int8_t)(op->imm & 0xFF);
    } else {
        // 32-bit relative jump (E9)
        offset = (int32_t)op->imm;
    }
    state->ctx.eip += offset;
}

static FORCE_INLINE void OpJcc_Rel(EmuState* state, DecodedOp* op) {
    // 0F 8x: Jcc rel32, 7x: Jcc rel8
    uint8_t cond = op->handler_index & 0xF;

    if (CheckCondition(state, cond)) {
        // For 8-bit relative jumps (0x70-0x7F), need to sign-extend
        // For 32-bit relative jumps (0x180-0x18F), imm is already 32-bit
        int32_t offset;
        if (op->handler_index < 0x100) {
            // 8-bit relative jump (0x7x opcodes)
            offset = (int32_t)(int8_t)(op->imm & 0xFF);
        } else {
            // 32-bit relative jump (0F 8x opcodes)
            offset = (int32_t)op->imm;
        }
        state->ctx.eip += offset;
    }
    // If not taken, EIP is already at next insn (fallthrough).
}

static FORCE_INLINE void OpCall_Rel(EmuState* state, DecodedOp* op) {
    // E8: CALL rel32
    // Push Return Address (Current EIP is already advanced to Next Insn by Wrapper/Step)
    Push32(state, state->ctx.eip);
    // Jump relative to Next Insn
    state->ctx.eip += (int32_t)op->imm;
}

static FORCE_INLINE void OpRet(EmuState* state, DecodedOp* op) {
    // C3: RET
    uint32_t ret_eip = Pop32(state);
    state->ctx.eip = ret_eip;
}

static FORCE_INLINE void OpRet_Imm16(EmuState* state, DecodedOp* op) {
    // C2: RET imm16
    uint32_t ret_eip = Pop32(state);
    state->ctx.eip = ret_eip;

    // Pop imm16 bytes from stack
    uint32_t esp = GetReg(state, ESP);
    esp += (uint16_t)op->imm;
    SetReg(state, ESP, esp);
}

static FORCE_INLINE void OpInt(EmuState* state, DecodedOp* op) {
    // CD ib: INT imm8
    // Note: Decoder puts imm8 in op->imm
    uint8_t vector = (uint8_t)op->imm;
    if (!state->hooks.on_interrupt(state, vector)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = vector;
    }
}

static FORCE_INLINE void OpInt3(EmuState* state, DecodedOp* op) {
    // CC: INT3 (Vector 3, Breakpoint)
    if (!state->hooks.on_interrupt(state, 3)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 3;
    }
}

static FORCE_INLINE void OpInto(EmuState* state, DecodedOp* op) {
    // CE: INTO (Interrupt 4 if Overflow Flag set)
    if (state->ctx.eflags & OF_MASK) {
        if (!state->hooks.on_interrupt(state, 4)) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 4;  // #OF
        }
    }
}

static FORCE_INLINE void OpHlt(EmuState* state, DecodedOp* op) {
    // HLT (0xF4)
    state->status = EmuStatus::Stopped;
}

static FORCE_INLINE void OpNop(EmuState* state, DecodedOp* op) {
    // 90: NOP
    // F3 90: PAUSE (REP NOP)
    if (op->prefixes.flags.rep) {
        CPU_RELAX();
    }
}

static FORCE_INLINE void OpCmov_GvEv(EmuState* state, DecodedOp* op) {
    // 0F 4x: CMOVcc r32, r/m32
    uint8_t cond = op->handler_index & 0xF;
    bool pass = CheckCondition(state, cond);
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

// ------------------------------------------------------------------------------------------------
// Flag Operations
// ------------------------------------------------------------------------------------------------

static FORCE_INLINE void OpPushf(EmuState* state, DecodedOp* op) {
    // 9C: PUSHF/PUSHFD
    if (op->prefixes.flags.opsize) {
        // PUSHF (16-bit)
        Push16(state, (uint16_t)state->ctx.eflags);
    } else {
        // PUSHFD (32-bit)
        // Note: VM and RF flags are usually cleared in image pushed to stack?
        // For simple emulation, we push raw.
        Push32(state, state->ctx.eflags & 0x00FCFFFF);
    }
}

static FORCE_INLINE void OpPopf(EmuState* state, DecodedOp* op) {
    // 9D: POPF/POPFD
    if (op->prefixes.flags.opsize) {
        // POPF (16-bit)
        uint16_t val = Pop16(state);
        // Only update bits allowed by mask (and within 16-bit range)
        uint32_t mask = state->ctx.eflags_mask & 0xFFFF;
        // Also always preserve Reserved Bit 1 (Value 2)
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        state->ctx.eflags = new_flags;
    } else {
        // POPFD (32-bit)
        uint32_t val = Pop32(state);
        uint32_t mask = state->ctx.eflags_mask;
        uint32_t original = state->ctx.eflags;
        uint32_t new_flags = (original & ~mask) | (val & mask);
        new_flags |= 2;  // Reserved bit 1 is always 1
        // Reserved bits 3, 5, 15, 22..31 should strictly be preserved or zeroed depending on CPU
        // model. But respecting mask is sufficient for user mode emulation.
        state->ctx.eflags = new_flags;
    }
}

static FORCE_INLINE void OpStc(EmuState* state, DecodedOp* op) {
    // F9: STC
    state->ctx.eflags |= CF_MASK;
}

static FORCE_INLINE void OpClc(EmuState* state, DecodedOp* op) {
    // F8: CLC
    state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpCmc(EmuState* state, DecodedOp* op) {
    // F5: CMC (Complement Carry)
    state->ctx.eflags ^= CF_MASK;
}

static FORCE_INLINE void OpStd(EmuState* state, DecodedOp* op) {
    // FD: STD (Set Direction Flag)
    state->ctx.eflags |= 0x400;  // DF Mask
}

static FORCE_INLINE void OpCld(EmuState* state, DecodedOp* op) {
    // FC: CLD (Clear Direction Flag)
    state->ctx.eflags &= ~0x400;
}

static FORCE_INLINE void OpSti(EmuState* state, DecodedOp* op) {
    // FB: STI (Set Interrupt Flag)
    // Privileged Instruction. In User Mode (CPL=3, IOPL=0), this faults.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
}

static FORCE_INLINE void OpCli(EmuState* state, DecodedOp* op) {
    // FA: CLI (Clear Interrupt Flag)
    // Privileged Instruction.
    state->status = EmuStatus::Fault;
    state->fault_vector = 13;  // #GP
}

static FORCE_INLINE void OpCpuid(EmuState* state, DecodedOp* op) {
    // 0F A2: CPUID
    uint32_t leaf = GetReg(state, EAX);
    // uint32_t ecx_in = GetReg(state, ECX);

    uint32_t eax = 0, ebx = 0, ecx = 0, edx = 0;

    if (leaf == 0) {
        eax = 1;  // Max Leaf
        // "Genu" "ineI" "ntel"
        ebx = 0x756E6547;
        edx = 0x49656E69;
        ecx = 0x6C65746E;
    } else if (leaf == 1) {
        eax = 0x00000680;  // Pentium III approx
        ebx = 0;
        ecx = 0;
        edx = 0x00008000;  // Minimal features
        // Add SSE/SSE2 flags if needed:
        // EDX: Bit 25 (SSE), Bit 26 (SSE2)
        edx |= (1 << 25) | (1 << 26);
        // Bit 0 (FPU)
        edx |= 1;
        // Bit 4 (TSC)
        edx |= (1 << 4);
    }

    SetReg(state, EAX, eax);
    SetReg(state, EBX, ebx);
    SetReg(state, ECX, ecx);
    SetReg(state, EDX, edx);
}

static FORCE_INLINE void OpRdtsc(EmuState* state, DecodedOp* op) {
    // 0F 31: RDTSC
    // For now return 0 or a mock counter
    static uint64_t tsc = 0;
    tsc += 1000;

    uint32_t low = (uint32_t)tsc;
    uint32_t high = (uint32_t)(tsc >> 32);

    SetReg(state, EAX, low);
    SetReg(state, EDX, high);
}

static FORCE_INLINE void OpWait(EmuState* state, DecodedOp* op) {
    // 9B: WAIT/FWAIT n
    // Check pending FPU exceptions?
    // For now NOP.
}

void RegisterControlOps() {
    g_Handlers[0x90] = DispatchWrapper<OpNop>;
    g_Handlers[0x9B] = DispatchWrapper<OpWait>;
    g_Handlers[0xF4] = DispatchWrapper<OpHlt>;
    g_Handlers[0x9C] = DispatchWrapper<OpPushf>;
    g_Handlers[0x9D] = DispatchWrapper<OpPopf>;
    g_Handlers[0xE9] = DispatchWrapper<OpJmp_Rel>;    // JMP rel32
    g_Handlers[0xEB] = DispatchWrapper<OpJmp_Rel>;    // JMP rel8
    g_Handlers[0xE8] = DispatchWrapper<OpCall_Rel>;   // CALL rel32
    g_Handlers[0xC3] = DispatchWrapper<OpRet>;        // RET
    g_Handlers[0xC2] = DispatchWrapper<OpRet_Imm16>;  // RET imm16
    g_Handlers[0xCD] = DispatchWrapper<OpInt>;        // INT imm8
    g_Handlers[0xCC] = DispatchWrapper<OpInt3>;       // INT3
    g_Handlers[0xF5] = DispatchWrapper<OpCmc>;        // CMC
    g_Handlers[0xF8] = DispatchWrapper<OpClc>;        // CLC
    g_Handlers[0xF9] = DispatchWrapper<OpStc>;        // STC
    g_Handlers[0xFA] = DispatchWrapper<OpCli>;        // CLI
    g_Handlers[0xFB] = DispatchWrapper<OpSti>;        // STI
    g_Handlers[0xFC] = DispatchWrapper<OpCld>;        // CLD
    g_Handlers[0xFD] = DispatchWrapper<OpStd>;        // STD
    for (int i = 0; i < 16; ++i) {
        g_Handlers[0x70 + i] = DispatchWrapper<OpJcc_Rel>;   // Jcc rel8
        g_Handlers[0x180 + i] = DispatchWrapper<OpJcc_Rel>;  // Jcc rel32 (0F 8x)
    }
    g_Handlers[0xCE] = DispatchWrapper<OpInto>;
    g_Handlers[0x131] = DispatchWrapper<OpRdtsc>;  // 0F 31
    g_Handlers[0x1A2] = DispatchWrapper<OpCpuid>;  // 0F A2
    for (int i = 0; i < 16; ++i) {
        g_Handlers[0x140 + i] = DispatchWrapper<OpCmov_GvEv>;
    }
    g_Handlers[0x11F] = DispatchWrapper<OpNop>;  // Multi-byte NOP (0F 1F)
}

}  // namespace x86emu