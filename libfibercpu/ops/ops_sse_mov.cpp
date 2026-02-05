// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#include <simde/x86/sse3.h>

#include "../dispatch.h"
#include "../exec_utils.h"
#include "../ops.h"
#include "../state.h"

namespace x86emu {

static FORCE_INLINE void OpMov_Sse_Load(EmuState* state, DecodedOp* op) {
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
            double val = state->mmu.read<double>(addr);
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
            float val = state->mmu.read<float>(addr);
            // set_ss sets low float, zeroes high
            state->ctx.xmm[reg] = simde_mm_set_ss(val);
        }
    } else {  // (None: MOVUPS) or (66: MOVUPD) -> Load 128
        simde__m128 src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = src_val;
    }
}

static FORCE_INLINE void OpMov_Sse_Store(EmuState* state, DecodedOp* op) {
    // 0F 11: MOVUPS/MOVUPD/MOVSS/MOVSD
    // Op is Store ModRM (Dest) from Reg (Src)
    uint8_t reg = (op->modrm >> 3) & 7;  // This is SRC Reg
    simde__m128 src_val = state->ctx.xmm[reg];

    // Check Dest (ModRM)
    // If Dest is Reg, behavior varies slightly?
    // MOVUPS/D/SS/SD xmm/m, xmm

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
            simde_mm_store_sd(&val, simde_mm_castps_pd(src_val));
            state->mmu.write<double>(addr, val);
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
            state->mmu.write<float>(addr, val);
        }
    } else {  // MOVUPS/MOVUPD
        // Store 128
        WriteModRM128(state, op, src_val);
    }
}

static FORCE_INLINE void OpMovd_Load(EmuState* state, DecodedOp* op) {
    // 0F 6E: MOVD xmm, r/m32
    // Zero extend to 128
    uint32_t val = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    // xmm[reg] = (int)val, rest 0
    state->ctx.xmm[reg] = simde_mm_cvtsi32_si128((int)val);
    // cast to ps is implicit via union? No, simde_mm_cvtsi32_si128 returns
    // simde__m128i Need cast for type safety if we use strictly typed logic,
    // checking binding... In simde/common.h, types might be compatible or require
    // cast. Let's use generic cast
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cvtsi32_si128((int)val));
}

static FORCE_INLINE void OpMovq_Load(EmuState* state, DecodedOp* op) {
    // 0F 6F: MOVQ xmm, xmm/m64
    // F3 0F 7E: MOVQ xmm, xmm/m64 (Rep Prefix!)
    // Load 64 bits, zero extend to 128
    uint64_t val;
    if ((op->modrm >> 6) == 3) {
        // Reg->Reg (xmm->xmm low 64)
        uint8_t rm = op->modrm & 7;
        // Read low 64
        val = ((uint64_t*)&state->ctx.xmm[rm])[0];
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        val = state->mmu.read<uint64_t>(addr);
    }
    uint8_t reg = (op->modrm >> 3) & 7;
    // Set 64 bits low, 0 high.
    // simde_mm_cvtsi64_si128 (x64 only?)
    // Manual set?
    uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
    ptr[0] = val;
    ptr[1] = 0;
}

static FORCE_INLINE void OpMovd_Store(EmuState* state, DecodedOp* op) {
    // 0F 7E: MOVD r/m32, xmm
    // F3 0F 7E: MOVQ xmm, xmm/m64 (Load!)
    if (op->prefixes.flags.rep) {
        OpMovq_Load(state, op);
        return;
    }

    // Store low 32 bits of XMM to r/m32
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    int32_t i_val = simde_mm_cvtsi128_si32(simde_mm_castps_si128(val));
    WriteModRM32(state, op, (uint32_t)i_val);
}

static FORCE_INLINE void OpMovq_Store(EmuState* state, DecodedOp* op) {
    // 0F 7F: MOVQ xmm/m64, xmm
    // Store low 64 bits of XMM to ModRM
    uint8_t reg = (op->modrm >> 3) & 7;
    uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];

    if ((op->modrm >> 6) == 3) {
        uint8_t dst_reg = op->modrm & 7;
        // Store low 64, zero high 64 of dest?
        // MOVQ xmm1, xmm2 clears upper 64 bits of Dest.
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[dst_reg];
        ptr[0] = val;
        ptr[1] = 0;
    } else {
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

static FORCE_INLINE void OpMovdqa_Load(EmuState* state, DecodedOp* op) {
    // 66 0F 6F: MOVDQA xmm, xmm/m128
    // Should check alignment if strict.
    simde__m128 val = ReadModRM128(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = val;
}

static FORCE_INLINE void OpMovdqa_Store(EmuState* state, DecodedOp* op) {
    // 66 0F 7F: MOVDQA xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    WriteModRM128(state, op, val);
}

static FORCE_INLINE void OpMovdqu_Load(EmuState* state, DecodedOp* op) {
    // F3 0F 6F: MOVDQU xmm, xmm/m128
    simde__m128 val = ReadModRM128(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = val;
}

static FORCE_INLINE void OpMovdqu_Store(EmuState* state, DecodedOp* op) {
    // F3 0F 7F: MOVDQU xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    WriteModRM128(state, op, val);
}

static FORCE_INLINE void OpMovhpd(EmuState* state, DecodedOp* op) {
    // 66 0F 16: MOVHPD xmm, m64 (Load)
    // 66 0F 17: MOVHPD m64, xmm (Store)
    // 16: extra=6, 17: extra=7

    if (op->extra == 6) {  // Load
        // Load m64 to Dest[127:64]
        uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[1] = val;  // High
    } else {           // Store 0x17
        // Store Dest[127:64] to m64
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

static FORCE_INLINE void OpMovhps(EmuState* state, DecodedOp* op) {
    // 0F 16: MOVHPS xmm, m64 (Load)
    // 0F 17: MOVHPS m64, xmm (Store)

    if (op->extra == 6) {  // Load
        uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[1] = val;  // High
    } else {           // Store 0x17
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

static FORCE_INLINE void OpMovlpd(EmuState* state, DecodedOp* op) {
    // 66 0F 12: MOVLPD xmm, m64 (Load)
    // 66 0F 13: MOVLPD m64, xmm (Store)

    if (op->extra == 2) {  // Load 12
        uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[0] = val;  // Low
    } else {           // Store 13
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

static FORCE_INLINE void OpMovlps(EmuState* state, DecodedOp* op) {
    // 0F 12: MOVLPS xmm, m64 (Load)
    // 0F 13: MOVLPS m64, xmm (Store)

    if (op->extra == 2) {  // Load 12
        uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[0] = val;  // Low
    } else {           // Store 13
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
        uint32_t addr = ComputeLinearAddress(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

static FORCE_INLINE void OpDup_Sse(EmuState* state, DecodedOp* op) {
    // F3 0F 12: MOVSLDUP, F2 0F 12: MOVDDUP, F3 0F 16: MOVSHDUP
    uint8_t reg = (op->modrm >> 3) & 7;

    if (op->prefixes.flags.repne) {  // F2: MOVDDUP
        simde__m128d src;
        if (op->modrm >= 0xC0) {
            src = simde_mm_castps_pd(state->ctx.xmm[op->modrm & 7]);
        } else {
            uint64_t val = state->mmu.read<uint64_t>(ComputeLinearAddress(state, op));
            src = simde_mm_set_sd(*(double*)&val);
        }
        state->ctx.xmm[reg] = simde_mm_castpd_ps(simde_mm_movedup_pd(src));
    } else {  // F3: MOVSLDUP / MOVSHDUP
        simde__m128 src = ReadModRM128(state, op);
        // 12: extra=2, 16: extra=6
        if (op->extra == 2) {
            state->ctx.xmm[reg] = simde_mm_moveldup_ps(src);
        } else {  // 0x16
            state->ctx.xmm[reg] = simde_mm_movehdup_ps(src);
        }
    }
}

static FORCE_INLINE void OpMovmsk_Unified(EmuState* state, DecodedOp* op) {
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
}

static FORCE_INLINE void OpMovnt_Sse(EmuState* state, DecodedOp* op) {
    // 0F 2B: MOVNTPS / MOVNTPD (66)
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 src = state->ctx.xmm[reg];
    WriteModRM128(state, op, src);
}

static FORCE_INLINE void OpMovntdq(EmuState* state, DecodedOp* op) {
    // 66 0F E7: MOVNTDQ
    uint8_t reg = (op->modrm >> 3) & 7;
    simde__m128 val = state->ctx.xmm[reg];
    WriteModRM128(state, op, val);
}

static FORCE_INLINE void OpMovnti(EmuState* state, DecodedOp* op) {
    // 0F C3: MOVNTI m32, r32
    uint8_t reg = (op->modrm >> 3) & 7;
    uint32_t r_val = GetReg(state, reg);
    uint32_t addr = ComputeLinearAddress(state, op);
    state->mmu.write<uint32_t>(addr, r_val);
}

static FORCE_INLINE void OpMaskmovdqu(EmuState* state, DecodedOp* op) {
    // 66 0F F7: MASKMOVDQU xmm1, xmm2
    simde__m128i val = simde_mm_castps_si128(state->ctx.xmm[(op->modrm >> 3) & 7]);
    simde__m128i mask = simde_mm_castps_si128(state->ctx.xmm[op->modrm & 7]);

    uint32_t addr = GetReg(state, x86emu::EDI);

    alignas(16) uint8_t v[16], m[16];
    std::memcpy(v, &val, 16);
    std::memcpy(m, &mask, 16);

    for (int i = 0; i < 16; ++i) {
        if (m[i] & 0x80) {
            state->mmu.write<uint8_t>(addr + i, v[i]);
        }
    }
}

// Groups for 0F 6F/7F etc.
static FORCE_INLINE void OpGroup_Mov6F(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVDQA
        OpMovdqa_Load(state, op);
    } else if (op->prefixes.flags.rep) {  // F3: MOVDQU
        OpMovdqu_Load(state, op);
    } else {  // None: MOVQ
        OpMovq_Load(state, op);
    }
}

static FORCE_INLINE void OpGroup_Mov7F(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVDQA
        OpMovdqa_Store(state, op);
    } else if (op->prefixes.flags.rep) {  // F3: MOVDQU
        OpMovdqu_Store(state, op);
    } else {  // None: MOVQ
        OpMovq_Store(state, op);
    }
}

static FORCE_INLINE void OpGroup_Mov12(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVLPD
        OpMovlpd(state, op);
    } else if (op->prefixes.flags.rep) {  // F3: MOVSLDUP
        OpDup_Sse(state, op);
    } else if (op->prefixes.flags.repne) {  // F2: MOVDDUP
        OpDup_Sse(state, op);
    } else {  // None: MOVLPS
        OpMovlps(state, op);
    }
}

static FORCE_INLINE void OpGroup_Mov13(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVLPD (Store)
        OpMovlpd(state, op);
    } else {  // None: MOVLPS (Store)
        OpMovlps(state, op);
    }
}

static FORCE_INLINE void OpGroup_Mov16(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVHPD
        OpMovhpd(state, op);
    } else if (op->prefixes.flags.rep) {  // F3: MOVSHDUP
        OpDup_Sse(state, op);
    } else {  // None: MOVHPS
        OpMovhps(state, op);
    }
}

static FORCE_INLINE void OpGroup_Mov17(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) {  // 66: MOVHPD (Store)
        OpMovhpd(state, op);
    } else {  // None: MOVHPS (Store)
        OpMovhps(state, op);
    }
}

void RegisterSseMovOps() {
    g_Handlers[0x110] = DispatchWrapper<OpMov_Sse_Load>;
    g_Handlers[0x111] = DispatchWrapper<OpMov_Sse_Store>;
    g_Handlers[0x112] = DispatchWrapper<OpGroup_Mov12>;
    g_Handlers[0x113] = DispatchWrapper<OpGroup_Mov13>;
    g_Handlers[0x116] = DispatchWrapper<OpGroup_Mov16>;
    g_Handlers[0x117] = DispatchWrapper<OpGroup_Mov17>;
    g_Handlers[0x150] = DispatchWrapper<OpMovmsk_Unified>;
    g_Handlers[0x12B] = DispatchWrapper<OpMovnt_Sse>;
    g_Handlers[0x1E7] = DispatchWrapper<OpMovntdq>;
    g_Handlers[0x1C3] = DispatchWrapper<OpMovnti>;
    g_Handlers[0x1F7] = DispatchWrapper<OpMaskmovdqu>;
    g_Handlers[0x16E] = DispatchWrapper<OpMovd_Load>;
    g_Handlers[0x17E] = DispatchWrapper<OpMovd_Store>;
    g_Handlers[0x16F] = DispatchWrapper<OpGroup_Mov6F>;
    g_Handlers[0x17F] = DispatchWrapper<OpGroup_Mov7F>;
    g_Handlers[0x1D6] = DispatchWrapper<OpMovq_Store>;  // 66 0F D6: MOVQ xmm/m64, xmm
}

}  // namespace x86emu