// Comparison & Test
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpCmp_EbGb(EmuState* state, DecodedOp* op) {
    // 38: CMP r/m8, r8
    uint8_t dest = ReadModRM8(state, op);
    uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);
    AluSub(state, dest, src);
}

static FORCE_INLINE void OpCmp_EvGv(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpCmp_GbEb(EmuState* state, DecodedOp* op) {
    // 3A: CMP r8, r/m8
    uint8_t dest = GetReg8(state, (op->modrm >> 3) & 7);
    uint8_t src = ReadModRM8(state, op);
    AluSub(state, dest, src);
}

static FORCE_INLINE void OpCmp_GvEv(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpTest_EvGv(EmuState* state, DecodedOp* op) {
    // 85: TEST r/m16/32, r16/32
    uint8_t reg = (op->modrm >> 3) & 7;
    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op);
        uint16_t src = (uint16_t)GetReg(state, reg);
        AluAnd<uint16_t>(state, dest, src);
    } else {
        uint32_t dest = ReadModRM32(state, op);
        uint32_t src = GetReg(state, reg);
        AluAnd<uint32_t>(state, dest, src);
    }
}

static FORCE_INLINE void OpSetcc(EmuState* state, DecodedOp* op) {
    // 0F 9x: SETcc r/m8
    uint8_t cond = op->extra;
    uint8_t val = CheckCondition(state, cond) ? 1 : 0;
    WriteModRM8(state, op, val);
}

static FORCE_INLINE void OpCmpxchg(EmuState* state, DecodedOp* op) {
    // 0F B0/B1: CMPXCHG r/m, r
    bool is_byte = (op->extra == 0);

    if (is_byte) {
        uint8_t acc = GetReg(state, EAX) & 0xFF;
        uint8_t src = GetReg8(state, (op->modrm >> 3) & 7);  // Reg
        uint8_t dest = ReadModRM8(state, op);                // Mem/Reg

        // Temp = Dest - Acc
        AluSub<uint8_t>(state, acc, dest);  // Sets flags based on ACC - DEST?
        // Wait: CMPXCHG: Compare Accumulator with Dest.
        // If Equal (ZF=1): Dest <- Src
        // Else: Accumulator <- Dest
        // Comparison is Dest - Acc? Or Acc - Dest?
        // CMP instruction does: Op1 - Op2.
        // CMPXCHG compares AL/AX/EAX with Dest.
        // "Compares the value in the AL, AX, or EAX register with the first operand
        // (destination operand). If the two values are equal, the second operand
        // (source operand) is loaded into the destination operand. Otherwise, the
        // destination operand is loaded into the AL, AX, or EAX register." Flag
        // setting is like CMP: Acc - Dest or Dest - Acc? CMP Dest, Src -> Dest
        // - Src. Here Dest is memory. Acc is implied source. Actually: "Compares
        // ... EAX with ... Destination". Usually CMP A, B is A - B. So EAX - Dest.

        AluSub<uint8_t>(state, acc, dest);

        if (state->ctx.eflags & ZF_MASK) {
            WriteModRM8(state, op, src);
        } else {
            // Load Dest into AL
            uint32_t val = GetReg(state, EAX);
            val = (val & 0xFFFFFF00) | dest;
            SetReg(state, EAX, val);
        }

    } else {
        uint32_t acc = GetReg(state, EAX);
        // Check OpSize?
        if (op->prefixes.flags.opsize) {
            uint16_t acc16 = (uint16_t)acc;
            uint16_t src = (uint16_t)GetReg(state, (op->modrm >> 3) & 7);
            uint16_t dest = ReadModRM16(state, op);

            AluSub<uint16_t>(state, acc16, dest);

            if (state->ctx.eflags & ZF_MASK) {
                WriteModRM16(state, op, src);
            } else {
                SetReg(state, EAX, (acc & 0xFFFF0000) | dest);
            }
        } else {
            uint32_t src = GetReg(state, (op->modrm >> 3) & 7);
            uint32_t dest = ReadModRM32(state, op);

            AluSub<uint32_t>(state, acc, dest);

            if (state->ctx.eflags & ZF_MASK) {
                WriteModRM32(state, op, src);
            } else {
                SetReg(state, EAX, dest);
            }
        }
    }
}

void RegisterCompareOps() {
    g_Handlers[0x38] = DispatchWrapper<OpCmp_EbGb>;
    g_Handlers[0x39] = DispatchWrapper<OpCmp_EvGv>;
    g_Handlers[0x3A] = DispatchWrapper<OpCmp_GbEb>;
    g_Handlers[0x3B] = DispatchWrapper<OpCmp_GvEv>;
    g_Handlers[0x85] = DispatchWrapper<OpTest_EvGv>;
    g_Handlers[0x1B0] = DispatchWrapper<OpCmpxchg>;  // 0F B0
    g_Handlers[0x1B1] = DispatchWrapper<OpCmpxchg>;  // 0F B1

    for (int i = 0; i < 16; ++i) {
        g_Handlers[0x190 + i] = DispatchWrapper<OpSetcc>;  // SETcc (0F 9x)
    }
}

}  // namespace x86emu