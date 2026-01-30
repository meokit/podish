// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

void OpAdd_Sse(EmuState* state, DecodedOp* op) {
    // 0F 58: ADDPS/ADDPD/ADDSS/ADDSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    
    // Dest is always Register
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val;
    
    if (op->prefixes.flags.opsize) { // 66: ADDPD (Packed Double)
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_add_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.repne) { // F2: ADDSD (Scalar Double)
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
            simde_mm_add_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.rep) { // F3: ADDSS (Scalar Single)
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
    } else { // None: ADDPS (Packed Single)
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_add_ps(dst_val, src_val);
    }
}

void OpSub_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5C: SUBPS/SUBPD/SUBSS/SUBSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val;
    
    if (op->prefixes.flags.opsize) { // 66: SUBPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_sub_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.repne) { // F2: SUBSD
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
            simde_mm_sub_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.rep) { // F3: SUBSS
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
    } else { // None: SUBPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_sub_ps(dst_val, src_val);
    }
}

void OpMul_Sse(EmuState* state, DecodedOp* op) {
    // 0F 59: MULPS/MULPD/MULSS/MULSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val;
    
    if (op->prefixes.flags.opsize) { // 66: MULPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_mul_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.repne) { // F2: MULSD
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
            simde_mm_mul_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.rep) { // F3: MULSS
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
    } else { // None: MULPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_mul_ps(dst_val, src_val);
    }
}

void OpDiv_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5E: DIVPS/DIVPD/DIVSS/DIVSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val;
    
    if (op->prefixes.flags.opsize) { // 66: DIVPD
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_div_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.repne) { // F2: DIVSD
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
            simde_mm_div_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.rep) { // F3: DIVSS
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
    } else { // None: DIVPS
        src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = simde_mm_div_ps(dst_val, src_val);
    }
}

void OpAnd_Sse(EmuState* state, DecodedOp* op) {
    // 0F 54: ANDPS/ANDPD
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val = ReadModRM128(state, op);
    
    if (op->prefixes.flags.opsize) { // 66: ANDPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_and_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else { // None (or F2/F3 ignored): ANDPS
        state->ctx.xmm[reg] = simde_mm_and_ps(dst_val, src_val);
    }
}

void OpAndn_Sse(EmuState* state, DecodedOp* op) {
    // 0F 55: ANDNPS/ANDNPD
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128 dst_val = state->ctx.xmm[reg];
    __m128 src_val = ReadModRM128(state, op);
    
    if (op->prefixes.flags.opsize) { // 66: ANDNPD
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_andnot_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else { // None: ANDNPS
        state->ctx.xmm[reg] = simde_mm_andnot_ps(dst_val, src_val);
    }
}

void OpCmp_Sse(EmuState* state, DecodedOp* op) {
    uint8_t pred = (uint8_t)op->imm;
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128* dest_ptr = &state->ctx.xmm[reg];
    
    if (op->prefixes.flags.opsize) {
        // 66 0F C2: CMPPD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        __m128 src = ReadModRM128(state, op);
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
        __m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t val = state->mmu.read<uint32_t>(ComputeEAD(state, op));
            src = simde_mm_set_ss(*(float*)&val);
        }
        *dest_ptr = Helper_CmpSS(*dest_ptr, src, pred);
        
    } else {
        // 0F C2: CMPPS
        __m128 src = ReadModRM128(state, op);
        *dest_ptr = Helper_CmpPS(*dest_ptr, src, pred);
    }
}

void OpMaxMin_Sse(EmuState* state, DecodedOp* op) {
    // 0F 5F: MAX (PD/SD/PS/SS)
    // 0F 5D: MIN (PD/SD/PS/SS)
    // Prefix determines type:
    // None: PS (Packed Single)
    // 66:   PD (Packed Double)
    // F3:   SS (Scalar Single)
    // F2:   SD (Scalar Double)
    
    bool is_min = (op->handler_index & 0xFF) == 0x5D;
    uint8_t reg_idx = (op->modrm >> 3) & 7;
    
    if (op->prefixes.flags.repne) { // F2: Scalar Double
        double b;
        if (op->modrm >= 0xC0) b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        else b = state->mmu.read<double>(ComputeEAD(state, op));
        
        double* dest = (double*)&state->ctx.xmm[reg_idx]; // [0]
        if (is_min) *dest = (*dest < b) ? *dest : b;
        else        *dest = (*dest > b) ? *dest : b;
        // High 64 bits unmodified
        
    } else if (op->prefixes.flags.rep) { // F3: Scalar Single
        float b;
        if (op->modrm >= 0xC0) b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        else b = state->mmu.read<float>(ComputeEAD(state, op));
        
        float* dest = (float*)&state->ctx.xmm[reg_idx]; // [0]
        if (is_min) *dest = (*dest < b) ? *dest : b;
        else        *dest = (*dest > b) ? *dest : b;
        // High 96 bits unmodified
        
    } else if (op->prefixes.flags.opsize) { // 66: Packed Double
        __m128 val = ReadModRM128(state, op);
        simde__m128d a = simde_mm_castps_pd(state->ctx.xmm[reg_idx]);
        simde__m128d b = simde_mm_castps_pd(val);
        simde__m128d res = is_min ? simde_mm_min_pd(a, b) : simde_mm_max_pd(a, b);
        state->ctx.xmm[reg_idx] = simde_mm_castpd_ps(res);
        
    } else { // None: Packed Single
        __m128 val = ReadModRM128(state, op);
        __m128 a = state->ctx.xmm[reg_idx];
        // Using SIMDe directly on __m128 (aliased to simde__m128)
        state->ctx.xmm[reg_idx] = is_min ? simde_mm_min_ps(a, val) : simde_mm_max_ps(a, val);
    }
}

void OpMovAp_Sse(EmuState* state, DecodedOp* op) {
    // 0F 28: MOVAPS xmm1, xmm2/m128 (Load/Move)
    // 0F 29: MOVAPS xmm2/m128, xmm1 (Store)
    // 66 Prefix: MOVAPD (Double) - Same bitwise movement usually, but alignment might differ on real HW.
    // For specialized emulation, we treat them as 128-bit moves.
    
    // Check Direction:
    // 28: Load (Gv, Ev) -> Reg = ModRM
    // 29: Store (Ev, Gv) -> ModRM = Reg
    
    // Note: Handler index will separate 28/29.
    
    uint8_t opcode = op->handler_index & 0xFF; // 28 or 29
    
    if (opcode == 0x28) { // Load
        __m128 val = ReadModRM128(state, op);
        uint8_t reg = (op->modrm >> 3) & 7;
        state->ctx.xmm[reg] = val;
    } else { // Store 0x29
        uint8_t reg = (op->modrm >> 3) & 7;
        __m128 val = state->ctx.xmm[reg];
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
            state->mmu.write<uint64_t>(addr+8, ((uint64_t*)&val)[1]);
        }
    }
}

simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_pd(a, b);
        case 1: return simde_mm_cmplt_pd(a, b);
        case 2: return simde_mm_cmple_pd(a, b);
        case 3: return simde_mm_cmpunord_pd(a, b);
        case 4: return simde_mm_cmpneq_pd(a, b);
        case 5: return simde_mm_cmpnlt_pd(a, b);
        case 6: return simde_mm_cmpnle_pd(a, b);
        case 7: return simde_mm_cmpord_pd(a, b);
    }
    return a;
}

simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_sd(a, b);
        case 1: return simde_mm_cmplt_sd(a, b);
        case 2: return simde_mm_cmple_sd(a, b);
        case 3: return simde_mm_cmpunord_sd(a, b);
        case 4: return simde_mm_cmpneq_sd(a, b);
        case 5: return simde_mm_cmpnlt_sd(a, b);
        case 6: return simde_mm_cmpnle_sd(a, b);
        case 7: return simde_mm_cmpord_sd(a, b);
    }
    return a;
}

simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_ps(a, b);
        case 1: return simde_mm_cmplt_ps(a, b);
        case 2: return simde_mm_cmple_ps(a, b);
        case 3: return simde_mm_cmpunord_ps(a, b);
        case 4: return simde_mm_cmpneq_ps(a, b);
        case 5: return simde_mm_cmpnlt_ps(a, b);
        case 6: return simde_mm_cmpnle_ps(a, b);
        case 7: return simde_mm_cmpord_ps(a, b);
    }
    return a;
}

simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred) {
    switch (pred & 7) {
        case 0: return simde_mm_cmpeq_ss(a, b);
        case 1: return simde_mm_cmplt_ss(a, b);
        case 2: return simde_mm_cmple_ss(a, b);
        case 3: return simde_mm_cmpunord_ss(a, b);
        case 4: return simde_mm_cmpneq_ss(a, b);
        case 5: return simde_mm_cmpnlt_ss(a, b);
        case 6: return simde_mm_cmpnle_ss(a, b);
        case 7: return simde_mm_cmpord_ss(a, b);
    }
    return a;
}

} // namespace x86emu
