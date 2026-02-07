// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../ops_helpers_template.h"
#include "../state.h"

namespace x86emu {

template <uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
static FORCE_INLINE void OpGroup1_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 80: Arith r/m8, imm8
    uint8_t dest = ReadModRM8(state, op, utlb);
    uint8_t src = (uint8_t)op->imm;
    Helper_Group1<uint8_t, FixedSubOp>(state, op, dest, src, utlb);
}

// Named wrappers for Group 1 EbIb specializations
static void OpGroup1_Add_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<0>(s, o, u); }
static void OpGroup1_Or_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u)  { OpGroup1_EbIb<1>(s, o, u); }
static void OpGroup1_Adc_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<2>(s, o, u); }
static void OpGroup1_Sbb_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<3>(s, o, u); }
static void OpGroup1_And_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<4>(s, o, u); }
static void OpGroup1_Sub_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<5>(s, o, u); }
static void OpGroup1_Xor_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<6>(s, o, u); }
static void OpGroup1_Cmp_EbIb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EbIb<7>(s, o, u); }

template <uint8_t FixedSubOp = 0xFF, Specialized S = Specialized::None>
static FORCE_INLINE void OpGroup1_EvIz(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 81: Arith r/m32, imm32
    // 83: Arith r/m32, imm8 (sign-extended)

    if (op->prefixes.flags.opsize) {
        uint16_t dest = ReadModRM16(state, op, utlb);
        uint16_t src = (uint16_t)op->imm;
        if (op->extra == 0x3) {  // 0x83 & 0xF == 3
            src = (int16_t)(int8_t)src;
        }
        Helper_Group1<uint16_t, FixedSubOp>(state, op, dest, src, utlb);
    } else {
        uint32_t dest = ReadModRM32(state, op, utlb);
        uint32_t src = op->imm;
        if (op->extra == 0x3) {  // 0x83 & 0xF == 3
            src = (int32_t)(int8_t)src;
        }
        Helper_Group1<uint32_t, FixedSubOp>(state, op, dest, src, utlb);
    }
}

// Named wrappers for Group 1 specializations
static void OpGroup1_Add_EvIz(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<0>(s, o, u); }
static void OpGroup1_Sub_EvIz(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<5>(s, o, u); }
static void OpGroup1_Cmp_EvIz(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<7>(s, o, u); }

static void OpGroup1_Add_EvIz_ModReg(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<0, Specialized::ModReg>(s, o, u); }
static void OpGroup1_Sub_EvIz_ModReg(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<5, Specialized::ModReg>(s, o, u); }
static void OpGroup1_Cmp_EvIz_ModReg(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup1_EvIz<7, Specialized::ModReg>(s, o, u); }

template <uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup3_Ev(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F6 (Byte) or F7 (Dword)
    bool is_byte = (op->extra == 0x6);  // 0xF6 & 0xF == 6
    if (is_byte) {
        uint8_t val = ReadModRM8(state, op, utlb);
        Helper_Group3<uint8_t, FixedSubOp>(state, op, val, utlb);
    } else {
        if (op->prefixes.flags.opsize) {
            uint16_t val = ReadModRM16(state, op, utlb);
            Helper_Group3<uint16_t, FixedSubOp>(state, op, val, utlb);
        } else {
            uint32_t val = ReadModRM32(state, op, utlb);
            Helper_Group3<uint32_t, FixedSubOp>(state, op, val, utlb);
        }
    }
}

// Named wrappers for Group 3 specializations (Profiling visibility)
static void OpGroup3_Test_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup3_Ev<0>(s, o, u); }
static void OpGroup3_Not_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u)  { OpGroup3_Ev<2>(s, o, u); }
static void OpGroup3_Neg_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u)  { OpGroup3_Ev<3>(s, o, u); }
static void OpGroup3_Mul_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u)  { OpGroup3_Ev<4>(s, o, u); }
static void OpGroup3_Imul_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup3_Ev<5>(s, o, u); }
static void OpGroup3_Div_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u)  { OpGroup3_Ev<6>(s, o, u); }
static void OpGroup3_Idiv_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup3_Ev<7>(s, o, u); }

template <uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup4_Eb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FE: Group 4 (Byte)
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }
    uint8_t val = ReadModRM8(state, op, utlb);
    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    switch (subop) {
        case 0:  // INC
        {
            uint8_t res = AluAdd(state, val, (uint8_t)1);
            state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res, utlb);
            break;
        }
        case 1:  // DEC
        {
            uint8_t res = AluSub(state, val, (uint8_t)1);
            state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res, utlb);
            break;
        }
        default:
            OpUd2(state, op);
    }
}

// Named wrappers for Group 4 specializations
static void OpGroup4_Inc_Eb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup4_Eb<0>(s, o, u); }
static void OpGroup4_Dec_Eb(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup4_Eb<1>(s, o, u); }

template <uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup5_Ev(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FF: Group 5
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    switch (subop) {
        case 0:  // INC Ev
            if (op->prefixes.flags.opsize) {
                uint16_t val = ReadModRM16(state, op, utlb);
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint16_t res = AluAdd<uint16_t>(state, val, 1);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM16(state, op, res, utlb);
            } else {
                uint32_t val = ReadModRM32(state, op, utlb);
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint32_t res = AluAdd<uint32_t>(state, val, 1U);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM32(state, op, res, utlb);
            }
            break;
        case 1:  // DEC Ev
            if (op->prefixes.flags.opsize) {
                uint16_t val = ReadModRM16(state, op, utlb);
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint16_t res = AluSub<uint16_t>(state, val, 1);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM16(state, op, res, utlb);
            } else {
                uint32_t val = ReadModRM32(state, op, utlb);
                uint32_t old_cf = state->ctx.eflags & CF_MASK;
                uint32_t res = AluSub<uint32_t>(state, val, 1U);
                state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
                WriteModRM32(state, op, res, utlb);
            }
            break;
        case 2:  // CALL Ev (Near Indirect)
        {
            uint32_t target;
            if (op->prefixes.flags.opsize) {
                target = ReadModRM16(state, op, utlb);
                Push16(state, (uint16_t)state->ctx.eip, utlb);
                state->ctx.eip = target & 0xFFFF;
            } else {
                target = ReadModRM32(state, op, utlb);
                Push32(state, state->ctx.eip, utlb);
                state->ctx.eip = target;
            }
            break;
        }
        case 4:  // JMP Ev (Near Indirect)
        {
            uint32_t target;
            if (op->prefixes.flags.opsize) {
                target = ReadModRM16(state, op, utlb);
                state->ctx.eip = target & 0xFFFF;
            } else {
                target = ReadModRM32(state, op, utlb);
                state->ctx.eip = target;
            }
            break;
        }
        case 6:  // PUSH Ev
            if (op->prefixes.flags.opsize) {
                uint16_t val = ReadModRM16(state, op, utlb);
                Push16(state, val, utlb);
            } else {
                uint32_t val = ReadModRM32(state, op, utlb);
                Push32(state, val, utlb);
            }
            break;
        default:
            OpUd2(state, op);
            break;
    }
}

static void OpGroup5_Inc_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup5_Ev<0>(s, o, u); }
static void OpGroup5_Dec_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup5_Ev<1>(s, o, u); }
static void OpGroup5_Call_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup5_Ev<2>(s, o, u); }
static void OpGroup5_Jmp_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup5_Ev<4>(s, o, u); }
static void OpGroup5_Push_Ev(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup5_Ev<6>(s, o, u); }

static FORCE_INLINE void OpGroup9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t sub = (op->modrm >> 3) & 7;
    if (sub == 1) {
        // CMPXCHG8B m64
        // Compare EDX:EAX with m64.
        // If equal, ZF=1 and m64 = ECX:EBX.
        // Else, ZF=0 and EDX:EAX = m64.

        uint32_t addr = ComputeLinearAddress(state, op);
        uint64_t mem_val = state->mmu.read<uint64_t>(addr, utlb);

        uint32_t eax = GetReg(state, EAX);
        uint32_t edx = GetReg(state, EDX);
        uint64_t edx_eax = ((uint64_t)edx << 32) | eax;

        if (mem_val == edx_eax) {
            state->ctx.eflags |= ZF_MASK;

            uint32_t ebx = GetReg(state, EBX);
            uint32_t ecx = GetReg(state, ECX);
            uint64_t ecx_ebx = ((uint64_t)ecx << 32) | ebx;

            state->mmu.write<uint64_t>(addr, ecx_ebx, utlb);
        } else {
            state->ctx.eflags &= ~ZF_MASK;

            SetReg(state, EAX, (uint32_t)mem_val);
            SetReg(state, EDX, (uint32_t)(mem_val >> 32));
        }
    } else {
        OpUd2(state, op);
    }
}

static FORCE_INLINE void OpXadd_Rm_R(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // XADD r/m, r: Exchange and Add

    uint32_t width = 4;
    if (op->extra == 0x0) {  // 0F C0 & 0xF == 0 -> Byte
        width = 1;
    } else {  // 0F C1
        if (op->prefixes.flags.opsize)
            width = 2;
        else
            width = 4;
    }

    // ---------------------------------------------------------
    // 1. Read Dest (E: R/M)
    // ---------------------------------------------------------
    uint32_t dest_val = 0;
    if (width == 1)
        dest_val = ReadModRM8(state, op, utlb);
    else if (width == 2)
        dest_val = ReadModRM16(state, op, utlb);
    else
        dest_val = ReadModRM32(state, op, utlb);

    // ---------------------------------------------------------
    // 2. Read Src (G: Reg)
    // ---------------------------------------------------------
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src_val = 0;
    if (width == 1)
        src_val = GetReg8(state, reg);
    else if (width == 2)
        src_val = GetReg(state, reg) & 0xFFFF;
    else
        src_val = GetReg(state, reg);

    // ---------------------------------------------------------
    // 3. ALU Add
    // ---------------------------------------------------------
    uint32_t res = 0;
    if (width == 1)
        res = AluAdd(state, (uint8_t)dest_val, (uint8_t)src_val);
    else if (width == 2)
        res = AluAdd(state, (uint16_t)dest_val, (uint16_t)src_val);
    else
        res = AluAdd(state, (uint32_t)dest_val, (uint32_t)src_val);

    // ---------------------------------------------------------
    // 4. Write Old Dest to Src (Reg)
    // ---------------------------------------------------------
    if (width == 1) {
        uint32_t* rptr = GetRegPtr(state, reg & 3);
        uint32_t curr = *rptr;
        if (reg < 4)
            curr = (curr & 0xFFFFFF00) | (dest_val & 0xFF);
        else
            curr = (curr & 0xFFFF00FF) | ((dest_val & 0xFF) << 8);
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
    // Let's rely on manual write using ComputeLinearAddress if memory.

    if (op->modrm >= 0xC0) {
        // Register Check
        uint8_t rm = op->modrm & 7;
        if (width == 1) {
            uint32_t* rptr = GetRegPtr(state, rm & 3);
            uint32_t curr = *rptr;
            if (rm < 4)
                curr = (curr & 0xFFFFFF00) | (res & 0xFF);
            else
                curr = (curr & 0xFFFF00FF) | ((res & 0xFF) << 8);
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
        uint32_t addr = ComputeLinearAddress(state, op);
        if (width == 1)
            state->mmu.write<uint8_t>(addr, (uint8_t)res, utlb);
        else if (width == 2)
            state->mmu.write<uint16_t>(addr, (uint16_t)res, utlb);
        else
            state->mmu.write<uint32_t>(addr, res, utlb);
    }
}

static FORCE_INLINE void OpCdq(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 99: CDQ
    uint32_t eax = GetReg(state, EAX);
    uint32_t edx = ((int32_t)eax < 0) ? 0xFFFFFFFF : 0;
    SetReg(state, EDX, edx);
}

static FORCE_INLINE void OpCwde(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 98: CBW (AL->AX) / CWDE (AX->EAX)
    if (op->prefixes.flags.opsize) {
        // CBW: MOVSX AX, AL
        int8_t val = (int8_t)GetReg(state, EAX);
        uint32_t current = GetReg(state, EAX);
        // Preserve high 16 bits
        uint32_t res = (current & 0xFFFF0000) | (uint16_t)(int16_t)val;
        SetReg(state, EAX, res);
    } else {
        // CWDE: MOVSX EAX, AX
        int16_t val = (int16_t)GetReg(state, EAX);
        SetReg(state, EAX, (uint32_t)(int32_t)val);
    }
}

// Exposed for other modules
void OpUd2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // #UD is a Fault.
    // DispatchWrapper will handle EIP rewind if generic Fault status is set.

    if (!state->hooks.on_invalid_opcode(state)) {
        state->status = EmuStatus::Fault;
        state->fault_vector = 6;  // #UD
    }
}

void OpUd2(EmuState* state, DecodedOp* op) {
    OpUd2(state, op, nullptr);
}

void RegisterGroupOps() {
    g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz<>>;
    g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIz<>>;
    g_Handlers[0x80] = DispatchWrapper<OpGroup1_EbIb<>>;

#define REGISTER_GROUP_SPEC(opcode, subop, func) \
    { \
        SpecCriteria c; c.reg_mask = 0x7; c.reg_val = subop; \
        DispatchRegistrar<func>::RegisterSpecialized(opcode, c); \
    }

#define REGISTER_GROUP_SPEC_MODREG(opcode, subop, func) \
    { \
        SpecCriteria c; c.reg_mask = 0x7; c.reg_val = subop; \
        c.mod_mask = 0xC0; c.mod_val = 0xC0; \
        DispatchRegistrar<func>::RegisterSpecialized(opcode, c); \
    }

    // Specializations for Group 1 EbIb (0x80)
    REGISTER_GROUP_SPEC(0x80, 0, OpGroup1_Add_EbIb);
    REGISTER_GROUP_SPEC(0x80, 1, OpGroup1_Or_EbIb);
    REGISTER_GROUP_SPEC(0x80, 2, OpGroup1_Adc_EbIb);
    REGISTER_GROUP_SPEC(0x80, 3, OpGroup1_Sbb_EbIb);
    REGISTER_GROUP_SPEC(0x80, 4, OpGroup1_And_EbIb);
    REGISTER_GROUP_SPEC(0x80, 5, OpGroup1_Sub_EbIb);
    REGISTER_GROUP_SPEC(0x80, 6, OpGroup1_Xor_EbIb);
    REGISTER_GROUP_SPEC(0x80, 7, OpGroup1_Cmp_EbIb);

    // Specializations for Group 1 (0x83: r/m32, imm8) - Heavily used (ADD, SUB, CMP)
    REGISTER_GROUP_SPEC(0x83, 0, OpGroup1_Add_EvIz);
    REGISTER_GROUP_SPEC_MODREG(0x83, 0, OpGroup1_Add_EvIz_ModReg);
    
    REGISTER_GROUP_SPEC(0x83, 5, OpGroup1_Sub_EvIz);
    REGISTER_GROUP_SPEC_MODREG(0x83, 5, OpGroup1_Sub_EvIz_ModReg);

    REGISTER_GROUP_SPEC(0x83, 7, OpGroup1_Cmp_EvIz);
    REGISTER_GROUP_SPEC_MODREG(0x83, 7, OpGroup1_Cmp_EvIz_ModReg);

    g_Handlers[0x98] = DispatchWrapper<OpCwde>;
    g_Handlers[0x99] = DispatchWrapper<OpCdq>;
    g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev<>>;
    
    // Group 5 Specializations
    REGISTER_GROUP_SPEC(0xFF, 0, OpGroup5_Inc_Ev);
    REGISTER_GROUP_SPEC(0xFF, 1, OpGroup5_Dec_Ev);
    REGISTER_GROUP_SPEC(0xFF, 2, OpGroup5_Call_Ev);
    REGISTER_GROUP_SPEC(0xFF, 4, OpGroup5_Jmp_Ev);
    REGISTER_GROUP_SPEC(0xFF, 6, OpGroup5_Push_Ev);

    g_Handlers[0x10B] = DispatchWrapper<OpUd2>;
    g_Handlers[0xF6] = DispatchWrapper<OpGroup3_Ev<>>;
    g_Handlers[0xF7] = DispatchWrapper<OpGroup3_Ev<>>;

    REGISTER_GROUP_SPEC(0xF6, 0, OpGroup3_Test_Ev);
    REGISTER_GROUP_SPEC(0xF6, 2, OpGroup3_Not_Ev);
    REGISTER_GROUP_SPEC(0xF6, 3, OpGroup3_Neg_Ev);
    REGISTER_GROUP_SPEC(0xF6, 4, OpGroup3_Mul_Ev);
    REGISTER_GROUP_SPEC(0xF6, 5, OpGroup3_Imul_Ev);
    REGISTER_GROUP_SPEC(0xF6, 6, OpGroup3_Div_Ev);
    REGISTER_GROUP_SPEC(0xF6, 7, OpGroup3_Idiv_Ev);

    REGISTER_GROUP_SPEC(0xF7, 0, OpGroup3_Test_Ev);
    REGISTER_GROUP_SPEC(0xF7, 2, OpGroup3_Not_Ev);
    REGISTER_GROUP_SPEC(0xF7, 3, OpGroup3_Neg_Ev);
    REGISTER_GROUP_SPEC(0xF7, 4, OpGroup3_Mul_Ev);
    REGISTER_GROUP_SPEC(0xF7, 5, OpGroup3_Imul_Ev);
    REGISTER_GROUP_SPEC(0xF7, 6, OpGroup3_Div_Ev);
    REGISTER_GROUP_SPEC(0xF7, 7, OpGroup3_Idiv_Ev);

    g_Handlers[0xFE] = DispatchWrapper<OpGroup4_Eb<>>;

    REGISTER_GROUP_SPEC(0xFE, 0, OpGroup4_Inc_Eb);
    REGISTER_GROUP_SPEC(0xFE, 1, OpGroup4_Dec_Eb);

#undef REGISTER_GROUP_SPEC
    g_Handlers[0x1C7] = DispatchWrapper<OpGroup9>;
    g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Rm_R>;
    g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Rm_R>;
}

}  // namespace x86emu