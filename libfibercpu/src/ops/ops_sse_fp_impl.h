#pragma once
// SSE/SSE2 Floating Point Operations
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace fiberish {

FORCE_INLINE simde__m128d Helper_CmpPD(simde__m128d a, simde__m128d b, uint8_t pred) {
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

FORCE_INLINE simde__m128d Helper_CmpSD(simde__m128d a, simde__m128d b, uint8_t pred) {
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

FORCE_INLINE simde__m128 Helper_CmpPS(simde__m128 a, simde__m128 b, uint8_t pred) {
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

FORCE_INLINE simde__m128 Helper_CmpSS(simde__m128 a, simde__m128 b, uint8_t pred) {
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

template <bool IsMin>
FORCE_INLINE LogicFlow OpMaxMin_Sse_Impl(LogicFuncParams) {
    // 0F 5F: MAX (PD/SD/PS/SS)
    // 0F 5D: MIN (PD/SD/PS/SS)

    uint8_t reg_idx = (operand_modrm >> 3) & 7;

    if (GetPrefixes(op).flags.repne) {  // F2: Scalar Double
        double b;
        if (operand_modrm >= 0xC0) {
            b = ((double*)&state->ctx.xmm[operand_modrm & 7])[0];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto b_res = ReadMem<double, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!b_res) return LogicFlow::RestartMemoryOp;
            b = *b_res;
        }

        double* dest = (double*)&state->ctx.xmm[reg_idx];  // [0]
        if constexpr (IsMin)
            *dest = (*dest < b) ? *dest : b;
        else
            *dest = (*dest > b) ? *dest : b;
        // High 64 bits unmodified

    } else if (GetPrefixes(op).flags.rep) {  // F3: Scalar Single
        float b;
        if (operand_modrm >= 0xC0) {
            b = ((float*)&state->ctx.xmm[operand_modrm & 7])[0];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto b_res = ReadMem<float, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!b_res) return LogicFlow::RestartMemoryOp;
            b = *b_res;
        }

        float* dest = (float*)&state->ctx.xmm[reg_idx];  // [0]
        if constexpr (IsMin)
            *dest = (*dest < b) ? *dest : b;
        else
            *dest = (*dest > b) ? *dest : b;
        // High 96 bits unmodified

    } else if (GetPrefixes(op).flags.opsize) {  // 66: Packed Double
        auto val_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        simde__m128d a = simde_mm_castps_pd(state->ctx.xmm[reg_idx]);
        simde__m128d b = simde_mm_castps_pd(*val_res);
        // Select b on equality (including signed zero) and unordered inputs,
        // independently of the host SIMD min/max tie-breaking rules.
        simde__m128d mask = IsMin ? simde_mm_cmplt_pd(a, b) : simde_mm_cmpgt_pd(a, b);
        simde__m128d res = simde_mm_or_pd(simde_mm_and_pd(mask, a), simde_mm_andnot_pd(mask, b));
        state->ctx.xmm[reg_idx] = simde_mm_castpd_ps(res);

    } else {  // None: Packed Single
        auto val_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        simde__m128 a = state->ctx.xmm[reg_idx];
        state->ctx.xmm[reg_idx] = IsMin ? simde_mm_min_ps(a, *val_res) : simde_mm_max_ps(a, *val_res);
    }
    return LogicFlow::Continue;
}

namespace op {

template <auto Arithmetic, bool Memory>
FORCE_INLINE LogicFlow OpScalarSingleSse(LogicFuncParams) {
    const uint8_t dest = (operand_modrm >> 3) & 7;
    simde__m128 source;
    if constexpr (Memory) {
        const uint32_t address = ComputeLinearAddress(state, gpr, op);
        auto value = ReadMem<float, OpOnTLBMiss::Restart>(state, gpr, address, utlb, op);
        if (!value) return LogicFlow::RestartMemoryOp;
        source = simde_mm_set_ss(*value);
    } else {
        source = state->ctx.xmm[operand_modrm & 7];
    }
    state->ctx.xmm[dest] = Arithmetic(state->ctx.xmm[dest], source);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAddss_Reg(LogicFuncParams) {
    return OpScalarSingleSse<simde_mm_add_ss, false>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpAddss_Mem(LogicFuncParams) {
    return OpScalarSingleSse<simde_mm_add_ss, true>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMulss_Reg(LogicFuncParams) {
    return OpScalarSingleSse<simde_mm_mul_ss, false>(LogicPassParams);
}
FORCE_INLINE LogicFlow OpMulss_Mem(LogicFuncParams) {
    return OpScalarSingleSse<simde_mm_mul_ss, true>(LogicPassParams);
}

FORCE_INLINE LogicFlow OpAdd_Sse(LogicFuncParams) {
    // 0F 58: ADDPS/ADDPD/ADDSS/ADDSD
    uint8_t reg = (operand_modrm >> 3) & 7;
    uint8_t rm = operand_modrm & 7;

    // Dest is always Register
    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (GetPrefixes(op).flags.opsize) {  // 66: ADDPD (Packed Double)
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_add_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.repne) {  // F2: ADDSD (Scalar Double)
        // Src is 64-bit (Mem) or 128-bit (Reg)
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint64_t mem_val = *mem_res;
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_add_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.rep) {  // F3
        if ((operand_modrm >> 6) == 3) return OpAddss_Reg(LogicPassParams);
        return OpAddss_Mem(LogicPassParams);
    } else {  // None: ADDPS (Packed Single)
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] = simde_mm_add_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSub_Sse(LogicFuncParams) {
    // 0F 5C: SUBPS/SUBPD/SUBSS/SUBSD
    uint8_t reg = (operand_modrm >> 3) & 7;
    uint8_t rm = operand_modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (GetPrefixes(op).flags.opsize) {  // 66: SUBPD
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_sub_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.repne) {  // F2: SUBSD
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint64_t mem_val = *mem_res;
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_sub_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.rep) {  // F3: SUBSS
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint32_t mem_val = *mem_res;
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_sub_ss(dst_val, src_val);
    } else {  // None: SUBPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] = simde_mm_sub_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMul_Sse(LogicFuncParams) {
    // 0F 59: MULPS/MULPD/MULSS/MULSD
    uint8_t reg = (operand_modrm >> 3) & 7;
    uint8_t rm = operand_modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (GetPrefixes(op).flags.opsize) {  // 66: MULPD
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_mul_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.repne) {  // F2: MULSD
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint64_t mem_val = *mem_res;
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_mul_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.rep) {  // F3
        if ((operand_modrm >> 6) == 3) return OpMulss_Reg(LogicPassParams);
        return OpMulss_Mem(LogicPassParams);
    } else {  // None: MULPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] = simde_mm_mul_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpDiv_Sse(LogicFuncParams) {
    // 0F 5E: DIVPS/DIVPD/DIVSS/DIVSD
    uint8_t reg = (operand_modrm >> 3) & 7;
    uint8_t rm = operand_modrm & 7;

    simde__m128 dst_val = state->ctx.xmm[reg];
    simde__m128 src_val;

    if (GetPrefixes(op).flags.opsize) {  // 66: DIVPD
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_div_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.repne) {  // F2: DIVSD
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint64_t mem_val = *mem_res;
            double d_val;
            std::memcpy(&d_val, &mem_val, 8);
            src_val = simde_mm_castpd_ps(simde_mm_set_sd(d_val));
        }
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_div_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else if (GetPrefixes(op).flags.rep) {  // F3: DIVSS
        if ((operand_modrm >> 6) == 3) {
            src_val = state->ctx.xmm[rm];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto mem_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!mem_res) return LogicFlow::RestartMemoryOp;
            uint32_t mem_val = *mem_res;
            float f_val;
            std::memcpy(&f_val, &mem_val, 4);
            src_val = simde_mm_set_ss(f_val);
        }
        state->ctx.xmm[reg] = simde_mm_div_ss(dst_val, src_val);
    } else {  // None: DIVPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        src_val = *src_res;
        state->ctx.xmm[reg] = simde_mm_div_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAnd_Sse(LogicFuncParams) {
    // 0F 54: ANDPS/ANDPD
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128 src_val = *src_res;

    if (GetPrefixes(op).flags.opsize) {  // 66: ANDPD
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_and_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // None (or F2/F3 ignored): ANDPS
        state->ctx.xmm[reg] = simde_mm_and_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpAndn_Sse(LogicFuncParams) {
    // 0F 55: ANDNPS/ANDNPD
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128 src_val = *src_res;

    if (GetPrefixes(op).flags.opsize) {  // 66: ANDNPD
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_andnot_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // None: ANDNPS
        state->ctx.xmm[reg] = simde_mm_andnot_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpOr_Sse(LogicFuncParams) {
    // 0F 56: ORPS/ORPD
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128 src_val = *src_res;

    if (GetPrefixes(op).flags.opsize) {  // 66: ORPD
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_or_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // ORPS
        state->ctx.xmm[reg] = simde_mm_or_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpXor_Sse(LogicFuncParams) {
    // 0F 57: XORPS/XORPD
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];
    auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!src_res) return LogicFlow::RestartMemoryOp;
    simde__m128 src_val = *src_res;

    if (GetPrefixes(op).flags.opsize) {  // 66: XORPD
        state->ctx.xmm[reg] =
            simde_mm_castpd_ps(simde_mm_xor_pd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
    } else {  // XORPS
        state->ctx.xmm[reg] = simde_mm_xor_ps(dst_val, src_val);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpCmp_Sse(LogicFuncParams) {
    uint8_t pred = (uint8_t)imm;
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (GetPrefixes(op).flags.opsize) {
        // 66 0F C2: CMPPD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128d src_pd = simde_mm_castps_pd(*src_res);

        simde__m128d res = Helper_CmpPD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (GetPrefixes(op).flags.repne) {
        // F2 0F C2: CMPSD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d src_pd;

        if ((operand_modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[operand_modrm & 7]);
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src_pd = simde_mm_set_sd(*(double*)&(*val_res));
        }

        simde__m128d res = Helper_CmpSD(dest_pd, src_pd, pred);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (GetPrefixes(op).flags.rep) {
        // F3 0F C2: CMPSS
        simde__m128 src;
        if ((operand_modrm >> 6) == 3) {
            src = state->ctx.xmm[operand_modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src = simde_mm_set_ss(*(float*)&(*val_res));
        }
        *dest_ptr = Helper_CmpSS(*dest_ptr, src, pred);

    } else {
        // 0F C2: CMPPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        *dest_ptr = Helper_CmpPS(*dest_ptr, *src_res, pred);
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_OPS_SSE_FP_IMPL_0
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_OPS_SSE_FP_IMPL_0

FORCE_INLINE LogicFlow OpMovAp_Load(LogicFuncParams) {
    // 0F 28: MOVAPS xmm1, xmm2/m128 (Load/Move)
    auto val_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t reg = (operand_modrm >> 3) & 7;
    state->ctx.xmm[reg] = *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovAp_Store(LogicFuncParams) {
    // 0F 29: MOVAPS xmm2/m128, xmm1 (Store)
    uint8_t reg = (operand_modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry, 16>(state, gpr, op, operand_modrm, val, utlb))
        return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpSqrt_Sse(LogicFuncParams) {
    // 0F 51: SQRTPS
    // 66 0F 51: SQRTPD
    // F2 0F 51: SQRTSD
    // F3 0F 51: SQRTSS

    uint8_t reg = (operand_modrm >> 3) & 7;

    if (GetPrefixes(op).flags.repne) {  // F2: SQRTSD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        simde__m128d src;

        if ((operand_modrm >> 6) == 3) {
            src = simde_mm_castps_pd(state->ctx.xmm[operand_modrm & 7]);
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            double d;
            std::memcpy(&d, &(*val_res), 8);
            src = simde_mm_set_sd(d);
        }

        simde__m128d res = simde_mm_sqrt_sd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);

    } else if (GetPrefixes(op).flags.rep) {  // F3: SQRTSS
        simde__m128 dest = state->ctx.xmm[reg];
        simde__m128 src;

        if ((operand_modrm >> 6) == 3) {
            src = state->ctx.xmm[operand_modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            float f;
            std::memcpy(&f, &(*val_res), 4);
            src = simde_mm_set_ss(f);
        }

        simde__m128 sqrt_val = simde_mm_sqrt_ss(src);
        state->ctx.xmm[reg] = simde_mm_move_ss(dest, sqrt_val);

    } else if (GetPrefixes(op).flags.opsize) {  // 66: SQRTPD
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128d src = simde_mm_castps_pd(*src_res);
        simde__m128d res = simde_mm_sqrt_pd(src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);

    } else {  // None: SQRTPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128 src = *src_res;
        simde__m128 res = simde_mm_sqrt_ps(src);
        state->ctx.xmm[reg] = res;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpUcomis_Unified(LogicFuncParams) {
    // 0F 2E: UCOMISS
    // 66 0F 2E: UCOMISD

    uint8_t reg = (operand_modrm >> 3) & 7;
    uint32_t flags = GetFlags32(flags_cache) & ~(ZF_MASK | PF_MASK | CF_MASK | OF_MASK | AF_MASK | SF_MASK);

    bool is_unordered = false;
    bool is_less = false;
    bool is_equal = false;

    if (GetPrefixes(op).flags.opsize) {  // 66: UCOMISD
        simde__m128d va = simde_mm_castps_pd(state->ctx.xmm[reg]);
        double a = simde_mm_cvtsd_f64(va);
        double b;
        if ((operand_modrm >> 6) == 3) {
            simde__m128d vb = simde_mm_castps_pd(state->ctx.xmm[operand_modrm & 7]);
            b = simde_mm_cvtsd_f64(vb);
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            std::memcpy(&b, &(*val_res), 8);
        }

        if (std::isnan(a) || std::isnan(b))
            is_unordered = true;
        else if (a < b)
            is_less = true;
        else if (a == b)
            is_equal = true;

    } else {  // None: UCOMISS
        float a = simde_mm_cvtss_f32(state->ctx.xmm[reg]);
        float b;
        if ((operand_modrm >> 6) == 3) {
            b = simde_mm_cvtss_f32(state->ctx.xmm[operand_modrm & 7]);
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            std::memcpy(&b, &(*val_res), 4);
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

    SetFlags32AndSyncParityState(flags_cache, flags);
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpComis_Unified(LogicFuncParams) {
    // 0F 2F: COMISS / COMISD
    return OpUcomis_Unified(LogicPassParams);
}

FORCE_INLINE LogicFlow OpRcp_Sse(LogicFuncParams) {
    // 0F 53: RCPPS / RCPSS
    uint8_t reg = (operand_modrm >> 3) & 7;
    if (GetPrefixes(op).flags.rep) {  // F3: RCPSS
        simde__m128 dst = state->ctx.xmm[reg];
        simde__m128 src;
        if ((operand_modrm >> 6) == 3) {
            src = state->ctx.xmm[operand_modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src = simde_mm_set_ss(*(float*)&(*val_res));
        }
        state->ctx.xmm[reg] = simde_mm_move_ss(dst, simde_mm_rcp_ss(src));
    } else {  // RCPPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        state->ctx.xmm[reg] = simde_mm_rcp_ps(*src_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpRsqrt_Sse(LogicFuncParams) {
    // 0F 52: RSQRTPS / RSQRTSS
    uint8_t reg = (operand_modrm >> 3) & 7;
    if (GetPrefixes(op).flags.rep) {  // F3: RSQRTSS
        simde__m128 dst = state->ctx.xmm[reg];
        simde__m128 src;
        if ((operand_modrm >> 6) == 3) {
            src = state->ctx.xmm[operand_modrm & 7];
        } else {
            uint32_t addr = ComputeLinearAddress(state, gpr, op);
            auto val_res = ReadMem<uint32_t, OpOnTLBMiss::Restart>(state, gpr, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src = simde_mm_set_ss(*(float*)&(*val_res));
        }
        state->ctx.xmm[reg] = simde_mm_move_ss(dst, simde_mm_rsqrt_ss(src));
    } else {  // RSQRTPS
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        state->ctx.xmm[reg] = simde_mm_rsqrt_ps(*src_res);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpShuf_Unified(LogicFuncParams) {
    // 0F C6: SHUFPS
    // 66 0F C6: SHUFPD
    uint8_t reg = (operand_modrm >> 3) & 7;

    if (GetPrefixes(op).flags.opsize) {  // 66: SHUFPD
        double* dest_arr = (double*)&state->ctx.xmm[reg];
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        double* src_arr = (double*)&(*src_res);

        double result[2];
        result[0] = dest_arr[(imm & 1)];        // Select from dest
        result[1] = src_arr[((imm >> 1) & 1)];  // Select from src

        std::memcpy(&state->ctx.xmm[reg], result, 16);

    } else {  // None: SHUFPS
        float* dest_arr = (float*)&state->ctx.xmm[reg];
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        float* src_arr = (float*)&(*src_res);

        float result[4];
        result[0] = dest_arr[(imm & 3)];         // Select from dest[0:1]
        result[1] = dest_arr[((imm >> 2) & 3)];  // Select from dest[0:1]
        result[2] = src_arr[((imm >> 4) & 3)];   // Select from src[2:3]
        result[3] = src_arr[((imm >> 6) & 3)];   // Select from src[2:3]

        std::memcpy(&state->ctx.xmm[reg], result, 16);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpUnpckl_Unified(LogicFuncParams) {
    // 0F 14: UNPCKLPS
    // 66 0F 14: UNPCKLPD
    uint8_t reg = (operand_modrm >> 3) & 7;

    if (GetPrefixes(op).flags.opsize) {  // 66: UNPCKLPD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128d src = simde_mm_castps_pd(*src_res);
        simde__m128d res = simde_mm_unpacklo_pd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);
    } else {  // None: UNPCKLPS
        simde__m128 dest = state->ctx.xmm[reg];
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128 src = *src_res;
        simde__m128 res = simde_mm_unpacklo_ps(dest, src);
        state->ctx.xmm[reg] = res;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpUnpckh_Unified(LogicFuncParams) {
    // 0F 15: UNPCKHPS
    // 66 0F 15: UNPCKHPD
    uint8_t reg = (operand_modrm >> 3) & 7;

    if (GetPrefixes(op).flags.opsize) {  // 66: UNPCKHPD
        simde__m128d dest = simde_mm_castps_pd(state->ctx.xmm[reg]);
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128d src = simde_mm_castps_pd(*src_res);
        simde__m128d res = simde_mm_unpackhi_pd(dest, src);
        state->ctx.xmm[reg] = simde_mm_castpd_ps(res);
    } else {  // None: UNPCKHPS
        simde__m128 dest = state->ctx.xmm[reg];
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart, 16>(state, gpr, op, operand_modrm, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128 src = *src_res;
        simde__m128 res = simde_mm_unpackhi_ps(dest, src);
        state->ctx.xmm[reg] = res;
    }
    return LogicFlow::Continue;
}

#define FIBERCPU_BIND_VECTOR_SSE_FP
#include "instruction_kernel_bindings.inc"
#undef FIBERCPU_BIND_VECTOR_SSE_FP

}  // namespace op

}  // namespace fiberish
