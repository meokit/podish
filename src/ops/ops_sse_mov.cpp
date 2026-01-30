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
        // Dest[63:0] = Src[63:0], Dest[127:64] Unchanged
        __m128 src_val;
        if ((op->modrm >> 6) == 3) {
             // Reg->Reg: Move low double
             src_val = state->ctx.xmm[rm];
        } else {
             // Mem->Reg: Load double
             uint32_t addr = ComputeEAD(state, op);
             double val = state->mmu.read<double>(addr);
             src_val = simde_mm_castpd_ps(simde_mm_set_sd(val));
        }
        // Use move_sd logic: dest, src -> dest_low replaced by src_low
        state->ctx.xmm[reg] = simde_mm_castpd_ps(
            simde_mm_move_sd(simde_mm_castps_pd(dst_val), simde_mm_castps_pd(src_val))
        );
    } else if (op->prefixes.flags.rep) { // F3: MOVSS (Load Scalar Single)
        // Dest[31:0] = Src[31:0], Dest[127:32] Unchanged
        __m128 src_val;
        if ((op->modrm >> 6) == 3) {
             src_val = state->ctx.xmm[rm];
        } else {
             uint32_t addr = ComputeEAD(state, op);
             float val = state->mmu.read<float>(addr);
             src_val = simde_mm_set_ss(val);
        }
        state->ctx.xmm[reg] = simde_mm_move_ss(dst_val, src_val);
    } else { // (None: MOVUPS) or (66: MOVUPD) -> Load 128
        // For Load from Mem, we just read 128
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

} // namespace x86emu
