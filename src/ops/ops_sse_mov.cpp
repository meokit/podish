// SSE/SSE2 Data Movement
// Auto-generated from ops.cpp refactoring

#include "../ops.h"
#include "../state.h"
#include "../exec_utils.h"
#include <simde/x86/sse.h>

namespace x86emu {

void OpMov_Sse_Load(EmuState* state, DecodedOp* op) {
    // 0F 10: MOVUPS/MOVUPD/MOVSS/MOVSD
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    __m128 dst_val = state->ctx.xmm[reg];
    
    if (op->prefixes.flags.repne) { // F2: MOVSD (Load Scalar Double)
        if ((op->modrm >> 6) == 3) {
             // Reg->Reg: Move low double, preserve high
             __m128 src_val = state->ctx.xmm[rm];
             state->ctx.xmm[reg] = simde_mm_castpd_ps(
                 simde_mm_move_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
             );
        } else {
             // Mem->Reg: Load double, zero high
             uint32_t addr = ComputeEAD(state, op);
             double val = state->mmu.read<double>(addr);
             // set_sd sets low double, zeroes high
             state->ctx.xmm[reg] = simde_mm_castpd_ps(simde_mm_set_sd(val));
        }
    } else if (op->prefixes.flags.rep) { // F3: MOVSS (Load Scalar Single)
        if ((op->modrm >> 6) == 3) {
             // Reg->Reg: Move low float, preserve high
             __m128 src_val = state->ctx.xmm[rm];
             state->ctx.xmm[reg] = simde_mm_move_ss(dst_val, src_val);
        } else {
             // Mem->Reg: Load float, zero high
             uint32_t addr = ComputeEAD(state, op);
             float val = state->mmu.read<float>(addr);
             // set_ss sets low float, zeroes high
             state->ctx.xmm[reg] = simde_mm_set_ss(val);
        }
    } else { // (None: MOVUPS) or (66: MOVUPD) -> Load 128
        __m128 src_val = ReadModRM128(state, op);
        state->ctx.xmm[reg] = src_val;
    }
}

void OpMov_Sse_Store(EmuState* state, DecodedOp* op) {
    // 0F 11: MOVUPS/MOVUPD/MOVSS/MOVSD
    // Op is Store ModRM (Dest) from Reg (Src)
    uint8_t reg = (op->modrm >> 3) & 7; // This is SRC Reg
    __m128 src_val = state->ctx.xmm[reg];
    
    // Check Dest (ModRM)
    // If Dest is Reg, behavior varies slightly?
    // MOVUPS/D/SS/SD xmm/m, xmm
    
    if (op->prefixes.flags.repne) { // F2: MOVSD
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Copy low 64 bits, upper unchanged
             uint8_t dst_reg = op->modrm & 7;
             state->ctx.xmm[dst_reg] = simde_mm_castpd_ps(
                 simde_mm_move_sd(simde_mm_castps_pd(state->ctx.xmm[dst_reg]), simde_mm_castps_pd(src_val))
             );
        } else {
            // Reg->Mem: Store 64 bits
            uint32_t addr = ComputeEAD(state, op);
            double val;
            simde_mm_store_sd(&val, simde_mm_castps_pd(src_val));
            state->mmu.write<double>(addr, val);
        }
    } else if (op->prefixes.flags.rep) { // F3: MOVSS
        if ((op->modrm >> 6) == 3) {
            // Reg->Reg: Copy low 32 bits
             uint8_t dst_reg = op->modrm & 7;
             state->ctx.xmm[dst_reg] = simde_mm_move_ss(state->ctx.xmm[dst_reg], src_val);
        } else {
            // Reg->Mem: Store 32 bits
            uint32_t addr = ComputeEAD(state, op);
            float val;
            simde_mm_store_ss(&val, src_val);
            state->mmu.write<float>(addr, val);
        }
    } else { // MOVUPS/MOVUPD
        // Store 128
        WriteModRM128(state, op, src_val);
    }
}

void OpMovd_Load(EmuState* state, DecodedOp* op) {
    // 0F 6E: MOVD xmm, r/m32
    // Zero extend to 128
    uint32_t val = ReadModRM32(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    // xmm[reg] = (int)val, rest 0
    state->ctx.xmm[reg] = simde_mm_cvtsi32_si128((int)val); 
    // cast to ps is implicit via union? No, simde_mm_cvtsi32_si128 returns __m128i
    // Need cast for type safety if we use strictly typed logic, checking binding...
    // In simde/common.h, types might be compatible or require cast.
    // Let's use generic cast
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cvtsi32_si128((int)val));
}

void OpMovd_Store(EmuState* state, DecodedOp* op) {
    // 0F 7E: MOVD r/m32, xmm
    // F3 0F 7E: MOVQ xmm, xmm/m64 (Load!)
    if (op->prefixes.flags.rep) {
        OpMovq_Load(state, op);
        return;
    }
    
    // Store low 32 bits of XMM to r/m32
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128 val = state->ctx.xmm[reg];
    int32_t i_val = simde_mm_cvtsi128_si32(simde_mm_castps_si128(val));
    WriteModRM32(state, op, (uint32_t)i_val);
}

void OpMovq_Load(EmuState* state, DecodedOp* op) {
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
         uint32_t addr = ComputeEAD(state, op);
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

void OpMovq_Store(EmuState* state, DecodedOp* op) {
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
         uint32_t addr = ComputeEAD(state, op);
         state->mmu.write<uint64_t>(addr, val);
    }
}

void OpMovdqa_Load(EmuState* state, DecodedOp* op) {
    // 66 0F 6F: MOVDQA xmm, xmm/m128
    // Should check alignment if strict.
    __m128 val = ReadModRM128(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = val;
}

void OpMovdqa_Store(EmuState* state, DecodedOp* op) {
    // 66 0F 7F: MOVDQA xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128 val = state->ctx.xmm[reg];
    WriteModRM128(state, op, val);
}

void OpMovdqu_Load(EmuState* state, DecodedOp* op) {
    // F3 0F 6F: MOVDQU xmm, xmm/m128
    __m128 val = ReadModRM128(state, op);
    uint8_t reg = (op->modrm >> 3) & 7;
    state->ctx.xmm[reg] = val;
}

void OpMovdqu_Store(EmuState* state, DecodedOp* op) {
    // F3 0F 7F: MOVDQU xmm/m128, xmm
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128 val = state->ctx.xmm[reg];
    WriteModRM128(state, op, val);
}

void OpMovhpd(EmuState* state, DecodedOp* op) {
    // 66 0F 16: MOVHPD xmm, m64 (Load)
    // 66 0F 17: MOVHPD m64, xmm (Store) -- Wait, Opcode 17 is Store?
    // Handler mapping handles direction?
    // Usually 16 is Load (to Reg), 17 is Store (from Reg).
    // Standard: 66 0F 16 /r: MOVHPD xmm1, m64.
    // 66 0F 17 /r: MOVHPD m64, xmm1.
    // I need distinct handlers or check opcode.
    
    // Check opcode
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x16) { // Load
        // Load m64 to Dest[127:64]
        uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[1] = val; // High
    } else { // Store 0x17
        // Store Dest[127:64] to m64
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

void OpMovhps(EmuState* state, DecodedOp* op) {
    // 0F 16: MOVHPS xmm, m64 (Load)
    // 0F 17: MOVHPS m64, xmm (Store)
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x16) { // Load
        uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[1] = val; // High
    } else { // Store 0x17
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[1];
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

void OpMovlpd(EmuState* state, DecodedOp* op) {
    // 66 0F 12: MOVLPD xmm, m64 (Load)
    // 66 0F 13: MOVLPD m64, xmm (Store)
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x12) { // Load
        uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[0] = val; // Low
    } else { // Store 0x13
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

void OpMovlps(EmuState* state, DecodedOp* op) {
    // 0F 12: MOVLPS xmm, m64 (Load)
    // 0F 13: MOVLPS m64, xmm (Store)
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x12) { // Load
        uint64_t val = state->mmu.read<uint64_t>(ComputeEAD(state, op));
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t* ptr = (uint64_t*)&state->ctx.xmm[reg];
        ptr[0] = val; // Low
    } else { // Store 0x13
        uint8_t reg = (op->modrm >> 3) & 7;
        uint64_t val = ((uint64_t*)&state->ctx.xmm[reg])[0];
        uint32_t addr = ComputeEAD(state, op);
        state->mmu.write<uint64_t>(addr, val);
    }
}

void OpMovmskps(EmuState* state, DecodedOp* op) {
    // 0F 50: MOVMSKPS r32, xmm
    uint8_t reg = (op->modrm >> 3) & 7; // Dest Reg
    uint8_t rm = op->modrm & 7; // Src XMM
    __m128 src = state->ctx.xmm[rm];
    
    int mask = simde_mm_movemask_ps(src);
    SetReg(state, reg, (uint32_t)mask);
}

// Groups for 0F 6F/7F etc.
void OpGroup_Mov6F(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVDQA
        OpMovdqa_Load(state, op);
    } else if (op->prefixes.flags.rep) { // F3: MOVDQU
        OpMovdqu_Load(state, op);
    } else { // None: MOVQ
        OpMovq_Load(state, op);
    }
}

void OpGroup_Mov7F(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVDQA
        OpMovdqa_Store(state, op);
    } else if (op->prefixes.flags.rep) { // F3: MOVDQU
        OpMovdqu_Store(state, op);
    } else { // None: MOVQ
        OpMovq_Store(state, op);
    }
}

void OpGroup_Mov12(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVLPD
        OpMovlpd(state, op);
    } else { // None: MOVLPS (or F2: MOVDDUP?)
        // TODO: MOVDDUP (F2) check?
        OpMovlps(state, op);
    }
}

void OpGroup_Mov13(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVLPD (Store)
        OpMovlpd(state, op);
    } else { // None: MOVLPS (Store)
        OpMovlps(state, op);
    }
}

void OpGroup_Mov16(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVHPD
        OpMovhpd(state, op);
    } else { // None: MOVHPS (or F3: MOVSHDUP?)
        OpMovhps(state, op);
    }
}

void OpGroup_Mov17(EmuState* state, DecodedOp* op) {
    if (op->prefixes.flags.opsize) { // 66: MOVHPD (Store)
        OpMovhpd(state, op);
    } else { // None: MOVHPS (Store)
        OpMovhps(state, op);
    }
}

} // namespace x86emu
