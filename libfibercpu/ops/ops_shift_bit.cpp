// Shifts & Bit Operations
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

template <uint8_t FixedSubOp = 0xFF>
MemResult<void> Helper_Group2(EmuState* state, DecodedOp* op, uint32_t dest, uint8_t count, bool is_byte,
                              mem::MicroTLB* utlb) {
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }
    uint32_t res = dest;

    // Mask count
    if (count == 0) return {};  // Nothing

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
        case 6:  // SAL alias
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
            return {};
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
            return {};
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            return state->mmu.write<uint8_t>(state, addr, (uint8_t)res, utlb, op);
        }
    } else {
        if (op->prefixes.flags.opsize)
            return WriteModRM16(state, op, (uint16_t)res, utlb);
        else
            return WriteModRM32(state, op, res, utlb);
    }
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
static FORCE_INLINE void OpGroup2_EvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // C0: r/m8, imm8
    // C1: r/m32, imm8
    uint32_t dest;
    if constexpr (S == Specialized::ModReg) {
        // Optimization: Skip ReadModRM call for Reg
        uint8_t rm = op->modrm & 7;
        if constexpr (IsByte) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            if (rm < 4)
                dest = (*rptr) & 0xFF;
            else
                dest = ((*rptr) >> 8) & 0xFF;
        } else {
            if (op->prefixes.flags.opsize) {
                dest = GetReg(state, rm) & 0xFFFF;
            } else {
                dest = GetReg(state, rm);
            }
        }
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM8(state, op, utlb);
            if (!res) return;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                dest = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                dest = *res;
            }
        }
    }

    uint8_t count = (uint8_t)op->imm;
    if (!Helper_Group2<FixedSubOp>(state, op, dest, count, IsByte, utlb)) return;
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
static FORCE_INLINE void OpGroup2_Ev1(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // D0: Shift r/m8, 1
    // D1: Shift r/m16/32, 1
    uint32_t dest;
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        if constexpr (IsByte)
            dest = GetReg8(state, op->modrm & 7);
        else
            dest = GetReg(state, op->modrm & 7);
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM8(state, op, utlb);
            if (!res) return;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                dest = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                dest = *res;
            }
        }
    }
    if (!Helper_Group2<FixedSubOp>(state, op, dest, 1, IsByte, utlb)) return;
}

template <bool IsByte, uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
static FORCE_INLINE void OpGroup2_EvCl(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // D2: r/m8, CL
    // D3: r/m32, CL
    uint32_t dest;
    if constexpr (S == Specialized::ModReg) {
        uint8_t rm = op->modrm & 7;
        if constexpr (IsByte) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            if (rm < 4)
                dest = (*rptr) & 0xFF;
            else
                dest = ((*rptr) >> 8) & 0xFF;
        } else {
            if (op->prefixes.flags.opsize) {
                dest = GetReg(state, rm) & 0xFFFF;
            } else {
                dest = GetReg(state, rm);
            }
        }
    } else {
        if constexpr (IsByte) {
            auto res = ReadModRM8(state, op, utlb);
            if (!res) return;
            dest = *res;
        } else {
            if (op->prefixes.flags.opsize) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                dest = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                dest = *res;
            }
        }
    }

    uint8_t count = GetReg(state, ECX) & 0xFF;
    if (!Helper_Group2<FixedSubOp>(state, op, dest, count, IsByte, utlb)) return;
}

static FORCE_INLINE void OpBt_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F A3: BT r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    uint32_t bit_val = 0;

    if (mod == 3) {
        uint32_t base = GetReg(state, op->modrm & 7);
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        bit_val = (base >> (bit_idx & mask_val)) & 1;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;   // Bit index can offset the memory address
        uint8_t byte_idx = bit_idx & 7;  // Get bit within the byte
        auto val_res = state->mmu.read<uint8_t>(state, addr, utlb, op);
        if (!val_res) return;  // Abort on memory fault
        bit_val = (*val_res >> byte_idx) & 1;
    }

    if (bit_val)
        state->ctx.eflags |= CF_MASK;
    else
        state->ctx.eflags &= ~CF_MASK;
}

static FORCE_INLINE void OpGroup8_EvIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BA /4: BT  r/m16/32, imm8
    // 0F BA /5: BTS r/m16/32, imm8
    // 0F BA /6: BTR r/m16/32, imm8
    // 0F BA /7: BTC r/m16/32, imm8

    uint8_t subop = (op->modrm >> 3) & 7;
    // Safety check: Decoder should route handling here only for Group8 (BA)

    bool opsize = op->prefixes.flags.opsize;
    uint8_t offset = op->imm & (opsize ? 15 : 31);

    bool is_mem = ((op->modrm >> 6) & 3) != 3;
    uint32_t base = 0;
    uint32_t addr = 0;

    if (is_mem) {
        addr = ComputeLinearAddress(state, op);
        MemResult<uint32_t> read_res;
        if (opsize) {
            read_res = state->mmu.read<uint16_t>(state, addr, utlb, op);
        } else {
            read_res = state->mmu.read<uint32_t>(state, addr, utlb, op);
        }
        if (!read_res) return;  // Abort on memory fault
        base = *read_res;
    } else {
        if (opsize) {
            base = GetReg(state, op->modrm & 7) & 0xFFFF;
        } else {
            base = GetReg(state, op->modrm & 7);
        }
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
            if (opsize) {
                if (!state->mmu.write<uint16_t>(state, addr, (uint16_t)res, utlb, op)) return;
            } else {
                if (!state->mmu.write<uint32_t>(state, addr, res, utlb, op)) return;
            }
        } else {
            if (opsize)
                SetReg(state, op->modrm & 7, (GetReg(state, op->modrm & 7) & 0xFFFF0000) | (uint16_t)res);
            else
                SetReg(state, op->modrm & 7, res);
        }
    }
    // Subop 4 (BT) falls through here (no writeback), which is correct.
}

static FORCE_INLINE void OpBtr_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F B3: BTR r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        *rptr = base & ~(1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        auto byte_res = state->mmu.read<uint8_t>(state, addr, utlb, op);
        if (!byte_res) return;  // Abort on memory fault
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        if (!state->mmu.write<uint8_t>(state, addr, byte & ~(1 << byte_idx), utlb, op))
            return;  // Abort on memory fault
    }
}

static FORCE_INLINE void OpBts_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F AB: BTS r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        *rptr = base | (1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        auto byte_res = state->mmu.read<uint8_t>(state, addr, utlb, op);
        if (!byte_res) return;  // Abort on memory fault
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        if (!state->mmu.write<uint8_t>(state, addr, byte | (1 << byte_idx), utlb, op)) return;  // Abort on memory fault
    }
}

static FORCE_INLINE void OpBtc_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BB: BTC r/m16/32, r16/32
    uint32_t bit_idx = GetReg(state, (op->modrm >> 3) & 7);
    uint8_t mod = (op->modrm >> 6) & 3;
    if (mod == 3) {
        uint8_t rm = op->modrm & 7;
        uint32_t* rptr = GetRegPtr(state, rm);
        uint32_t base = *rptr;
        uint32_t mask_val = op->prefixes.flags.opsize ? 15 : 31;
        uint32_t bit_to_test = bit_idx & mask_val;

        if ((base >> bit_to_test) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        *rptr = base ^ (1 << bit_to_test);
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        addr += (int32_t)bit_idx >> 3;
        uint8_t byte_idx = bit_idx & 7;
        auto byte_res = state->mmu.read<uint8_t>(state, addr, utlb, op);
        if (!byte_res) return;  // Abort on memory fault
        uint8_t byte = *byte_res;

        if ((byte >> byte_idx) & 1)
            state->ctx.eflags |= CF_MASK;
        else
            state->ctx.eflags &= ~CF_MASK;
        if (!state->mmu.write<uint8_t>(state, addr, byte ^ (1 << byte_idx), utlb, op)) return;  // Abort on memory fault
    }
}

static FORCE_INLINE void OpBsr_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BD: BSR r16/32, r/m16/32
    auto val_res = ReadModRM32(state, op, utlb);
    if (!val_res) return;
    uint32_t val = *val_res;

    uint8_t reg = (op->modrm >> 3) & 7;
    if (val == 0) {
        state->ctx.eflags |= ZF_MASK;
    } else {
        state->ctx.eflags &= ~ZF_MASK;
        int count = 31;
        while (((val >> count) & 1) == 0) count--;
        SetReg(state, reg, count);
    }
}

static FORCE_INLINE void OpBsf_Tzcnt_GvEv(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F BC: BSF r32, r/m32
    // F3 0F BC: TZCNT r32, r/m32

    auto src_res = ReadModRM32(state, op, utlb);
    if (!src_res) return;
    uint32_t src = *src_res;
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

static FORCE_INLINE void OpBswap_Reg(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 0F C8+rd: BSWAP r32
    // DecodeInstruction puts rd in op->modrm for non-ModRM ops
    uint8_t reg = op->modrm & 7;
    uint32_t val = GetReg(state, reg);
    uint32_t res = __builtin_bswap32(val);
    SetReg(state, reg, res);
}

void RegisterShiftBitOps() {
    g_Handlers[0xC0] = DispatchWrapper<OpGroup2_EvIb<true>>;
    g_Handlers[0xC1] = DispatchWrapper<OpGroup2_EvIb<false>>;
    g_Handlers[0xD0] = DispatchWrapper<OpGroup2_Ev1<true>>;
    g_Handlers[0xD1] = DispatchWrapper<OpGroup2_Ev1<false>>;
    g_Handlers[0xD2] = DispatchWrapper<OpGroup2_EvCl<true>>;
    g_Handlers[0xD3] = DispatchWrapper<OpGroup2_EvCl<false>>;

    // Specializations
    // Explicit registration for common cases to ensure they are generated

    // SHL (4)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 4;
        DispatchRegistrar<OpGroup2_EvIb<false, 4>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 4>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 4>>::RegisterSpecialized(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb<false, 4, Specialized::ModReg>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 4, Specialized::ModReg>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 4, Specialized::ModReg>>::RegisterSpecialized(0xD3, c);
    }
    // SHR (5)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 5;
        DispatchRegistrar<OpGroup2_EvIb<false, 5>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 5>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 5>>::RegisterSpecialized(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb<false, 5, Specialized::ModReg>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 5, Specialized::ModReg>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 5, Specialized::ModReg>>::RegisterSpecialized(0xD3, c);
    }
    // SAR (7)
    {
        SpecCriteria c;
        c.reg_mask = 7;
        c.reg_val = 7;
        DispatchRegistrar<OpGroup2_EvIb<false, 7>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 7>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 7>>::RegisterSpecialized(0xD3, c);

        c.mod_mask = 0xC0;
        c.mod_val = 0xC0;
        DispatchRegistrar<OpGroup2_EvIb<false, 7, Specialized::ModReg>>::RegisterSpecialized(0xC1, c);
        DispatchRegistrar<OpGroup2_Ev1<false, 7, Specialized::ModReg>>::RegisterSpecialized(0xD1, c);
        DispatchRegistrar<OpGroup2_EvCl<false, 7, Specialized::ModReg>>::RegisterSpecialized(0xD3, c);
    }

    g_Handlers[0x1A3] = DispatchWrapper<OpBt_Reg>;
    g_Handlers[0x1AB] = DispatchWrapper<OpBts_Reg>;  // 0F AB
    g_Handlers[0x1B3] = DispatchWrapper<OpBtr_Reg>;
    g_Handlers[0x1BB] = DispatchWrapper<OpBtc_Reg>;      // 0F BB
    g_Handlers[0x1BA] = DispatchWrapper<OpGroup8_EvIb>;  // 0F BA (All subops)
    g_Handlers[0x1BD] = DispatchWrapper<OpBsr_GvEv>;
    g_Handlers[0x1BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;  // 0F BC: BSF
    g_Handlers[0x2BC] = DispatchWrapper<OpBsf_Tzcnt_GvEv>;
    for (int i = 0; i < 8; ++i) {
        g_Handlers[0x1C8 + i] = DispatchWrapper<OpBswap_Reg>;
    }
}

}  // namespace fiberish