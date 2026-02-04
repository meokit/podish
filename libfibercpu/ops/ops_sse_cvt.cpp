// SSE/SSE2 Type Conversions
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpCvt_2A(EmuState* state, DecodedOp* op) {
    // 0F 2A: CVTPI2PS (MMX) / CVTSI2SS (F3) / CVTSI2SD (F2)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    int32_t val = (int32_t)ReadModRM32(state, op);

    if (op->prefixes.flags.repne) {
        // F2: CVTSI2SD
        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtsi32_sd(dest_pd, val);
        *dest_ptr = simde_mm_castpd_ps(res);
    } else if (op->prefixes.flags.rep) {
        // F3: CVTSI2SS
        *dest_ptr = simde_mm_cvtsi32_ss(*dest_ptr, val);
    } else {
        OpUd2(state, op);
    }
}

static FORCE_INLINE void OpCvt_2C(EmuState* state, DecodedOp* op) {
    // 2C: CVTTSD2SI (F2), CVTTSS2SI (F3)
    uint32_t addr = 0;
    if (op->modrm < 0xC0) addr = ComputeLinearAddress(state, op);
    int32_t res = 0;

    if (op->prefixes.flags.repne) {  // F2: CVTTSD2SI (Double -> Int32)
        double b;
        if (op->modrm >= 0xC0)
            b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<double>(addr);
        // Truncate
        res = (int32_t)b;
    } else if (op->prefixes.flags.rep) {  // F3: CVTTSS2SI (Single -> Int32)
        float b;
        if (op->modrm >= 0xC0)
            b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<float>(addr);
        res = (int32_t)b;
    } else {
        OpUd2(state, op);
        return;
    }
    SetReg(state, (op->modrm >> 3) & 7, (uint32_t)res);
}

static FORCE_INLINE void OpCvt_2D(EmuState* state, DecodedOp* op) {
    // 0F 2D: CVTSD2SI (F2) / CVTSS2SI (F3)
    uint32_t addr = 0;
    if (op->modrm < 0xC0) addr = ComputeLinearAddress(state, op);
    int32_t res = 0;

    if (op->prefixes.flags.repne) {  // F2: CVTSD2SI
        double b;
        if (op->modrm >= 0xC0)
            b = ((double*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<double>(addr);

        // TODO: Sync MXCSR rounding mode to host
        res = simde_mm_cvtsd_si32(simde_mm_set_sd(b));
    } else if (op->prefixes.flags.rep) {  // F3: CVTSS2SI
        float b;
        if (op->modrm >= 0xC0)
            b = ((float*)&state->ctx.xmm[op->modrm & 7])[0];
        else
            b = state->mmu.read<float>(addr);

        // TODO: Sync MXCSR rounding mode to host
        res = simde_mm_cvtss_si32(simde_mm_set_ss(b));
    } else {
        OpUd2(state, op);
        return;
    }
    SetReg(state, (op->modrm >> 3) & 7, (uint32_t)res);
}

static FORCE_INLINE void OpCvt_5A(EmuState* state, DecodedOp* op) {
    // 0F 5A: CVTPS2PD / CVTPD2PS (66) / CVTSD2SS (F2) / CVTSS2SD (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (op->prefixes.flags.opsize) {
        // 66: CVTPD2PS (xmm/m128 -> xmm)
        simde__m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);
        *dest_ptr = simde_mm_cvtpd_ps(src_pd);

    } else if (op->prefixes.flags.repne) {
        // F2: CVTSD2SS (xmm/m64 -> xmm)
        simde__m128d src_pd;
        if ((op->modrm >> 6) == 3) {
            src_pd = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
            src_pd = simde_mm_set_sd(*(double*)&val);
        }
        *dest_ptr = simde_mm_cvtsd_ss(*dest_ptr, src_pd);

    } else if (op->prefixes.flags.rep) {
        // F3: CVTSS2SD (xmm/m32 -> xmm)
        simde__m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint32_t val = state->mmu.read<uint32_t>(ComputeLinearAddress(state, op));
            src = simde_mm_set_ss(*(float*)&val);
        }

        simde__m128d dest_pd = simde_mm_castps_pd(*dest_ptr);
        simde__m128d res = simde_mm_cvtss_sd(dest_pd, src);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else {
        // 0F: CVTPS2PD (xmm/m64 -> xmm)
        simde__m128 src;
        if ((op->modrm >> 6) == 3) {
            src = state->ctx.xmm[op->modrm & 7];
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
            uint32_t v0 = val & 0xFFFFFFFF;
            uint32_t v1 = val >> 32;
            src = simde_mm_set_ps(0.0f, 0.0f, *(float*)&v1, *(float*)&v0);
        }
        simde__m128d res = simde_mm_cvtps_pd(src);
        *dest_ptr = simde_mm_castpd_ps(res);
    }
}

static FORCE_INLINE void OpCvt_5B(EmuState* state, DecodedOp* op) {
    // 0F 5B: CVTDQ2PS / CVTPS2DQ (66) / CVTTPS2DQ (F3)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];
    simde__m128 src = ReadModRM128(state, op);

    if (op->prefixes.flags.opsize) {
        // 66: CVTPS2DQ
        simde__m128i res = simde_mm_cvtps_epi32(src);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else if (op->prefixes.flags.rep) {
        // F3: CVTTPS2DQ (Truncate)
        simde__m128i res = simde_mm_cvttps_epi32(src);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else {
        // 0F: CVTDQ2PS
        simde__m128i isrc = simde_mm_castps_si128(src);
        *dest_ptr = simde_mm_cvtepi32_ps(isrc);
    }
}

static FORCE_INLINE void OpCvt_E6(EmuState* state, DecodedOp* op) {
    // 0F E6: CVTPD2DQ (F2) / CVTDQ2PD (F3) / CVTTPD2DQ (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128* dest_ptr = &state->ctx.xmm[reg];

    if (op->prefixes.flags.opsize) {
        // 66: CVTTPD2DQ (Truncate xmm/m128 -> xmm)
        simde__m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);

        simde__m128i res = simde_mm_cvttpd_epi32(src_pd);
        *dest_ptr = simde_mm_castsi128_ps(res);

    } else if (op->prefixes.flags.rep) {
        // F3: CVTDQ2PD (xmm/m64 -> xmm)
        simde__m128i isrc;
        if ((op->modrm >> 6) == 3) {
            // Register: cast float to int
            simde__m128 src = state->ctx.xmm[op->modrm & 7];
            isrc = simde_mm_castps_si128(src);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
            uint32_t v0 = val & 0xFFFFFFFF;
            uint32_t v1 = val >> 32;
            isrc = simde_mm_set_epi32(0, 0, v1, v0);
        }

        simde__m128d res = simde_mm_cvtepi32_pd(isrc);
        *dest_ptr = simde_mm_castpd_ps(res);

    } else if (op->prefixes.flags.repne) {
        // F2: CVTPD2DQ (xmm/m128 -> xmm)
        simde__m128 src = ReadModRM128(state, op);
        simde__m128d src_pd = simde_mm_castps_pd(src);

        simde__m128i res = simde_mm_cvtpd_epi32(src_pd);
        *dest_ptr = simde_mm_castsi128_ps(res);
    } else {
        OpUd2(state, op);
    }
}

void RegisterSseCvtOps() {
    g_Handlers[0x12A] = DispatchWrapper<OpCvt_2A>;
    g_Handlers[0x15A] = DispatchWrapper<OpCvt_5A>;
    g_Handlers[0x15B] = DispatchWrapper<OpCvt_5B>;
    g_Handlers[0x1E6] = DispatchWrapper<OpCvt_E6>;
    g_Handlers[0x12C] = DispatchWrapper<OpCvt_2C>;
    g_Handlers[0x12D] = DispatchWrapper<OpCvt_2D>;
}

}  // namespace x86emu