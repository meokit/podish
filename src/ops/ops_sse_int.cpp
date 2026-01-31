#include "ops_sse_int.h"
#include "../exec_utils.h"
#include <simde/x86/sse2.h>
#include <cstring>

namespace x86emu {

// Logical
void OpPand_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_and_si128(dst, src));
}

void OpPandn_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_andnot_si128(dst, src));
}

void OpPor_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_or_si128(dst, src));
}

void OpPxor_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_xor_si128(dst, src));
}

// Arithmetic
void OpPaddb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi8(dst, src));
}

void OpPaddw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi16(dst, src));
}

void OpPaddd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi32(dst, src));
}

void OpPaddq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_add_epi64(dst, src));
}

void OpPsubb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi8(dst, src));
}

void OpPsubw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi16(dst, src));
}

void OpPsubd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi32(dst, src));
}

void OpPsubq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sub_epi64(dst, src));
}

void OpPmuludq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_mul_epu32(dst, src));
}

// Comparison
void OpPcmpeqb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi8(dst, src));
}

void OpPcmpeqw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi16(dst, src));
}

void OpPcmpeqd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpeq_epi32(dst, src));
}

void OpPcmpgtb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi8(dst, src));
}

void OpPcmpgtw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi16(dst, src));
}

void OpPcmpgtd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_cmpgt_epi32(dst, src));
}

// Max/Min
void OpPmaxub_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epu8(dst, src));
}

void OpPminub_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epu8(dst, src));
}

void OpPmaxsw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_max_epi16(dst, src));
}

void OpPminsw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_min_epi16(dst, src));
}

// Shifts
// Shift operations have ModRM/Imm encoding differences.
// Group 12/13/14 (0F 71/72/73) use ModRM.reg as opcode extension.
// But some are also separate opcodes like PSLLW/D/Q (Register/Memory form).
// The handler will be dispatched based on the primary opcode usually.
// For Group opcodes, the dispatch table handles .reg.
// For now, these handlers assume they are called for the correct operation.

void OpPsllw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    // Source can be Imm8 (if Group) or XMM/Mem (if Opcode)
    // If we map 0F 71 /2 to this, Src is Imm8
    // If we map 0F F1 to this, Src is XMM/Mem
    __m128i count;
    if (op->handler_index == 0x1F1) { // PSLLW xmm, xmm/m128
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    } else { // Group 0F 71 /6
        count = simde_mm_set_epi64x(0, op->imm);
    }
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_sll_epi16(dst, count));
}

void OpPslld_Sse(EmuState* state, DecodedOp* op) {
    // uint8_t reg = (op->modrm >> 3) & 7; // Dest is ModRM.rm if Group? NO.
    // Wait. For Group 0F 72 /6 (PSLLD xmm, imm8), ModRM.rm is Dest (Register).
    // For 0F F2 (PSLLD xmm, xmm/m128), ModRM.reg is Dest.
    
    __m128i dst;
    __m128i count;
    uint8_t dst_idx;

    // Check opcode to distinguish
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x72) { // Group 13
        // ModRM.reg is Opcode Ext (6)
        // Dest is ModRM.rm (must be register for SSE integer shifts mostly? No, instructions are xmm, imm8)
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F F2
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sll_epi32(dst, count));
}

void OpPsllq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x73) { // Group 14 /6
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F F3
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sll_epi64(dst, count));
}

void OpPsraw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x71) { // Group 12 /4
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F E1
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sra_epi16(dst, count));
}

void OpPsrad_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x72) { // Group 13 /4
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F E2
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_sra_epi32(dst, count));
}

void OpPsrlw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x71) { // Group 12 /2
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F D1
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi16(dst, count));
}

void OpPsrld_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x72) { // Group 13 /2
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F D2
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi32(dst, count));
}

void OpPsrlq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t dst_idx;
    __m128i dst, count;
    uint8_t opcode = op->handler_index & 0xFF;
    
    if (opcode == 0x73) { // Group 14 /2
        dst_idx = op->modrm & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_set_epi64x(0, op->imm);
    } else { // 0F D3
        dst_idx = (op->modrm >> 3) & 7;
        dst = simde_mm_castps_si128(state->ctx.xmm[dst_idx]);
        count = simde_mm_castps_si128(ReadModRM128(state, op));
    }
    state->ctx.xmm[dst_idx] = simde_mm_castsi128_ps(simde_mm_srl_epi64(dst, count));
}

void OpPslldq_Sse(EmuState* state, DecodedOp* op) {
    // 0F 73 /7: PSLLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    __m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm = (uint8_t)op->imm;
    
    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);
    
    if (imm >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift left (move to higher index)
        for (int i = 15; i >= 0; --i) {
            if (i >= imm) bytes[i] = bytes[i - imm];
            else bytes[i] = 0;
        }
    }
    
    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
}

void OpPsrldq_Sse(EmuState* state, DecodedOp* op) {
    // 0F 73 /3: PSRLDQ xmm, imm8
    uint8_t reg = op->modrm & 7;
    __m128i dst_val = simde_mm_castps_si128(state->ctx.xmm[reg]);
    uint8_t imm = (uint8_t)op->imm;
    
    uint8_t bytes[16];
    std::memcpy(bytes, &dst_val, 16);
    
    if (imm >= 16) {
        std::memset(bytes, 0, 16);
    } else {
        // Shift right (move to lower index)
        for (int i = 0; i < 16; ++i) {
            if (i + imm < 16) bytes[i] = bytes[i + imm];
            else bytes[i] = 0;
        }
    }
    
    std::memcpy(&state->ctx.xmm[reg], bytes, 16);
}

// Shuffle/Pack/Unpack
void OpPshufd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    uint8_t imm = (uint8_t)op->imm;
    
    // We must manually implement shuffle because _mm_shuffle_epi32 requires const immediate
    int32_t* s = (int32_t*)&src;
    int32_t res[4];
    res[0] = s[(imm >> 0) & 3];
    res[1] = s[(imm >> 2) & 3];
    res[2] = s[(imm >> 4) & 3];
    res[3] = s[(imm >> 6) & 3];
    
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_setr_epi32(res[0], res[1], res[2], res[3]));
}

void OpPshufhw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    uint8_t imm = (uint8_t)op->imm;
    
    int16_t* s = (int16_t*)&src;
    int16_t res[8];
    // Low words (0-3) copied
    res[0] = s[0]; res[1] = s[1]; res[2] = s[2]; res[3] = s[3];
    // High words (4-7) shuffled
    res[4] = s[4 + ((imm >> 0) & 3)];
    res[5] = s[4 + ((imm >> 2) & 3)];
    res[6] = s[4 + ((imm >> 4) & 3)];
    res[7] = s[4 + ((imm >> 6) & 3)];
    
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const __m128i*)res));
}

void OpPshuflw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    uint8_t imm = (uint8_t)op->imm;
    
    int16_t* s = (int16_t*)&src;
    int16_t res[8];
    // Low words (0-3) shuffled
    res[0] = s[((imm >> 0) & 3)];
    res[1] = s[((imm >> 2) & 3)];
    res[2] = s[((imm >> 4) & 3)];
    res[3] = s[((imm >> 6) & 3)];
    // High words (4-7) copied
    res[4] = s[4]; res[5] = s[5]; res[6] = s[6]; res[7] = s[7];
    
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_loadu_si128((const __m128i*)res));
}

void OpPunpckhbw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi8(dst, src));
}

void OpPunpckhwd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi16(dst, src));
}

void OpPunpckhdq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi32(dst, src));
}

void OpPunpckhqdq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpackhi_epi64(dst, src));
}

void OpPunpcklbw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi8(dst, src));
}

void OpPunpcklwd_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi16(dst, src));
}

void OpPunpckldq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi32(dst, src));
}

void OpPunpcklqdq_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_unpacklo_epi64(dst, src));
}

void OpPacksswb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi16(dst, src));
}

void OpPackssdw_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packs_epi32(dst, src));
}

void OpPackuswb_Sse(EmuState* state, DecodedOp* op) {
    uint8_t reg = (op->modrm >> 3) & 7;
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    __m128i src = simde_mm_castps_si128(ReadModRM128(state, op));
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(simde_mm_packus_epi16(dst, src));
}

// Move / Extraction / Insertion
void OpPextrw_Sse(EmuState* state, DecodedOp* op) {
    // PEXTRW reg32, xmm, imm8 (0F C5)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    __m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);
    uint8_t imm = (uint8_t)op->imm & 7;
    
    uint16_t val = 0;
    switch (imm) {
        case 0: val = simde_mm_extract_epi16(src, 0); break;
        case 1: val = simde_mm_extract_epi16(src, 1); break;
        case 2: val = simde_mm_extract_epi16(src, 2); break;
        case 3: val = simde_mm_extract_epi16(src, 3); break;
        case 4: val = simde_mm_extract_epi16(src, 4); break;
        case 5: val = simde_mm_extract_epi16(src, 5); break;
        case 6: val = simde_mm_extract_epi16(src, 6); break;
        case 7: val = simde_mm_extract_epi16(src, 7); break;
    }
    state->ctx.regs[reg] = (uint32_t)val;
}

void OpPinsrw_Sse(EmuState* state, DecodedOp* op) {
    // PINSRW xmm, r32/m16, imm8 (0F C4)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t imm = (uint8_t)op->imm & 7;
    
    int val; // simde expects int
    if (op->modrm >= 0xC0) {
        val = (int)(uint16_t)state->ctx.regs[op->modrm & 7];
    } else {
        val = (int)state->mmu.read<uint16_t>(ComputeEAD(state, op));
    }
    
    __m128i dst = simde_mm_castps_si128(state->ctx.xmm[reg]);
    switch (imm) {
        case 0: dst = simde_mm_insert_epi16(dst, val, 0); break;
        case 1: dst = simde_mm_insert_epi16(dst, val, 1); break;
        case 2: dst = simde_mm_insert_epi16(dst, val, 2); break;
        case 3: dst = simde_mm_insert_epi16(dst, val, 3); break;
        case 4: dst = simde_mm_insert_epi16(dst, val, 4); break;
        case 5: dst = simde_mm_insert_epi16(dst, val, 5); break;
        case 6: dst = simde_mm_insert_epi16(dst, val, 6); break;
        case 7: dst = simde_mm_insert_epi16(dst, val, 7); break;
    }
    state->ctx.xmm[reg] = simde_mm_castsi128_ps(dst);
}

void OpPmovmskb_Sse(EmuState* state, DecodedOp* op) {
    // PMOVMSKB reg32, xmm (0F D7)
    uint8_t reg = (op->modrm >> 3) & 7;
    uint8_t rm = op->modrm & 7;
    __m128i src = simde_mm_castps_si128(state->ctx.xmm[rm]);
    
    int mask = simde_mm_movemask_epi8(src);
    state->ctx.regs[reg] = (uint32_t)mask;
}

// Groups
void OpGroup_Pshuf(EmuState* state, DecodedOp* op) {
    // 0F 70
    if (op->prefixes.flags.opsize) { // 66: PSHUFD
        OpPshufd_Sse(state, op);
    } else if (op->prefixes.flags.repne) { // F2: PSHUFLW
        OpPshuflw_Sse(state, op);
    } else if (op->prefixes.flags.rep) { // F3: PSHUFHW
        OpPshufhw_Sse(state, op);
    } else {
        // PSHUFW (MMX) - Not implemented
        // OpUd2(state, op);
    }
}

void OpGroup_Sse_Shift_Imm_W(EmuState* state, DecodedOp* op) {
    // 0F 71 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2: OpPsrlw_Sse(state, op); break;
        case 4: OpPsraw_Sse(state, op); break;
        case 6: OpPsllw_Sse(state, op); break;
        default: break; // #UD
    }
}

void OpGroup_Sse_Shift_Imm_D(EmuState* state, DecodedOp* op) {
    // 0F 72 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2: OpPsrld_Sse(state, op); break;
        case 4: OpPsrad_Sse(state, op); break;
        case 6: OpPslld_Sse(state, op); break;
        default: break;
    }
}

void OpGroup_Sse_Shift_Imm_Q(EmuState* state, DecodedOp* op) {
    // 0F 73 /r
    uint8_t sub = (op->modrm >> 3) & 7;
    switch (sub) {
        case 2: OpPsrlq_Sse(state, op); break;
        case 3: OpPsrldq_Sse(state, op); break;
        case 6: OpPsllq_Sse(state, op); break;
        case 7: OpPslldq_Sse(state, op); break;
        default: break;
    }
}

} // namespace x86emu
