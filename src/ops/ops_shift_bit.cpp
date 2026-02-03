// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

void Helper_Group2(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count, bool is_byte) {
    uint8_t subop = (op->modrm >> 3) & 7;
    // printf("[Group2] Sub=%d Dest=%08X Count=%d Byte=%d\n", subop, dest, count, is_byte);
    uint32_t res = dest;

    // Mask count
    if (count == 0) return;  // Nothing

    // Perform Op
    bool is_opsize = op->prefixes.flags.opsize;

    switch (subop) {
        case 0:  // ROL
            if (is_byte)
                res = AluRol<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRol<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluRol<uint32_t>(state, dest, count);
            break;
        case 1:  // ROR
            if (is_byte)
                res = AluRor<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRor<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluRor<uint32_t>(state, dest, count);
            break;
        case 2:  // RCL
            if (is_byte)
                res = AluRcl<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRcl<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluRcl<uint32_t>(state, dest, count);
            break;
        case 3:  // RCR
            if (is_byte)
                res = AluRcr<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluRcr<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluRcr<uint32_t>(state, dest, count);
            break;
        case 4:  // SHL/SAL
            if (is_byte)
                res = AluShl<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluShl<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluShl<uint32_t>(state, dest, count);
            break;
        case 5:  // SHR
            if (is_byte)
                res = AluShr<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluShr<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluShr<uint32_t>(state, dest, count);
            break;
        case 7:  // SAR
            if (is_byte)
                res = AluSar<uint8_t>(state, (uint8_t)dest, count);
            else if (is_opsize)
                res = AluSar<uint16_t>(state, (uint16_t)dest, count);
            else
                res = AluSar<uint32_t>(state, dest, count);
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
    } else if (is_opsize) {
        WriteModRM16(state, op, (uint16_t)res);
    } else {
        WriteModRM32(state, op, res);
    }
}

static FORCE_INLINE void OpGroup2_EvIb(EmuState* state, DecodedOp* op) {
    // C0: r/m8, imm8
    // C1: r/m32, imm8
    bool is_byte = (op->handler_index == 0xC0);
    uint32_t dest = ReadModRM(state, op, is_byte);
    uint8_t count = (uint8_t)op->imm;
    Helper_Group2(state, op, dest, count, is_byte);
}

static FORCE_INLINE void OpGroup2_Ev1(EmuState* state, DecodedOp* op) {
    // D0: r/m8, 1
    // D1: r/m32, 1
    bool is_byte = (op->handler_index == 0xD0);
    uint32_t dest = ReadModRM(state, op, is_byte);
    Helper_Group2(state, op, dest, 1, is_byte);
}

static FORCE_INLINE void OpGroup2_EvCl(EmuState* state, DecodedOp* op) {
    // D2: r/m8, CL
    // D3: r/m32, CL
    bool is_byte = (op->handler_index == 0xD2);
    uint32_t dest = ReadModRM(state, op, is_byte);
    uint8_t count = GetReg(state, ECX) & 0xFF;
    Helper_Group2(state, op, dest, count, is_byte);
}

static FORCE_INLINE void OpBt_EvGv(EmuState* state, DecodedOp* op) {
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

    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpGroup8_EvIb(EmuState* state, DecodedOp* op) {
    // 0F BA /4: BT  r/m32, imm8
    // 0F BA /5: BTS r/m32, imm8
    // 0F BA /6: BTR r/m32, imm8
    // 0F BA /7: BTC r/m32, imm8

    uint8_t subop = (op->modrm >> 3) & 7;
    uint8_t offset = op->imm & 31;  // imm8 modulo 32

    bool is_mem = ((op->modrm >> 6) & 3) != 3;
    uint32_t base = 0;
    uint32_t addr = 0;

    if (is_mem) {
        // Memory Operand
        // For Immediate form, the bit index is within the ModRM operand (16 or 32 bits).
        // It DOES NOT offset the address like r/m, reg form does.
        // It operates on the word/dword at effective address.
        addr = ComputeEAD(state, op);
        base = state->mmu.read<uint32_t>(addr);  // Always 32-bit in our emu for now (or opsize)
        // If 16-bit opsize, we should read 16. Assuming 32 for simplicity or check opsize.
        if (op->prefixes.flags.opsize) base &= 0xFFFF;
    } else {
        // Register Operand
        base = ReadModRM32(state, op);
    }

    uint8_t bit_val = (base >> offset) & 1;

    // Update CF
    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;

    // Write Back if needed
    if (subop >= 5 && subop <= 7) {
        uint32_t res = base;
        uint32_t mask = (1 << offset);

        if (subop == 5)
            res |= mask;  // BTS
        else if (subop == 6)
            res &= ~mask;  // BTR
        else if (subop == 7)
            res ^= mask;  // BTC

        if (is_mem) {
            state->mmu.write<uint32_t>(addr, res);
        } else {
            WriteModRM32(state, op, res);
        }
    } else if (subop != 4) {
        OpUd2(state, op);
    }
}

static FORCE_INLINE void OpBtr_EvGv(EmuState* state, DecodedOp* op) {
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

    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpBts_EvGv(EmuState* state, DecodedOp* op) {
    // 0F AB: BTS r/m32, r32
    uint32_t offset = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    uint8_t bit_val = 0;
    if (mod == 3) {
        uint32_t base = GetReg(state, rm);
        uint32_t mask = 1 << (offset & 31);
        bit_val = (base & mask) ? 1 : 0;
        SetReg(state, rm, base | mask);
    } else {
        uint32_t addr = ComputeEAD(state, op);
        int32_t signed_offset = (int32_t)offset;
        addr += (signed_offset >> 3);
        uint8_t bit_idx = signed_offset & 7;

        uint8_t byte = state->mmu.read<uint8_t>(addr);
        bit_val = (byte >> bit_idx) & 1;
        state->mmu.write<uint8_t>(addr, byte | (1 << bit_idx));
    }

    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpBtc_EvGv(EmuState* state, DecodedOp* op) {
    // 0F BB: BTC r/m32, r32
    uint32_t offset = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint8_t rm = op->modrm & 7;

    uint8_t bit_val = 0;
    if (mod == 3) {
        uint32_t base = GetReg(state, rm);
        uint32_t mask = 1 << (offset & 31);
        bit_val = (base & mask) ? 1 : 0;
        SetReg(state, rm, base ^ mask);
    } else {
        uint32_t addr = ComputeEAD(state, op);
        int32_t signed_offset = (int32_t)offset;
        addr += (signed_offset >> 3);
        uint8_t bit_idx = signed_offset & 7;

        uint8_t byte = state->mmu.read<uint8_t>(addr);
        bit_val = (byte >> bit_idx) & 1;
        state->mmu.write<uint8_t>(addr, byte ^ (1 << bit_idx));
    }

    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpBsr_GvEv(EmuState* state, DecodedOp* op) {
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

static FORCE_INLINE void OpBsf_Tzcnt_GvEv(EmuState* state, DecodedOp* op) {
    // 0F BC: BSF r32, r/m32
    // F3 0F BC: TZCNT r32, r/m32

    uint32_t src = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.rep) {
        // TZCNT (F3 Prefix)
        if (src == 0) {
            state->ctx.eflags |= CF_MASK;
            state->ctx.eflags &= ~ZF_MASK;
            SetReg(state, reg, 32);  // Operand Size
        } else {
            state->ctx.eflags &= ~(CF_MASK | ZF_MASK);  // CF cleared
            // __builtin_ctz is undefined for 0, but we checked src==0
            int count = __builtin_ctz(src);
            SetReg(state, reg, count);
            if (count == 0) state->ctx.eflags |= ZF_MASK;
        }
    } else {
        // BSF (No F3 Prefix)
        if (src == 0) {
            state->ctx.eflags |= ZF_MASK;
            // Dest undefined.
        } else {
            state->ctx.eflags &= ~ZF_MASK;
            int count = __builtin_ctz(src);
            SetReg(state, reg, count);
            // Flags: ZF cleared (done above).
            // CF, OF, SF, AF, PF undefined.
        }
    }
}

static FORCE_INLINE void OpBswap_Reg(EmuState* state, DecodedOp* op) {
    // 0F C8+rd: BSWAP r32
    uint8_t reg = op->handler_index & 7;
    uint32_t val = GetReg(state, reg);
    uint32_t res = __builtin_bswap32(val);
    SetReg(state, reg, res);
}

void RegisterShiftBitOps() {
    g_Handlers[0xC0] = DispatchWrapper<OpGroup2_EvIb>;
    g_Handlers[0xC1] = DispatchWrapper<OpGroup2_EvIb>;
    g_Handlers[0xD0] = DispatchWrapper<OpGroup2_Ev1>;
    g_Handlers[0xD1] = DispatchWrapper<OpGroup2_Ev1>;
    g_Handlers[0xD2] = DispatchWrapper<OpGroup2_EvCl>;
    g_Handlers[0xD3] = DispatchWrapper<OpGroup2_EvCl>;
    g_Handlers[0x1A3] = DispatchWrapper<OpBt_EvGv>;
    g_Handlers[0x1AB] = DispatchWrapper<OpBts_EvGv>;  // 0F AB
    g_Handlers[0x1B3] = DispatchWrapper<OpBtr_EvGv>;
    g_Handlers[0x1BB] = DispatchWrapper<OpBtc_EvGv>;  // 0F BB
    g_Handlers[0x1BA] = DispatchWrapper<OpGroup8_EvIb>;
    g_Handlers[0x1BD] = DispatchWrapper<OpBsr_GvEv>;
    g_Handlers[0x1BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;  // 0F BC: BSF
    g_Handlers[0x2BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x1C8 + i] = DispatchWrapper<OpBswap_Reg>;
    }
}

}  // namespace x86emu