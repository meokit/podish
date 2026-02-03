// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpAdd_Sse(EmuState* state, DecodedOp* op) {
    // 0F 58: ADDPS/ADDPD/ADDSS/ADDSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;

    // Dest is always Register
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (op->prefixes.flags.opsize) {  // 66: ADDPD (Packed Double)
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_add_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.repne) {  // F2: ADDSD (Scalar Double)
        // Src is 64-bit (Mem) or 128-bit (Reg)
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t mem_val = state->mmu.read<uint64_t>(addr);
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_add_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.rep) {  // F3: ADDSS (Scalar Single)
        // Src is 32-bit (Mem) or 128-bit (Reg)
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t mem_val = state->mmu.read<uint32_t>(addr);
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_add_ss(dst_val, src_val);
    } else {  // None: ADDPS (Packed Single)
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_add_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpSub_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5C: SUBPS/SUBPD/SUBSS/SUBSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (op->prefixes.flags.opsize) {  // 66: SUBPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_sub_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.repne) {  // F2: SUBSD
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t mem_val = state->mmu.read<uint64_t>(addr);
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_sub_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.rep) {  // F3: SUBSS
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t mem_val = state->mmu.read<uint32_t>(addr);
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_sub_ss(dst_val, src_val);
    } else {  // None: SUBPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_sub_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpMul_Sse(EmuState* state, DecodedOp* op) {
    // 0F 59: MULPS/MULPD/MULSS/MULSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (op->prefixes.flags.opsize) {  // 66: MULPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_mul_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.repne) {  // F2: MULSD
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t mem_val = state->mmu.read<uint64_t>(addr);
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_mul_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.rep) {  // F3: MULSS
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t mem_val = state->mmu.read<uint32_t>(addr);
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_mul_ss(dst_val, src_val);
    } else {  // None: MULPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_mul_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpDiv_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5E: DIVPS/DIVPD/DIVSS/DIVSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (op->prefixes.flags.opsize) {  // 66: DIVPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_div_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.repne) {  // F2: DIVSD
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t mem_val = state->mmu.read<uint64_t>(addr);
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_div_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (op->prefixes.flags.rep) {  // F3: DIVSS
        if ((op->modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t mem_val = state->mmu.read<uint32_t>(addr);
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_div_ss(dst_val, src_val);
    } else {  // None: DIVPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_div_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpAnd_Sse(EmuState* state, DecodedOp* op) {
    // 0F 54: ANDPS/ANDPD
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val = ReadModRM128(state, op);

    if (op->prefixes.flags.opsize) {  // 66: ANDPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_and_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // None (or F2/F3 ignored): ANDPS
        state->ctx.xmm[reg] = simde_mm_and_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpAndn_Sse(EmuState* state, DecodedOp* op) {
    // 0F 55: ANDNPS/ANDNPD
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val = ReadModRM128(state, op);

    if (op->prefixes.flags.opsize) {  // 66: ANDNPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_andnot_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // None: ANDNPS
        state->ctx.xmm[reg] = simde_mm_andnot_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpOr_Sse(EmuState* state, DecodedOp* op) {
    // 0F 56: ORPS/ORPD
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val = ReadModRM128(state, op);

    if (op->prefixes.flags.opsize) {  // 66: ORPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_or_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // ORPS
        state->ctx.xmm[reg] = simde_mm_or_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpXor_Sse(EmuState* state, DecodedOp* op) {
    // 0F 57: XORPS/XORPD
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val = ReadModRM128(state, op);

    if (op->prefixes.flags.opsize) {  // 66: XORPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_xor_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // XORPS
        state->ctx.xmm[reg] = simde_mm_xor_ps(dst_val, src_val);
    }
}

static FORCE_INLINE void OpCmp_Sse(EmuState* state, DecodedOp* op) {
    uint8_t pred = (uint8_t)op->imm;
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (op->prefixes.flags.opsize) {
        // 66 0F C2: CMPPD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);

        simde__m128d res = Helper_CmpPD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (op->prefixes.flags.repne) {
        // F2 0F C2: CMPSD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d src_pd;

        if ((op->modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
            src_pd = simde_mm_set_sd(*(double*)&val);
        }

        simde__m128d res = Helper_CmpSD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (op->prefixes.flags.rep) {
        // F3 0F C2: CMPSS
        simde__m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t val = state->mmu.read<uint32_t>(ComputeEAD(state, op));
            src = simde_mm_set_ss(*(float*)&val);
        }
        *dest_ptr = Helper_CmpSS(*dest_ptr, src, pred);

    } else {
        // 0F C2: CMPPS
        simde__m128 src = ReadModRM128(state, op);
        *dest_ptr = Helper_CmpPS(*dest_ptr, src, pred);
    }
}

static FORCE_INLINE void OpMaxMin_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5F: MAX (PD/SD/PS/SS)
    // 0F 5D: MIN (PD/SD/PS/SS)
    // Prefix determines type:
    // None: PS (Packed Single)
    // 66:   PD (Packed Double)
    // F3:   SS (Scalar Single)
    // F2:   SD (Scalar Double)

    bool is_min = (op->handler_index & 0xFF) == 0x5D;
    uint8_t reg_idx = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.repne) {  // F2: Scalar Double
        double b;
        if (op->modrm >= 0xC0)
            b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<double>(ComputeEAD(state, op));

        double* dest = (double*)&state->ctx.xmm[reg_idx];  // [0]
        if (is_min)
            *dest = (*dest < b) ? *dest : b;
        else
            *dest = (*dest > b) ? *dest : b;
        // High 64 bits unmodified

    } else if (op->prefixes.flags.rep) {  // F3: Scalar Single
        float b;
        if (op->modrm >= 0xC0)
            b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<float>(ComputeEAD(state, op));

        float* dest = (float*)&state->ctx.xmm[reg_idx];  // [0]
        if (is_min)
            *dest = (*dest < b) ? *dest : b;
        else
            *dest = (*dest > b) ? *dest : b;
        // High 96 bits unmodified

    } else if (op->prefixes.flags.opsize) {  // 66: Packed Double
        simde__m128 val = ReadModRM128(state, op);
        simde__m128d a = simde_mm_castps_pd(state->ctx.xmm[reg_idx]);
        simde__m128d b = simde_mm_castps_pd(val);
        simde__m128d res = is_min ? simde_mm_min_pd(a, b) : simde_mm_max_pd(a, b);
        state->ctx.xmm[reg_idx] = simde_mm_castpd_ps(res);

    } else {  // None: Packed Single
        simde__m128 val = ReadModRM128(state, op);
        simde__m128 a = state->ctx.xmm[reg_idx];
        // Using SIMDe directly on simde__m128 (aliased to simde__m128)
        state->ctx.xmm[reg_idx] = is_min ? simde_mm_min_ps(a, val) : simde_mm_max_ps(a, val);
    }
}

static FORCE_INLINE void OpMovAp_Sse(EmuState* state, DecodedOp* op) {
    // 0F 28: MOVAPS xmm1, xmm2/m128 (Load/Move)
    // 0F 29: MOVAPS xmm2/m128, xmm1 (Store)
    // 66 Prefix: MOVAPD (Double) - Same bitwise movement usually, but alignment might differ on
    // real HW. For specialized emulation, we treat them as 128-bit moves.

    // Check Direction:
    // 28: Load (Gv, Ev) -> Reg = ModRM
    // 29: Store (Ev, Gv) -> ModRM = Reg

    // Note: Handler index will separate 28/29.

    uint8_t opcode = op->handler_index & 0xFF;  // 28 or 29

    if (opcode == 0x28) {  // Load
        simde__m128 val = ReadModRM128(state, op);
        uint8_t reg = (op->modrm >> 3) & 7;
        state->ctx.xmm[reg] = val;
    } else {  // Store 0x29
        uint8_t reg = (op->modrm >> 3) & 7;
        simde__m128 val = state->ctx.xmm[reg];
        // WriteModRM128 logic inline
        if (op->modrm >= 0xC0) {
            state->ctx.xmm[op->modrm & 7] = val;
        } else {
            uint32_t addr = ComputeEAD(state, op);
            // MOVAPS/MOVAPD require alignment check?
            // Ignoring for SoftMMU unless strict mode.
            // Using generic write128 helper if available or manual.
            if (op->modrm < 0xC0) {
                // Check alignment?
                // if ((addr & 15) != 0) { Fault(#GP); return; }
            }
            state->mmu.write<uint64_t>(addr, ((uint64_t*)&val)[0]);
            state->mmu.write<uint64_t>(addr + 8, ((uint64_t*)&val)[1]);
        }
    }
}

simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0:
            return simde_mm_cmpeq_pd(a, b);
        case 1:
            return simde_mm_cmplt_pd(a, b);
        case 2:
            return simde_mm_cmple_pd(a, b);
        case 3:
            return simde_mm_cmpunord_pd(a, b);
        case 4:
            return simde_mm_cmpneq_pd(a, b);
        case 5:
            return simde_mm_cmpnlt_pd(a, b);
        case 6:
            return simde_mm_cmpnle_pd(a, b);
        case 7:
            return simde_mm_cmpord_pd(a, b);
    }
    return a;
}

simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0:
            return simde_mm_cmpeq_sd(a, b);
        case 1:
            return simde_mm_cmplt_sd(a, b);
        case 2:
            return simde_mm_cmple_sd(a, b);
        case 3:
            return simde_mm_cmpunord_sd(a, b);
        case 4:
            return simde_mm_cmpneq_sd(a, b);
        case 5:
            return simde_mm_cmpnlt_sd(a, b);
        case 6:
            return simde_mm_cmpnle_sd(a, b);
        case 7:
            return simde_mm_cmpord_sd(a, b);
    }
    return a;
}

simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0:
            return simde_mm_cmpeq_ps(a, b);
        case 1:
            return simde_mm_cmplt_ps(a, b);
        case 2:
            return simde_mm_cmple_ps(a, b);
        case 3:
            return simde_mm_cmpunord_ps(a, b);
        case 4:
            return simde_mm_cmpneq_ps(a, b);
        case 5:
            return simde_mm_cmpnlt_ps(a, b);
        case 6:
            return simde_mm_cmpnle_ps(a, b);
        case 7:
            return simde_mm_cmpord_ps(a, b);
    }
    return a;
}

simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0:
            return simde_mm_cmpeq_ss(a, b);
        case 1:
            return simde_mm_cmplt_ss(a, b);
        case 2:
            return simde_mm_cmple_ss(a, b);
        case 3:
            return simde_mm_cmpunord_ss(a, b);
        case 4:
            return simde_mm_cmpneq_ss(a, b);
        case 5:
            return simde_mm_cmpnlt_ss(a, b);
        case 6:
            return simde_mm_cmpnle_ss(a, b);
        case 7:
            return simde_mm_cmpord_ss(a, b);
    }
    return a;
}

// Sqrt Unified
static FORCE_INLINE void OpSqrt_Sse(EmuState* state, DecodedOp* op) {
    // 0F 51: SQRTPS
    // 66 0F 51: SQRTPD
    // F2 0F 51: SQRTSD
    // F3 0F 51: SQRTSS

    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.repne) {  // F2: SQRTSD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        simde__m128d src;

        if ((op->modrm >> 6) == 3) {
            src = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t val = state->mmu.read<uint64_t>(addr);
            double d;
            std::memcpy(&d, &val, 8);
            src = simde_mm_set_sd(d);
        }

        simde__m128d res = simde_mm_sqrt_sd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);

    } else if (op->prefixes.flags.rep) {  // F3: SQRTSS
        simde__m128 dest = state->ctx.xmm[reg];
        simde__m128 src;

        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t val = state->mmu.read<uint32_t>(addr);
            float f;
            std::memcpy(&f, &val, 4);
            src = simde_mm_set_ss(f);
        }

        simde__m128 sqrt_val = simde_mm_sqrt_ss(src);
        state->ctx.xmm[reg] = simde_mm_move_ss(dest, sqrt_val);

    } else if (op->prefixes.flags.opsize) {  // 66: SQRTPD
        simde__m128d src = simde_mm_castps_pd(ReadModRM128(state, op));
        simde__m128d res = simde_mm_sqrt_pd(src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);

    } else {  // None: SQRTPS
        simde__m128 src = ReadModRM128(state, op);
        simde__m128 res = simde_mm_sqrt_ps(src);
        state->ctx.xmm[reg] = res;
    }
}

// Unified UCOMIS (Scalar Ordered Compare)
static FORCE_INLINE void OpUcomis_Unified(EmuState* state, DecodedOp* op) {
    // 0F 2E: UCOMISS
    // 66 0F 2E: UCOMISD

    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t flags =
        state->ctx.eflags & ~(ZF_MASK | PF_MASK | CF_MASK | OF_MASK | AF_MASK | SF_MASK);

    bool is_unordered = false;
    bool is_less = false;
    bool is_equal = false;

    if (op->prefixes.flags.opsize) {  // 66: UCOMISD
        double a = ((double*)&state->ctx.xmm[reg])[0];
        double b;
        if ((op->modrm >> 6) == 3) {
            b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint64_t val = state->mmu.read<uint64_t>(addr);
            std::memcpy(&b, &val, 8);
        }

        if (std::isnan(a) || std::isnan(b))
            is_unordered = true;
        else if (a < b)
            is_less = true;
        else if (a == b)
            is_equal = true;

    } else {  // None: UCOMISS
        float a = ((float*)&state->ctx.xmm[reg])[0];
        float b;
        if ((op->modrm >> 6) == 3) {
            b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        } else {
            uint32_t addr = ComputeEAD(state, op);
            uint32_t val = state->mmu.read<uint32_t>(addr);
            std::memcpy(&b, &val, 4);
        }

        if (std::isnan(a) || std::isnan(b))
            is_unordered = true;
        else if (a < b)
            is_less = true;
        else if (a == b)
            is_equal = true;
    }

    if (is_unordered) {
        flags |= ZF_MASK | PF_MASK | CF_MASK;
    } else if (is_less) {
        flags |= CF_MASK;
    } else if (is_equal) {
        flags |= ZF_MASK;
    }
    // Else (Greater): All Clear

    state->ctx.eflags = flags;
}

// Unified SHUF (Packed Shuffle)
static FORCE_INLINE void OpShuf_Unified(EmuState* state, DecodedOp* op) {
    // 0F C6: SHUFPS
    // 66 0F C6: SHUFPD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t imm = op->imm;

    if (op->prefixes.flags.opsize) {  // 66: SHUFPD
        double* dest_arr = (double*)&state->ctx.xmm[reg];
        simde__m128 src_val = ReadModRM128(state, op);
        double* src_arr = (double*)&src_val;

        double result[2];
        result[0] = dest_arr[(imm & 1)];        // Select from dest
        result[1] = src_arr[((imm >> 1) & 1)];  // Select from src

        std::memcpy(&state->ctx.xmm[reg], result, 16);

    } else {  // None: SHUFPS
        float* dest_arr = (float*)&state->ctx.xmm[reg];
        simde__m128 src_val = ReadModRM128(state, op);
        float* src_arr = (float*)&src_val;

        float result[4];
        result[0] = dest_arr[(imm & 3)];         // Select from dest[0:1]
        result[1] = dest_arr[((imm >> 2) & 3)];  // Select from dest[0:1]
        result[2] = src_arr[((imm >> 4) & 3)];   // Select from src[2:3]
        result[3] = src_arr[((imm >> 6) & 3)];   // Select from src[2:3]

        std::memcpy(&state->ctx.xmm[reg], result, 16);
    }
}

// Unified UNPCKL (Unpack Low)
static FORCE_INLINE void OpUnpckl_Unified(EmuState* state, DecodedOp* op) {
    // 0F 14: UNPCKLPS
    // 66 0F 14: UNPCKLPD
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.opsize) {  // 66: UNPCKLPD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        simde__m128d src = simde_mm_castps_pd(ReadModRM128(state, op));
        simde__m128d res = simde_mm_unpacklo_pd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);
    } else {  // None: UNPCKLPS
        simde__m128 dest = state->ctx.xmm[reg];
        simde__m128 src = ReadModRM128(state, op);
        simde__m128 res = simde_mm_unpacklo_ps(dest, src);
        state->ctx.xmm[reg] = res;
    }
}

// Unified UNPCKH (Unpack High)
static FORCE_INLINE void OpUnpckh_Unified(EmuState* state, DecodedOp* op) {
    // 0F 15: UNPCKHPS
    // 66 0F 15: UNPCKHPD
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.opsize) {  // 66: UNPCKHPD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        simde__m128d src = simde_mm_castps_pd(ReadModRM128(state, op));
        simde__m128d res = simde_mm_unpackhi_pd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);
    } else {  // None: UNPCKHPS
        simde__m128 dest = state->ctx.xmm[reg];
        simde__m128 src = ReadModRM128(state, op);
        simde__m128 res = simde_mm_unpackhi_ps(dest, src);
        state->ctx.xmm[reg] = res;
    }
}

void RegisterSseFpOps() {
    g_Handlers[0x1C2] = DispatchWrapper<OpCmp_Sse>;
    g_Handlers[0x15E] = DispatchWrapper<OpDiv_Sse>;
    g_Handlers[0x128] = DispatchWrapper<OpMovAp_Sse>;
    g_Handlers[0x129] = DispatchWrapper<OpMovAp_Sse>;
    g_Handlers[0x15F] = DispatchWrapper<OpMaxMin_Sse>;
    g_Handlers[0x15D] = DispatchWrapper<OpMaxMin_Sse>;
    g_Handlers[0x158] = DispatchWrapper<OpAdd_Sse>;
    g_Handlers[0x159] = DispatchWrapper<OpMul_Sse>;
    g_Handlers[0x15C] = DispatchWrapper<OpSub_Sse>;
    g_Handlers[0x154] = DispatchWrapper<OpAnd_Sse>;
    g_Handlers[0x155] = DispatchWrapper<OpAndn_Sse>;
    g_Handlers[0x156] = DispatchWrapper<OpOr_Sse>;
    g_Handlers[0x157] = DispatchWrapper<OpXor_Sse>;
    g_Handlers[0x12E] = DispatchWrapper<OpUcomis_Unified>;  // 0F 2E: UCOMISS / UCOMISD
    g_Handlers[0x151] = DispatchWrapper<OpSqrt_Sse>;        // 0F 51: SQRTPS/PD/SS/SD
    g_Handlers[0x1C6] = DispatchWrapper<OpShuf_Unified>;    // 0F C6: SHUFPS / SHUFPD
    g_Handlers[0x114] = DispatchWrapper<OpUnpckl_Unified>;  // 0F 14: UNPCKLPS / PD
    g_Handlers[0x115] = DispatchWrapper<OpUnpckh_Unified>;  // 0F 15: UNPCKHPS / PD
}

}  // namespace x86emu