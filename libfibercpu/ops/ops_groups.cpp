// Instruction Groups & Misc
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../ops_helpers_template.h"
#include "../state.h"

namespace fiberish {

// =========================================================================================
// Group 1: 0x80 (Eb,Ib), 0x81 (Ev,Iz), 0x82 (Eb,Ib - alias), 0x83 (Ev,Ib)
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup1_EbIb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 80: Arith r/m8, imm8
    auto dest_res = ReadModRM8(state, op, utlb);
    if (!dest_res) return;
    uint8_t dest = *dest_res;

    uint8_t src = (uint8_t)op->imm;
    Helper_Group1<uint8_t, UpdateFlags, FixedSubOp>(state, op, dest, src, utlb);
}

// Fixed Size Templates for Ev operations
template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup1_EvIz_T(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // 81: Arith r/m, imm32
    // 83: Arith r/m, imm8 (sign-extended)
    T dest;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM16(state, op, utlb);
        if (!res) return;
        dest = *res;
    } else {
        auto res = ReadModRM32(state, op, utlb);
        if (!res) return;
        dest = *res;
    }

    T src;
    if (op->extra == 0x3) {                 // 0x83
        src = (T)(int16_t)(int8_t)op->imm;  // Sign extend byte to T
    } else {                                // 0x81
        src = (T)op->imm;
    }

    Helper_Group1<T, UpdateFlags, FixedSubOp>(state, op, dest, src, utlb);
}

// =========================================================================================
// Group 3: 0xF6 (Eb), 0xF7 (Ev)
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup3_Eb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F6
    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
    uint8_t val = *val_res;
    Helper_Group3<uint8_t, UpdateFlags, FixedSubOp>(state, op, val, utlb);
}

template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup3_Ev_T(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // F7
    T val;
    if constexpr (sizeof(T) == 2) {
        auto res = ReadModRM16(state, op, utlb);
        if (!res) return;
        val = *res;
    } else {
        auto res = ReadModRM32(state, op, utlb);
        if (!res) return;
        val = *res;
    }

    Helper_Group3<T, UpdateFlags, FixedSubOp>(state, op, val, utlb);
}

// =========================================================================================
// Group 4: 0xFE (Eb) - INC/DEC
// =========================================================================================

template <bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup4_Eb(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FE
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    auto val_res = ReadModRM8(state, op, utlb);
    if (!val_res) return;
    uint8_t val = *val_res;

    uint32_t old_cf = state->ctx.eflags & CF_MASK;

    switch (subop) {
        case 0:  // INC
        {
            uint8_t res = AluAdd<uint8_t, UpdateFlags>(state, val, (uint8_t)1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res, utlb);
            break;
        }
        case 1:  // DEC
        {
            uint8_t res = AluSub<uint8_t, UpdateFlags>(state, val, (uint8_t)1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;
            WriteModRM8(state, op, res, utlb);
            break;
        }
        default:
            OpUd2(state, op);
    }
}

// =========================================================================================
// Group 5: 0xFF (Ev)
// =========================================================================================

template <typename T, bool UpdateFlags, uint8_t FixedSubOp = 0xFF>
static FORCE_INLINE void OpGroup5_Ev_T(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    // FF
    uint8_t subop;
    if constexpr (FixedSubOp != 0xFF) {
        subop = FixedSubOp;
    } else {
        subop = (op->modrm >> 3) & 7;
    }

    switch (subop) {
        case 0:  // INC Ev
        {
            T val;
            if constexpr (sizeof(T) == 2) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                val = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                val = *res;
            }

            uint32_t old_cf = state->ctx.eflags & CF_MASK;
            T res = AluAdd<T, UpdateFlags>(state, val, 1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 2)
                WriteModRM16(state, op, res, utlb);
            else
                WriteModRM32(state, op, res, utlb);
            break;
        }
        case 1:  // DEC Ev
        {
            T val;
            if constexpr (sizeof(T) == 2) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                val = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                val = *res;
            }

            uint32_t old_cf = state->ctx.eflags & CF_MASK;
            T res = AluSub<T, UpdateFlags>(state, val, 1);
            if constexpr (UpdateFlags) state->ctx.eflags = (state->ctx.eflags & ~CF_MASK) | old_cf;

            if constexpr (sizeof(T) == 2)
                WriteModRM16(state, op, res, utlb);
            else
                WriteModRM32(state, op, res, utlb);
            break;
        }
        case 2:  // CALL Ev (Near Indirect)
        case 4:  // JMP Ev (Near Indirect)
        case 6:  // PUSH Ev
        {
            // These Ops do not flag, so UpdateFlags ignored (effectively always false/true logic same)
            // Reuse implementation from switch
            uint32_t val = 0;  // Or target
            if constexpr (sizeof(T) == 2) {
                auto res = ReadModRM16(state, op, utlb);
                if (!res) return;
                val = *res;
            } else {
                auto res = ReadModRM32(state, op, utlb);
                if (!res) return;
                val = *res;
            }

            if (subop == 2) {  // Call
                if constexpr (sizeof(T) == 2) {
                    if (!Push16(state, (uint16_t)op->next_eip, utlb, op)) return;
                    op->branch_target = val & 0xFFFF;
                } else {
                    if (!Push32(state, op->next_eip, utlb, op)) return;
                    op->branch_target = val;
                }
            } else if (subop == 4) {  // Jmp
                if constexpr (sizeof(T) == 2)
                    op->branch_target = val & 0xFFFF;
                else
                    op->branch_target = val;
            } else if (subop == 6) {  // Push
                if constexpr (sizeof(T) == 2) {
                    if (!Push16(state, (uint16_t)val, utlb, op)) return;
                } else {
                    if (!Push32(state, val, utlb, op)) return;
                }
            }
            break;
        }
        default:
            OpUd2(state, op);
            break;
    }
}

// Wrappers for Dispatch (Generic fallback)
static void OpGroup1_EvIz_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    if (o->prefixes.flags.opsize)
        OpGroup1_EvIz_T<uint16_t, true>(s, o, u);
    else
        OpGroup1_EvIz_T<uint32_t, true>(s, o, u);
}

static void OpGroup3_Ev_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    if (o->prefixes.flags.opsize)
        OpGroup3_Ev_T<uint16_t, true>(s, o, u);
    else
        OpGroup3_Ev_T<uint32_t, true>(s, o, u);
}

static void OpGroup5_Ev_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {
    if (o->prefixes.flags.opsize)
        OpGroup5_Ev_T<uint16_t, true>(s, o, u);
    else
        OpGroup5_Ev_T<uint32_t, true>(s, o, u);
}

static void OpGroup4_Eb_Generic(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { OpGroup4_Eb<true>(s, o, u); }

// Implements wrappers: e.g. OpGroup1_EbIb_0_Flags, OpGroup1_EbIb_0_NoFlags
#define IMPL_G1_EB(subop, name)                                                                \
    [[maybe_unused]] static void name##_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {   \
        OpGroup1_EbIb<true, subop>(s, o, u);                                                   \
    }                                                                                          \
    [[maybe_unused]] static void name##_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        OpGroup1_EbIb<false, subop>(s, o, u);                                                  \
    }

IMPL_G1_EB(0, OpGroup1_EbIb_Add)
IMPL_G1_EB(1, OpGroup1_EbIb_Or)
IMPL_G1_EB(2, OpGroup1_EbIb_Adc)
IMPL_G1_EB(3, OpGroup1_EbIb_Sbb)
IMPL_G1_EB(4, OpGroup1_EbIb_And)
IMPL_G1_EB(5, OpGroup1_EbIb_Sub)
IMPL_G1_EB(6, OpGroup1_EbIb_Xor)
IMPL_G1_EB(7, OpGroup1_EbIb_Cmp)

// Implements wrappers: e.g. OpGroup1_EvIz_T_Add_32_Flags
#define IMPL_EV_SPEC(subop, name, func)                                                           \
    [[maybe_unused]] static void name##_32_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {   \
        func<uint32_t, true, subop>(s, o, u);                                                     \
    }                                                                                             \
    [[maybe_unused]] static void name##_32_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        func<uint32_t, false, subop>(s, o, u);                                                    \
    }                                                                                             \
    [[maybe_unused]] static void name##_16_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {   \
        func<uint16_t, true, subop>(s, o, u);                                                     \
    }                                                                                             \
    [[maybe_unused]] static void name##_16_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        func<uint16_t, false, subop>(s, o, u);                                                    \
    }

// Group 1
IMPL_EV_SPEC(0, OpGroup1_EvIz_Add, OpGroup1_EvIz_T)
IMPL_EV_SPEC(5, OpGroup1_EvIz_Sub, OpGroup1_EvIz_T)
IMPL_EV_SPEC(7, OpGroup1_EvIz_Cmp, OpGroup1_EvIz_T)

// Group 3
IMPL_EV_SPEC(2, OpGroup3_Ev_Not, OpGroup3_Ev_T)
IMPL_EV_SPEC(3, OpGroup3_Ev_Neg, OpGroup3_Ev_T)
IMPL_EV_SPEC(4, OpGroup3_Ev_Mul, OpGroup3_Ev_T)
IMPL_EV_SPEC(5, OpGroup3_Ev_Imul, OpGroup3_Ev_T)
IMPL_EV_SPEC(6, OpGroup3_Ev_Div, OpGroup3_Ev_T)   // Only Size relevant
IMPL_EV_SPEC(7, OpGroup3_Ev_Idiv, OpGroup3_Ev_T)  // Only Size relevant

// Group 5
IMPL_EV_SPEC(0, OpGroup5_Ev_Inc, OpGroup5_Ev_T)
IMPL_EV_SPEC(1, OpGroup5_Ev_Dec, OpGroup5_Ev_T)
IMPL_EV_SPEC(2, OpGroup5_Ev_Call, OpGroup5_Ev_T)
IMPL_EV_SPEC(4, OpGroup5_Ev_Jmp, OpGroup5_Ev_T)
IMPL_EV_SPEC(6, OpGroup5_Ev_Push, OpGroup5_Ev_T)

// Group 3 Eb
#define IMPL_G3_EB(subop, name)                                                                \
    [[maybe_unused]] static void name##_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {   \
        OpGroup3_Eb<true, subop>(s, o, u);                                                     \
    }                                                                                          \
    [[maybe_unused]] static void name##_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        OpGroup3_Eb<false, subop>(s, o, u);                                                    \
    }

IMPL_G3_EB(2, OpGroup3_Eb_Not)
IMPL_G3_EB(3, OpGroup3_Eb_Neg)
IMPL_G3_EB(4, OpGroup3_Eb_Mul)
IMPL_G3_EB(5, OpGroup3_Eb_Imul)
IMPL_G3_EB(6, OpGroup3_Eb_Div)
IMPL_G3_EB(7, OpGroup3_Eb_Idiv)

// Group 4 Eb
#define IMPL_G4_EB(subop, name)                                                                \
    [[maybe_unused]] static void name##_Flags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) {   \
        OpGroup4_Eb<true, subop>(s, o, u);                                                     \
    }                                                                                          \
    [[maybe_unused]] static void name##_NoFlags(EmuState* s, DecodedOp* o, mem::MicroTLB* u) { \
        OpGroup4_Eb<false, subop>(s, o, u);                                                    \
    }

IMPL_G4_EB(0, OpGroup4_Eb_Inc)
IMPL_G4_EB(1, OpGroup4_Eb_Dec)

// Misc Ops (unchanged except include)
static FORCE_INLINE void OpCdq(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint32_t eax = GetReg(state, EAX);
    uint32_t edx = ((int32_t)eax < 0) ? 0xFFFFFFFF : 0;
    SetReg(state, EDX, edx);
}

static FORCE_INLINE void OpCwde(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (op->prefixes.flags.opsize) {
        int8_t val = (int8_t)GetReg(state, EAX);
        uint32_t current = GetReg(state, EAX);
        uint32_t res = (current & 0xFFFF0000) | (uint16_t)(int16_t)val;
        SetReg(state, EAX, res);
    } else {
        int16_t val = (int16_t)GetReg(state, EAX);
        SetReg(state, EAX, (uint32_t)(int32_t)val);
    }
}

void OpUd2(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    if (!state->hooks.on_invalid_opcode(state)) {
        TriggerPreciseFault(state, op);
        state->status = EmuStatus::Fault;
        state->fault_vector = 6;
    }
}
void OpUd2(EmuState* state, DecodedOp* op) { OpUd2(state, op, nullptr); }

static FORCE_INLINE void OpGroup9(EmuState* state, DecodedOp* op, mem::MicroTLB* utlb) {
    uint8_t sub = (op->modrm >> 3) & 7;
    if (sub == 1) {  // CMPXCHG8B
        uint32_t addr = ComputeLinearAddress(state, op);
        auto mem_res = state->mmu.read<uint64_t>(state, addr, utlb, op);
        if (!mem_res) return;
        uint64_t mem_val = *mem_res;

        uint32_t eax = GetReg(state, EAX);
        uint32_t edx = GetReg(state, EDX);
        uint64_t edx_eax = ((uint64_t)edx << 32) | eax;
        if (mem_val == edx_eax) {
            state->ctx.eflags |= ZF_MASK;
            uint32_t ebx = GetReg(state, EBX);
            uint32_t ecx = GetReg(state, ECX);
            uint64_t ecx_ebx = ((uint64_t)ecx << 32) | ebx;
            (void)state->mmu.write<uint64_t>(state, addr, ecx_ebx, utlb, op);
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
    // Simplified XADD (Generic)
    uint32_t width = 4;
    if (op->extra == 0x0)
        width = 1;
    else if (op->prefixes.flags.opsize)
        width = 2;

    uint32_t dest_val = 0;
    if (width == 1) {
        auto res = ReadModRM8(state, op, utlb);
        if (!res) return;
        dest_val = *res;
    } else if (width == 2) {
        auto res = ReadModRM16(state, op, utlb);
        if (!res) return;
        dest_val = *res;
    } else {
        auto res = ReadModRM32(state, op, utlb);
        if (!res) return;
        dest_val = *res;
    }

    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t src_val = 0;
    if (width == 1)
        src_val = GetReg8(state, reg);
    else if (width == 2)
        src_val = GetReg(state, reg) & 0xFFFF;
    else
        src_val = GetReg(state, reg);

    uint32_t res = 0;
    // Always update flags for XADD
    if (width == 1)
        res = AluAdd(state, (uint8_t)dest_val, (uint8_t)src_val);
    else if (width == 2)
        res = AluAdd(state, (uint16_t)dest_val, (uint16_t)src_val);
    else
        res = AluAdd(state, (uint32_t)dest_val, (uint32_t)src_val);

    // Swap: Original Dest -> Src Reg
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
        *rptr = (*rptr & 0xFFFF0000) | (dest_val & 0xFFFF);
    } else {
        SetReg(state, reg, dest_val);
    }

    // Result -> Dest Memory/Reg
    if (width == 1)
        WriteModRM8(state, op, (uint8_t)res, utlb);
    else if (width == 2)
        WriteModRM16(state, op, (uint16_t)res, utlb);
    else
        WriteModRM32(state, op, res, utlb);
}

// =========================================================================================
// Registration
// =========================================================================================

void RegisterGroupOps() {
    // Generic Handlers (fallback)
    g_Handlers[0x80] = DispatchWrapper<OpGroup1_EbIb<true>>;
    g_Handlers[0x81] = DispatchWrapper<OpGroup1_EvIz_Generic>;
    g_Handlers[0x82] = DispatchWrapper<OpGroup1_EbIb<true>>;  // Alias of 80
    g_Handlers[0x83] = DispatchWrapper<OpGroup1_EvIz_Generic>;

    g_Handlers[0xF6] = DispatchWrapper<OpGroup3_Eb<true>>;
    g_Handlers[0xF7] = DispatchWrapper<OpGroup3_Ev_Generic>;
    g_Handlers[0xFE] = DispatchWrapper<OpGroup4_Eb_Generic>;
    g_Handlers[0xFF] = DispatchWrapper<OpGroup5_Ev_Generic>;

    g_Handlers[0x98] = DispatchWrapper<OpCwde>;
    g_Handlers[0x99] = DispatchWrapper<OpCdq>;
    g_Handlers[0x1C7] = DispatchWrapper<OpGroup9>;
    g_Handlers[0x1C0] = DispatchWrapper<OpXadd_Rm_R>;
    g_Handlers[0x1C1] = DispatchWrapper<OpXadd_Rm_R>;
    g_Handlers[0x10B] = DispatchWrapper<OpUd2>;

// Macro for Group 1 EbIb (Byte) - No Size variant needed, just NF
#define REG_G1_EB(opcode, subop, name)                                     \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        DispatchRegistrar<name##_Flags>::RegisterSpecialized(opcode, c);   \
    }                                                                      \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        c.no_flags = true;                                                 \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecialized(opcode, c); \
    }

    REG_G1_EB(0x80, 0, OpGroup1_EbIb_Add);
    REG_G1_EB(0x80, 1, OpGroup1_EbIb_Or);
    REG_G1_EB(0x80, 2, OpGroup1_EbIb_Adc);
    REG_G1_EB(0x80, 3, OpGroup1_EbIb_Sbb);
    REG_G1_EB(0x80, 4, OpGroup1_EbIb_And);
    REG_G1_EB(0x80, 5, OpGroup1_EbIb_Sub);
    REG_G1_EB(0x80, 6, OpGroup1_EbIb_Xor);
    REG_G1_EB(0x80, 7, OpGroup1_EbIb_Cmp);

// Macro for Group 1 EvIz, Group 5 Ev, etc. (Size + NF)
#define REG_EV_SPEC(opcode, subop, name)                                      \
    /* 32-bit Normal */                                                       \
    {                                                                         \
        SpecCriteria c;                                                       \
        c.reg_mask = 7;                                                       \
        c.reg_val = subop;                                                    \
        c.prefix_mask = 0x40;                                                 \
        c.prefix_val = 0;                                                     \
        DispatchRegistrar<name##_32_Flags>::RegisterSpecialized(opcode, c);   \
    }                                                                         \
    /* 32-bit NF */                                                           \
    {                                                                         \
        SpecCriteria c;                                                       \
        c.reg_mask = 7;                                                       \
        c.reg_val = subop;                                                    \
        c.prefix_mask = 0x40;                                                 \
        c.prefix_val = 0;                                                     \
        c.no_flags = true;                                                    \
        DispatchRegistrar<name##_32_NoFlags>::RegisterSpecialized(opcode, c); \
    }                                                                         \
    /* 16-bit Normal */                                                       \
    {                                                                         \
        SpecCriteria c;                                                       \
        c.reg_mask = 7;                                                       \
        c.reg_val = subop;                                                    \
        c.prefix_mask = 0x40;                                                 \
        c.prefix_val = 0x40;                                                  \
        DispatchRegistrar<name##_16_Flags>::RegisterSpecialized(opcode, c);   \
    }                                                                         \
    /* 16-bit NF */                                                           \
    {                                                                         \
        SpecCriteria c;                                                       \
        c.reg_mask = 7;                                                       \
        c.reg_val = subop;                                                    \
        c.prefix_mask = 0x40;                                                 \
        c.prefix_val = 0x40;                                                  \
        c.no_flags = true;                                                    \
        DispatchRegistrar<name##_16_NoFlags>::RegisterSpecialized(opcode, c); \
    }

    // Group 1: 0x83 (Mostly used)
    REG_EV_SPEC(0x83, 0, OpGroup1_EvIz_Add);
    REG_EV_SPEC(0x83, 5, OpGroup1_EvIz_Sub);
    REG_EV_SPEC(0x83, 7, OpGroup1_EvIz_Cmp);

    // Group 1: 0x81 (Also used)
    REG_EV_SPEC(0x81, 0, OpGroup1_EvIz_Add);
    REG_EV_SPEC(0x81, 5, OpGroup1_EvIz_Sub);
    REG_EV_SPEC(0x81, 7, OpGroup1_EvIz_Cmp);

// Group 3: 0xF6 (Eb) - Only NF needed
#define REG_G3_EB(opcode, subop, name)                                     \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        DispatchRegistrar<name##_Flags>::RegisterSpecialized(opcode, c);   \
    }                                                                      \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        c.no_flags = true;                                                 \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecialized(opcode, c); \
    }

    REG_G3_EB(0xF6, 2, OpGroup3_Eb_Not);
    REG_G3_EB(0xF6, 3, OpGroup3_Eb_Neg);
    REG_G3_EB(0xF6, 4, OpGroup3_Eb_Mul);
    REG_G3_EB(0xF6, 5, OpGroup3_Eb_Imul);

    // Group 3: 0xF7 (Ev) - Size + NF
    REG_EV_SPEC(0xF7, 2, OpGroup3_Ev_Not);
    REG_EV_SPEC(0xF7, 3, OpGroup3_Ev_Neg);
    REG_EV_SPEC(0xF7, 4, OpGroup3_Ev_Mul);
    REG_EV_SPEC(0xF7, 5, OpGroup3_Ev_Imul);
    REG_EV_SPEC(0xF7, 6, OpGroup3_Ev_Div);   // Only Size relevant
    REG_EV_SPEC(0xF7, 7, OpGroup3_Ev_Idiv);  // Only Size relevant

// Group 4: 0xFE (Eb) - INC/DEC
#define REG_G4_EB(opcode, subop, name)                                     \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        DispatchRegistrar<name##_Flags>::RegisterSpecialized(opcode, c);   \
    }                                                                      \
    {                                                                      \
        SpecCriteria c;                                                    \
        c.reg_mask = 7;                                                    \
        c.reg_val = subop;                                                 \
        c.no_flags = true;                                                 \
        DispatchRegistrar<name##_NoFlags>::RegisterSpecialized(opcode, c); \
    }

    REG_G4_EB(0xFE, 0, OpGroup4_Eb_Inc);
    REG_G4_EB(0xFE, 1, OpGroup4_Eb_Dec);

    // Group 5: 0xFF (Ev)
    REG_EV_SPEC(0xFF, 0, OpGroup5_Ev_Inc);
    REG_EV_SPEC(0xFF, 1, OpGroup5_Ev_Dec);
    REG_EV_SPEC(0xFF, 2, OpGroup5_Ev_Call);
    REG_EV_SPEC(0xFF, 4, OpGroup5_Ev_Jmp);
    REG_EV_SPEC(0xFF, 6, OpGroup5_Ev_Push);

#undef REG_G1_EB
#undef REG_EV_SPEC
#undef REG_G3_EB
#undef REG_G4_EB
}

}  // namespace fiberish