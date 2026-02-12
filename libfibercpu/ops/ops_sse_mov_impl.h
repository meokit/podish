#pragma once
// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse3.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"
#include "ops_sse_mov.h"

namespace fiberish {
namespace op {

FORCE_INLINE LogicFlow OpMov_Sse_Load(LogicFuncParams) {
    // 0F 10: MOVUPS/MOVUPD/MOVSS/MOVSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128 dst_val = state->ctx.xmm[reg];

    if (op->prefixes.flags.repne) {  // F2: MOVSD (Load Scalar Double)
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Move low double, preserve high
            simde__m128 src_val = state->ctx.xmm[rm];
            state->ctx.xmm[reg] =
                simde_mm_castpd_ps(simde_mm_move_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val)));
        } else {
            // Mem->Reg: Load double, zero high
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<double, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            double val = *val_res;
            // set_sd sets low double, zeroes high
            state->ctx.xmm[reg] = simde_mm_castpd_ps(simde_mm_set_sd(val));
        }
    } else if (op->prefixes.flags.rep) {  // F3: MOVSS (Load Scalar Single)
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Move low float, preserve high
            simde__m128 src_val = state->ctx.xmm[rm];
            state->ctx.xmm[reg] = simde_mm_move_ss(dst_val, src_val);
        } else {
            // Mem->Reg: Load float, zero high
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<float, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            float val = *val_res;
            // set_ss sets low float, zeroes high
            state->ctx.xmm[reg] = simde_mm_set_ss(val);
        }
    } else {  // (None: MOVUPS) or (66: MOVUPD) -> Load 128
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        state->ctx.xmm[reg] = *src_res;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMov_Sse_Store(LogicFuncParams) {
    // 0F 11: MOVUPS/MOVUPD/MOVSS/MOVSD
    // Op is Store ModRM (Dest) from Reg (Src)
    uint8_t reg = (op->modrm >> 3) & 7;  // This is SRC Reg
    simde__m128 src_val = state->ctx.xmm[reg];

    if (op->prefixes.flags.repne) {  // F2: MOVSD
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Copy low 64 bits, upper unchanged
            uint8_t dst_reg = op->modrm & 7;
            state->ctx.xmm[dst_reg] = simde_mm_castpd_ps(
                simde_mm_move_sd(simde_mm_castps_pd(state->ctx.xmm[dst_reg]), simde_mm_castps_pd(src_val)));
        } else {
            // Reg->Mem: Store 64 bits
            uint32_t addr = ComputeLinearAddress(state, op);
            double val;
            simde_mm_store_sd(&val, simde_mm_castpd_ps(src_val));
            if (!WriteMem<double, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
        }
    } else if (op->prefixes.flags.rep) {  // F3: MOVSS
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Copy low 32 bits
            uint8_t dst_reg = op->modrm & 7;
            state->ctx.xmm[dst_reg] = simde_mm_move_ss(state->ctx.xmm[dst_reg], src_val);
        } else {
            // Reg->Mem: Store 32 bits
            uint32_t addr = ComputeLinearAddress(state, op);
            float val;
            simde_mm_store_ss(&val, src_val);
            if (!WriteMem<float, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
        }
    } else {  // MOVUPS/MOVUPD
        // Store 128
        if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry>(state, op, src_val, utlb)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovd_Load(LogicFuncParams) {
    // 0F 6E: MOVD xmm, r/m32
    // Zero extend to 128
    auto val_res = ReadModRM<uint32_t, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint32_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cvtsi32_si128((int)val));
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovq_Load(LogicFuncParams) {
    // 0F 6F: MOVQ xmm, xmm/m64
    // F3 0F 7E: MOVQ xmm, xmm/m64 (Rep Prefix!)
    // Load 64 bits, zero extend to 128
    uint64_t val;
    if ((op->modrm >> 6) == 3) {
        // Reg->Reg (xmm->xmm low 64)
        uint8_t rm = op->modrm & 7;
        val = ((uint64_t*)&state->ctx.xmm[rm])[0];
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
        if (!val_res) return LogicFlow::RestartMemoryOp;
        val = *val_res;
    }
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[0] = val;
    ptr[1] = 0;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovd_Store(LogicFuncParams) {
    // 0F 7E: MOVD r/m32, xmm
    // F3 0F 7E: MOVQ xmm, xmm/m64 (Load!)
    if (op->prefixes.flags.rep) {
        return OpMovq_Load(LogicPassParams);
    }

    // Store low 32 bits of XMM to r/m32
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    int32_t i_val = simde_mm_cvtsi128_si32(simde_mm_castps_si128(val));
    if (!WriteModRM<uint32_t, OpOnTLBMiss::Retry>(state, op, (uint32_t)i_val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovq_Store(LogicFuncParams) {
    // 0F 7F: MOVQ xmm/m64, xmm
    // Store low 64 bits of XMM to ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];

    if ((op->modrm >> 6) == 3) {
        uint8_t dst_reg = op->modrm & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[dst_reg];
        ptr[0] = val;
        ptr[1] = 0;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovdqa_Load(LogicFuncParams) {
    // 66 0F 6F: MOVDQA xmm, xmm/m128
    auto val_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovdqa_Store(LogicFuncParams) {
    // 66 0F 7F: MOVDQA xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovdqu_Load(LogicFuncParams) {
    // F3 0F 6F: MOVDQU xmm, xmm/m128
    auto val_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = *val_res;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovdqu_Store(LogicFuncParams) {
    // F3 0F 7F: MOVDQU xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovhpd_Load(LogicFuncParams) {
    // 66 0F 16: MOVHPD xmm, m64 (Load)
    uint32_t addr = ComputeLinearAddress(state, op);
    auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[1] = *val_res;  // High
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovhpd_Store(LogicFuncParams) {
    // 66 0F 17: MOVHPD m64, xmm (Store)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
    uint32_t addr = ComputeLinearAddress(state, op);
    if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovhps_Load(LogicFuncParams) {
    // 0F 16: MOVHPS xmm, m64 (Load)
    uint32_t addr = ComputeLinearAddress(state, op);
    auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[1] = *val_res;  // High
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovhps_Store(LogicFuncParams) {
    // 0F 17: MOVHPS m64, xmm (Store)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
    uint32_t addr = ComputeLinearAddress(state, op);
    if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovlpd_Load(LogicFuncParams) {
    // 66 0F 12: MOVLPD xmm, m64 (Load)
    uint32_t addr = ComputeLinearAddress(state, op);
    auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint64_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[0] = val;  // Low
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovlpd_Store(LogicFuncParams) {
    // 66 0F 13: MOVLPD m64, xmm (Store)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
    uint32_t addr = ComputeLinearAddress(state, op);
    if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovlps_Load(LogicFuncParams) {
    // 0F 12: MOVLPS xmm, m64 (Load)
    uint32_t addr = ComputeLinearAddress(state, op);
    auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
    if (!val_res) return LogicFlow::RestartMemoryOp;
    uint64_t val = *val_res;
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[0] = val;  // Low
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovlps_Store(LogicFuncParams) {
    // 0F 13: MOVLPS m64, xmm (Store)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
    uint32_t addr = ComputeLinearAddress(state, op);
    if (!WriteMem<uint64_t, OpOnTLBMiss::Retry>(state, addr, val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpDup_Sse_Lo(LogicFuncParams) {
    // F2 0F 12: MOVDDUP (Load Double Dup, Low->High)
    // F3 0F 12: MOVSLDUP (Load Float Dup, Evn->Odd)

    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.repne) {  // F2: MOVDDUP
        simde__m128d src;
        if (op->modrm >= 0xC0) {
            src = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint32_t addr = ComputeLinearAddress(state, op);
            auto val_res = ReadMem<uint64_t, OpOnTLBMiss::Restart>(state, addr, utlb, op);
            if (!val_res) return LogicFlow::RestartMemoryOp;
            src = simde_mm_set_sd(*(double*)&(*val_res));
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(simde_mm_movedup_pd(src));
    } else {  // F3: MOVSLDUP
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128 src = *src_res;
        state->ctx.xmm[reg] = simde_mm_moveldup_ps(src);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpDup_Sse_Hi(LogicFuncParams) {
    // F3 0F 16: MOVSHDUP (Load Float Dup, Odd->Evn)
    if (op->prefixes.flags.rep) {
        uint8_t reg = (op->modrm >> 3) & 7;
        auto src_res = ReadModRM<simde__m128, OpOnTLBMiss::Restart>(state, op, utlb);
        if (!src_res) return LogicFlow::RestartMemoryOp;
        simde__m128 src = *src_res;
        state->ctx.xmm[reg] = simde_mm_movehdup_ps(src);
    }
    // If not rep, this handler shouldn't be called (OpGroup_Mov16 filters)
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovmsk_Unified(LogicFuncParams) {
    // 0F 50: MOVMSKPS / MOVMSKPD (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    simde__m128 src = state->ctx.xmm[rm];

    if (op->prefixes.flags.opsize) {  // 66: MOVMSKPD
        int mask = simde_mm_movemask_pd(simde_mm_castps_pd(src));
        SetReg(state, reg, (uint32_t)mask);
    } else {  // None: MOVMSKPS
        int mask = simde_mm_movemask_ps(src);
        SetReg(state, reg, (uint32_t)mask);
    }
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovnt_Sse(LogicFuncParams) {
    // 0F 2B: MOVNTPS / MOVNTPD (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 src = state->ctx.xmm[reg];
    if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry>(state, op, src, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovntdq(LogicFuncParams) {
    // 66 0F E7: MOVNTDQ
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    if (!WriteModRM<simde__m128, OpOnTLBMiss::Retry>(state, op, val, utlb)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMovnti(LogicFuncParams) {
    // 0F C3: MOVNTI m32, r32
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t r_val = GetReg(state, reg);
    uint32_t addr = ComputeLinearAddress(state, op);
    if (!WriteMem<uint32_t, OpOnTLBMiss::Retry>(state, addr, r_val, utlb, op)) return LogicFlow::RetryMemoryOp;
    return LogicFlow::Continue;
}

FORCE_INLINE LogicFlow OpMaskmovdqu(LogicFuncParams) {
    // 66 0F F7: MASKMOVDQU xmm1, xmm2
    // Complex Block: fail_on_tlb_miss = false
    simde__m128i val = simde_mm_castps_si128(state->ctx.xmm[(op->modrm >> 3) & 7]);
    simde__m128i mask = simde_mm_castps_si128(state->ctx.xmm[op->modrm & 7]);

    uint32_t addr = GetReg(state, fiberish::EDI);

    alignas(16) uint8_t v[16], m[16];
    std::memcpy(v, &val, 16);
    std::memcpy(m, &mask, 16);

    for (int i = 0; i < 16; ++i) {
        if (m[i] & 0x80) {
            // Blocking write
            // Returns std::unexpected on fatal fault
            if (!WriteMem<uint8_t, OpOnTLBMiss::Blocking>(state, addr + i, v[i], utlb, op)) {
                return LogicFlow::ExitOnCurrentEIP;
            }
        }
    }
    return LogicFlow::Continue;
}

// Groups for 0F 6F/7F etc.
FORCE_INLINE LogicFlow OpGroup_Mov6F(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVDQA
        return OpMovdqa_Load(LogicPassParams);
    } else if (op->prefixes.flags.rep) {  // F3: MOVDQU
        return OpMovdqu_Load(LogicPassParams);
    } else {  // None: MOVQ
        return OpMovq_Load(LogicPassParams);
    }
}

FORCE_INLINE LogicFlow OpGroup_Mov7F(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVDQA
        return OpMovdqa_Store(LogicPassParams);
    } else if (op->prefixes.flags.rep) {  // F3: MOVDQU
        return OpMovdqu_Store(LogicPassParams);
    } else {  // None: MOVQ
        return OpMovq_Store(LogicPassParams);
    }
}

FORCE_INLINE LogicFlow OpGroup_Mov12(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVLPD (Load)
        return OpMovlpd_Load(LogicPassParams);
    } else if (op->prefixes.flags.rep) {  // F3: MOVSLDUP
        return OpDup_Sse_Lo(LogicPassParams);
    } else if (op->prefixes.flags.repne) {  // F2: MOVDDUP
        return OpDup_Sse_Lo(LogicPassParams);
    } else {  // None: MOVLPS (Load)
        return OpMovlps_Load(LogicPassParams);
    }
}

FORCE_INLINE LogicFlow OpGroup_Mov13(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVLPD (Store)
        return OpMovlpd_Store(LogicPassParams);
    } else {  // None: MOVLPS (Store)
        return OpMovlps_Store(LogicPassParams);
    }
}

FORCE_INLINE LogicFlow OpGroup_Mov16(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVHPD (Load)
        return OpMovhpd_Load(LogicPassParams);
    } else if (op->prefixes.flags.rep) {  // F3: MOVSHDUP
        return OpDup_Sse_Hi(LogicPassParams);
    } else {  // None: MOVHPS (Load)
        return OpMovhps_Load(LogicPassParams);
    }
}

FORCE_INLINE LogicFlow OpGroup_Mov17(LogicFuncParams) {
    if (op->prefixes.flags.opsize) {  // 66: MOVHPD (Store)
        return OpMovhpd_Store(LogicPassParams);
    } else {  // None: MOVHPS (Store)
        return OpMovhps_Store(LogicPassParams);
    }
}

}  // namespace op

}  // namespace fiberish
