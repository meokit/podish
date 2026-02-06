#include "decoder.h"
#include <cstdio>
#include <cstring>
#include "decoder_lut.h"
#include "dispatch.h"
#include "exec_utils.h"  // For Flag Masks
#include "ops.h"         // For g_Handlers
#include "state.h"

namespace x86emu {

alignas(64) static const uint8_t kControlFlowMaps[2][32] = {
    // Map 0: Primary opcodes (Jcc short, CALL, JMP, RET, LOOP, INT, HLT, etc.)
    {0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF,
     0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x0C, 0xFC, 0x00, 0x00, 0x0F, 0x0F, 0x10, 0x80},
    // Map 1: 0x0F prefixed opcodes (Jcc near, SYSCALL, SYSENTER)
    {0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
     0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00}};

// Helper to determine Immediate Size (in bytes) based on Blink Type and
// Prefixes
static int GetImmLength(uint8_t type, const DecodedOp* op) {
    bool osz = op->prefixes.flags.opsize;  // 0x66

    switch (type) {
        case 0:
            return -1;  // Error
        case 1:
            return 0;  // None
        case 3:        // Group 3 Byte (TEST=1, others 0) / Group 11 Byte (MOV=1)
        {
            uint8_t reg = (op->modrm >> 3) & 7;
            // 0xF6/0xC6: Only Reg 0 (TEST/MOV) and 1 (TEST reserved) have Imm8
            return (reg <= 1) ? 1 : 0;
        }
        case 4:  // Group 3 Word/Dword (TEST=1, others 0) / Group 11 (MOV=1)
        {
            uint8_t reg = (op->modrm >> 3) & 7;
            // 0xF7/0xC7: Only Reg 0 and 1 have Imm
            return (reg <= 1) ? (osz ? 2 : 4) : 0;
        }
        case 5:
            return 1;  // Byte Signed (really just 1 byte)
        case 6:
            return osz ? 2 : 4;  // v (Word or Dword) Same as 7 for 32-bit
        case 7:
            return osz ? 2 : 4;  // v (Word or Dword)
        case 8:
            return 2;  // Word
        case 9:
            return 1;  // Byte
        case 10:
            return osz ? 2 : 4;  // v (Word or Dword)
        case 11:
            return 3;  // uimm0(2) + uimm1(1) (ENTER)
        default:
            return 0;
    }
}

// Decoder Logic
// Returns true on success, false on failure/invalid instruction
bool DecodeInstruction(const uint8_t* code, DecodedOp* op) {
    // Reset op
    std::memset(op, 0, sizeof(DecodedOp));
    op->prefixes.flags.ea_base = 8;
    op->prefixes.flags.ea_index = 8;

    const uint8_t* start = code;
    const uint8_t* ptr = code;

    // 1. Legacy Prefixes
    bool prefix_done = false;
    while (!prefix_done) {
        uint8_t b = *ptr;
        switch (b) {
            // Group 1: Lock / REP
            case 0xF0:
                op->prefixes.flags.lock = 1;
                break;
            case 0xF2:
                op->prefixes.flags.repne = 1;
                break;
            case 0xF3:
                op->prefixes.flags.rep = 1;
                break;

            // Group 2: Segment Overrides
            case 0x2E:
                op->prefixes.flags.segment = 2;
                break;  // CS
            case 0x36:
                op->prefixes.flags.segment = 3;
                break;  // SS
            case 0x3E:
                op->prefixes.flags.segment = 4;
                break;  // DS
            case 0x26:
                op->prefixes.flags.segment = 1;
                break;  // ES
            case 0x64:
                op->prefixes.flags.segment = 5;
                break;  // FS
            case 0x65:
                op->prefixes.flags.segment = 6;
                break;  // GS

            // Group 3: Op Size
            case 0x66:
                op->prefixes.flags.opsize = 1;
                break;

            // Group 4: Addr Size
            case 0x67:
                op->prefixes.flags.addrsize = 1;
                break;

            default:
                prefix_done = true;
                continue;  // Do not increment ptr for non-prefix
        }
        ptr++;
        if (ptr - start > 14) return false;
    }

    // 2. Opcode Dispatch
    uint8_t map = 0;
    uint8_t opcode = *ptr;

    if (opcode == 0x0F) {
        map = 1;
        ptr++;
        opcode = *ptr;  // Next byte is opcode
    }
    ptr++;

    // Set Handler Index (Map 0 or 1) - Local, not stored in op
    uint16_t handler_index = (map << 8) | opcode;

    // 3. ModRM
    uint8_t has_modrm = kHasModRM[map][opcode];
    if (has_modrm) {
        // ... (existing logic) ...
        op->meta.flags.has_modrm = 1;
        uint8_t modrm = *ptr++;
        op->modrm = modrm;

        uint8_t mod = (modrm >> 6) & 3;
        uint8_t rm = modrm & 7;

        // SIB? (If Mod != 3 and RM == 4)
        uint8_t sib_byte = 0;
        if (mod != 3 && rm == 4) {
            op->meta.flags.has_sib = 1;
            sib_byte = *ptr++;
        }

        // Displacement?
        uint8_t disp_size = 0;
        if (mod == 0) {
            if (rm == 5) disp_size = 4;  // Disp32 (EBP replaced by Disp32)
        } else if (mod == 1) {
            disp_size = 1;  // Disp8
        } else if (mod == 2) {
            disp_size = 4;  // Disp32
        }

        // SIB Base Special Case?
        if (op->meta.flags.has_sib) {
            uint8_t base = sib_byte & 7;
            if (mod == 0 && base == 5) {
                disp_size = 4;  // Disp32 (Mod=0, Base=5 -> Disp32, no Base)
            }
        }

        if (disp_size > 0) {
            op->meta.flags.has_disp = 1;
            if (disp_size == 1) {
                int8_t d8 = (int8_t)*ptr;
                op->disp = (uint32_t)(int32_t)d8;  // Sign extend!
                ptr += 1;
            } else {
                op->disp = *reinterpret_cast<const uint32_t*>(ptr);
                ptr += 4;
            }
        }

        // Pre-calculate EA components for faster execution
        if (op->meta.flags.has_sib) {
            uint8_t scale = (sib_byte >> 6) & 3;
            uint8_t index = (sib_byte >> 3) & 7;
            uint8_t base_reg = sib_byte & 7;

            if (index != 4) op->prefixes.flags.ea_index = index;
            op->meta.flags.ea_shift = scale;

            if (mod == 0 && base_reg == 5) {
                // Base is None (Disp32) - already initialized to 8
            } else {
                op->prefixes.flags.ea_base = base_reg;
            }
        } else {
            // No SIB
            if (mod == 0 && rm == 5) {
                // Base is None (Disp32) - already initialized to 8
            } else {
                op->prefixes.flags.ea_base = rm;
            }
        }
    } else {
        // No ModRM: Use modrm field to store opcode low bits (for Reg index in
        // INC/DEC/PUSH/POP)
        op->modrm = opcode & 7;
    }

    // Store extra info (Condition Code or Low Opcode bits) in op->extra (4 bits)
    op->extra = opcode & 0xF;

    // 4. Immediate
    uint8_t imm_type = kImmType[map][opcode];
    int imm_len = GetImmLength(imm_type, op);

    // Override for MOV moffs (A0-A3) - Address Size determines Imm Size
    if (map == 0 && (opcode >= 0xA0 && opcode <= 0xA3)) {
        if (op->prefixes.flags.addrsize)
            imm_len = 2;
        else
            imm_len = 4;
    }

    if (imm_len > 0) {
        op->meta.flags.has_imm = 1;
        if (imm_len == 1)
            op->imm = *reinterpret_cast<const uint8_t*>(ptr);
        else if (imm_len == 2)
            op->imm = *reinterpret_cast<const uint16_t*>(ptr);
        else if (imm_len == 4)
            op->imm = *reinterpret_cast<const uint32_t*>(ptr);
        else if (imm_len == 3) {
            // Special Case for ENTER (Iw Ib)
            uint16_t iw = *reinterpret_cast<const uint16_t*>(ptr);
            uint8_t ib = *(ptr + 2);
            op->imm = iw | (ib << 16);
        }
        ptr += imm_len;
    }

    op->length = (ptr - start);

    // Rule 1: REP/REPNE prefixes always terminate blocks as they can loop (modify
    // EIP)
    bool is_rep = op->prefixes.flags.rep || op->prefixes.flags.repne;

    // Rule 2: Table-based lookup for branching opcodes
    // Access bitmap using map & 1 to ensure safe indexing.
    // Filter map > 1 later to ensure strict correctness.
    bool is_map_match = (kControlFlowMaps[map & 1][opcode >> 3] >> (opcode & 7)) & 1;

    // Rule 3: Handle Group 5 (0xFF) Special Case
    // Only sub-opcodes 2 (CALL), 3 (CALLF), 4 (JMP), 5 (JMPF) are control flow.
    // Use __builtin_expect to hint that 0xFF is less common than other
    // instructions.
    if (__builtin_expect((opcode == 0xFF) && (map == 0), 0)) {
        // Optimized range check: (reg - 2) <= 3 is equivalent to (reg >= 2 && reg
        // <= 5)
        uint8_t reg = (op->modrm >> 3) & 7;
        is_map_match = ((uint8_t)(reg - 2) <= 3);
    }

    // Combine results and apply map safety filter (ensures 0x0F 0x0F maps don't
    // false positive)
    op->meta.flags.is_control_flow = is_rep | (is_map_match & (map <= 1));

    // Store extra info (Condition Code or Low Opcode bits) in op->extra (4 bits)
    // Useful for Jcc (70-7F, 0F 80-8F), Setcc (0F 90-9F), CMOVcc (0F 40-4F)
    // All these have condition code in low 4 bits of opcode.
    op->extra = opcode & 0xF;

    // Final Step: Calculate Relative Handler Offset
    // Specialized 32-bit MOV (0x89, 0x8B) - No Legacy Prefixes
    // Important: We only check legacy prefixes (lock, rep, segment, opsize, addrsize) 
    // which are in the low 8 bits of prefixes.all.
    if (map == 0 && (op->prefixes.all & 0xFF) == 0) {
        if (opcode == 0x89) {
            uint8_t mod = (op->modrm >> 6) & 3;
            handler_index = (mod == 3) ? OP_MOV_RR_STORE : OP_MOV_RM_STORE;
        } else if (opcode == 0x8B) {
            uint8_t mod = (op->modrm >> 6) & 3;
            handler_index = (mod == 3) ? OP_MOV_RR_LOAD : OP_MOV_MR_LOAD;
        }
    }

    op->opcode = handler_index;
    HandlerFunc h = g_Handlers[handler_index];
    if (h) {
        op->handler_offset = (int32_t)((intptr_t)h - (intptr_t)g_HandlerBase);
    } else {
        // Fallback to UD2
        HandlerFunc ud2 = g_Handlers[0x10B];
        op->handler_offset = (int32_t)((intptr_t)ud2 - (intptr_t)g_HandlerBase);
    }

    return true;
}

// Helper to check if opcode is Control Flow
static bool IsControlFlow(const DecodedOp* op) { return op->meta.flags.is_control_flow; }

bool DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts, BasicBlock* block) {
    block->start_eip = start_eip;
    block->ops.clear();
    block->inst_count = 0;

    uint32_t current_eip = start_eip;
    uint64_t effective_limit = 128;
    if (max_insts > 0 && max_insts < 128) effective_limit = max_insts;

    // Temporary storage for indices to support DFE (since DecodedOp doesn't store
    // them anymore)
    std::vector<uint16_t> op_indices;
    op_indices.reserve(effective_limit);

    // Page boundary check helper
    auto is_page_cross = [](uint32_t start, uint32_t len) {
        return (start & 0xFFFFF000) != ((start + len - 1) & 0xFFFFF000);
    };

    while (block->ops.size() < effective_limit) {
        // 0. Check Limit
        if (limit_eip != 0 && current_eip >= limit_eip) {
            break;
        }
        // 1. Fetch
        // Read instruction bytes safely
        uint8_t buf[16];
        bool fetch_fault = false;
        for (int i = 0; i < 16; ++i) {
            buf[i] = state->mmu.read_for_exec<uint8_t>(current_eip + i);
            if (state->status != EmuStatus::Running) {
                fetch_fault = true;
                break;
            }
        }

        if (fetch_fault) {
            if (block->ops.empty()) return false;  // First instruction failed
            break;                                 // Terminate block
        }

        DecodedOp op;
        if (!DecodeInstruction(buf, &op)) {
            // Decode error: Insert Fault Op
            printf(
                "[DecodeBlock] DecodeInstruction Failed at %08X. Bytes: %02X %02X "
                "%02X %02X\n",
                current_eip, buf[0], buf[1], buf[2], buf[3]);
            std::memset(&op, 0, sizeof(op));
            op.length = 0;  // Fault: EIP points to instruction

            HandlerFunc ud2 = g_Handlers[0x10B];  // UD2
            op.handler_offset = (int32_t)((intptr_t)ud2 - (intptr_t)g_HandlerBase);

            block->ops.push_back(op);

            // Append Sentinel for dispatch safety
            DecodedOp sentinel;
            std::memset(&sentinel, 0, sizeof(sentinel));

            HandlerFunc exit_h = g_ExitHandlers[0];
            sentinel.handler_offset = (int32_t)((intptr_t)exit_h - (intptr_t)g_HandlerBase);

            block->ops.push_back(sentinel);

            block->end_eip = current_eip + 1;
            return true;  // Return true to execute what we have (including the fault)
        }

        // Recover index logic (to keep op_indices in sync)
        uint8_t map = 0;
        uint8_t opcode = buf[0];
        // Handle prefixes correctly to find opcode
        const uint8_t* ptr = buf;
        while (true) {
            uint8_t b = *ptr;
            if (b == 0xF0 || b == 0xF2 || b == 0xF3 || b == 0x2E || b == 0x36 || b == 0x3E || b == 0x26 || b == 0x64 ||
                b == 0x65 || b == 0x66 || b == 0x67) {
                ptr++;
                continue;
            }
            break;
        }
        opcode = *ptr;
        if (opcode == 0x0F) {
            map = 1;
            ptr++;
            opcode = *ptr;
        }
        op_indices.push_back((map << 8) | opcode);

        // Add to block
        block->ops.push_back(op);
        block->inst_count++;
        uint32_t inst_len = op.length;

        // Stop if Control Flow
        if (IsControlFlow(&op)) {
            current_eip += inst_len;
            break;
        }

        // Advance
        current_eip += inst_len;

#if 1
        // --- CMP + Jcc Fusion ---
        // If the current instruction is a CMP (various forms) and the NEXT is a Jcc,
        // we can fuse them into a single handler that does both.
        if (block->inst_count > 0 && !block->ops.empty()) {
            DecodedOp& cmp_op = block->ops.back();
            uint16_t cmp_idx = op_indices.back();
            uint8_t cmp_map = (cmp_idx >> 8) & 0xFF;
            uint8_t cmp_opcode = cmp_idx & 0xFF;

            SpecializedOp fusion_type = (SpecializedOp)0;
            if (cmp_map == 0 && !cmp_op.prefixes.flags.opsize) {
                if (cmp_opcode == 0x39 || cmp_opcode == 0x3B) {
                    uint8_t mod = (cmp_op.modrm >> 6) & 3;
                    if (mod == 3) {
                        fusion_type = OP_FUSED_CMP_RR_JCC;
                    } else {
                        // RM or MR fusion only if displacement is not used, so we can use disp/imm freely?
                        // Actually RM/MR specialized ops use ReadModRM32, which uses disp if present.
                        // So we can only fuse if disp is NOT used by the CMP.
                        if (!cmp_op.meta.flags.has_disp) {
                            fusion_type = (cmp_opcode == 0x3B) ? OP_FUSED_CMP_RM_JCC : OP_FUSED_CMP_MR_JCC;
                        }
                    }
                } else if ((cmp_opcode == 0x81 || cmp_opcode == 0x83) && ((cmp_op.modrm >> 3) & 7) == 7) {
                    // Group 1 CMP
                    if ((cmp_op.modrm >> 6) == 3) {
                        fusion_type = OP_FUSED_CMP_RI_JCC;
                    } else if (!cmp_op.meta.flags.has_disp) {
                        // CMP [reg], imm
                        fusion_type = OP_FUSED_CMP_RI8_JCC;
                    }
                } else if (cmp_opcode == 0x3C) {
                    // CMP AL, imm8
                    fusion_type = OP_FUSED_CMP_AL_I8_JCC;
                } else if (cmp_opcode == 0x80 && ((cmp_op.modrm >> 3) & 7) == 7) {
                    // CMP r/m8, imm8. Fuse if no displacement.
                    if (!cmp_op.meta.flags.has_disp) {
                        fusion_type = OP_FUSED_CMP_I8I8_JCC;
                    }
                } else if (cmp_opcode == 0x85) {
                    // TEST r/m32, r32. Fuse if no displacement (to use imm for Jcc)
                    // Actually we can use imm even if there is displacement.
                    fusion_type = ((cmp_op.modrm >> 6) == 3) ? OP_FUSED_TEST_RR_JCC : OP_FUSED_TEST_RM_JCC;
                } else if (cmp_opcode == 0x84) {
                    // TEST r/m8, r8
                    fusion_type = ((cmp_op.modrm >> 6) == 3) ? OP_FUSED_TEST_I8I8_RR_JCC : OP_FUSED_TEST_I8I8_RM_JCC;
                }
            }

            if (fusion_type != 0) {
                // Peek next instruction
                uint8_t next_buf[16];
                bool fetch_ok = true;
                for (int i = 0; i < 16; ++i) {
                    next_buf[i] = state->mmu.read_for_exec<uint8_t>(current_eip + i);
                    if (state->status != EmuStatus::Running) {
                        fetch_ok = false;
                        break;
                    }
                }

                if (fetch_ok) {
                    DecodedOp jcc_op;
                    if (DecodeInstruction(next_buf, &jcc_op)) {
                        uint8_t j_opcode = next_buf[0];
                        uint8_t j_map = 0;
                        const uint8_t* j_ptr = next_buf;
                        while (*j_ptr == 0x66 || *j_ptr == 0x67) j_ptr++;  // Skip prefixes
                        j_opcode = *j_ptr;
                        if (j_opcode == 0x0F) {
                            j_map = 1;
                            j_opcode = *(j_ptr + 1);
                        }

                        bool is_jcc = false;
                        if (j_map == 0 && j_opcode >= 0x70 && j_opcode <= 0x7F)
                            is_jcc = true;
                        else if (j_map == 1 && j_opcode >= 0x80 && j_opcode <= 0x8F)
                            is_jcc = true;

                        if (is_jcc) {
                            // FUSE!
                            cmp_op.meta.flags.is_fused = 1;
                            cmp_op.meta.flags.is_control_flow = 1;

                            int32_t jcc_rel = 0;
                            if (j_map == 0) {
                                jcc_rel = (int32_t)(int8_t)(jcc_op.imm & 0xFF);
                            } else {
                                jcc_rel = (int32_t)jcc_op.imm;
                            }

                            // Store Jcc target.
                            // Types that use imm for CMP immediate must use disp for Jcc target.
                            if (fusion_type == OP_FUSED_CMP_RI_JCC || fusion_type == OP_FUSED_CMP_RI8_JCC ||
                                fusion_type == OP_FUSED_CMP_AL_I8_JCC || fusion_type == OP_FUSED_CMP_I8I8_JCC) {
                                cmp_op.disp = (uint32_t)jcc_rel;
                            } else {
                                cmp_op.imm = (uint32_t)jcc_rel;
                            }
                            cmp_op.extra = j_opcode & 0xF;

                            uint32_t total_len = cmp_op.length + jcc_op.length;
                            if (total_len <= 15) {
                                cmp_op.length = total_len;
                                current_eip += jcc_op.length;

                                // Update Handler to Fused variant
                                HandlerFunc fused_h = g_Handlers[fusion_type];
                                cmp_op.handler_offset = (int32_t)((intptr_t)fused_h - (intptr_t)g_HandlerBase);
                            } else {
                                cmp_op.meta.flags.is_fused = 0;
                                cmp_op.meta.flags.is_control_flow = 0;
                            }
                        }
                    }
                }
            }
        }
#endif

        // Stop if Control Flow
        if (IsControlFlow(&block->ops.back())) {
            break;
        }

        // Stop if Page Cross
        // (Execution engine might need to re-check TLB)
        if (is_page_cross(current_eip, 1)) {
            break;
        }
    }

    // Append Sentinel Op to terminate Threaded Dispatch
    DecodedOp sentinel;
    std::memset(&sentinel, 0, sizeof(sentinel));

    // Select exit handler based on last opcode to reduce BTB pressure
    uint8_t last_opcode = 0;
    if (!op_indices.empty()) {
        last_opcode = op_indices.back() & 0xFF;
    }
    HandlerFunc exit_h = g_ExitHandlers[last_opcode % 16];
    sentinel.handler_offset = (int32_t)((intptr_t)exit_h - (intptr_t)g_HandlerBase);

    block->ops.push_back(sentinel);

#if 1
    // Optimization: Dead Flag Elimination (Backward Pass)
    // We analyze the def-use chain of EFLAGS within the block.
    // If an instruction writes flags that are never used (or overwritten),
    // we switch it to its No-Flags (NF) handler variant.

    // Initial live_flags = all flags (assume live at block exit)
    uint32_t live_flags = 0xFFFFFFFF;

    // Iterate backwards (skipping the sentinel)
    // op_indices corresponds to block->ops[0...N-1]. Sentinel is at N.
    for (int i = (int)block->ops.size() - 2; i >= 0; --i) {
        DecodedOp& op = block->ops[i];
        if (i >= (int)op_indices.size()) break;  // Should not happen

        uint16_t h_idx = op_indices[i];
        uint8_t map = (h_idx >> 8) & 1;
        uint8_t opcode = h_idx & 0xFF;

        // Define flag masks (simplified for analysis)
        constexpr uint32_t ALL_FLAGS = CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK;
        uint32_t writes = 0;
        uint32_t reads = 0;

        // [Safety] Never optimize fused instructions - they have special handlers
        if (op.meta.flags.is_fused) {
            // Fused ops (e.g., CMP+Jcc) kill flags and handle jumps internally.
            // They act as producers, so we reset live_flags.
            live_flags = 0;
            continue;
        }

        // 1. Determine Read/Write sets for common ALU opcodes
        if (map == 0) {
            // Check ranges first
            if (opcode >= 0x40 && opcode <= 0x4F) {
                // INC / DEC (Writes all except CF)
                writes = ALL_FLAGS & ~CF_MASK;
            } else if (opcode >= 0x70 && opcode <= 0x7F) {
                // Jcc (Reads various flags)
                reads = ALL_FLAGS;
            } else if (opcode >= 0x80 && opcode <= 0x83) {
                // Group 1 (80-83)
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 7) {
                    // CMP: Writes flags, reads none (producer)
                    writes = ALL_FLAGS;
                } else if (reg == 2 || reg == 3) {
                    // ADC, SBB: Reads CF, Writes all
                    reads = CF_MASK;
                    writes = ALL_FLAGS;
                } else {
                    // ADD, OR, AND, SUB, XOR
                    writes = ALL_FLAGS;
                }
            } else if (opcode == 0xC0 || opcode == 0xC1 || (opcode >= 0xD0 && opcode <= 0xD3)) {
                // Shift/Rotate: Writes flags
                writes = ALL_FLAGS;
            } else if (opcode == 0xF6 || opcode == 0xF7) {
                // Group 3: TEST, NOT, NEG, MUL, IMUL, DIV, IDIV
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 0 || reg == 1) {
                    // TEST: Writes flags, reads none
                    writes = ALL_FLAGS;
                } else if (reg == 2) {
                    // NOT: No flags affected
                } else {
                    // NEG, MUL, IMUL, DIV, IDIV: Writes flags
                    writes = ALL_FLAGS;
                }
            } else if (opcode == 0x9D) {
                // POPF: Writes all flags
                writes = ALL_FLAGS;
            } else if (opcode == 0x9C) {
                // PUSHF: Reads all flags
                reads = ALL_FLAGS;
            } else if (opcode == 0x9E) {
                // SAHF: Writes SF, ZF, AF, PF, CF
                writes = ALL_FLAGS;
            } else if (opcode == 0x9F) {
                // LAHF: Reads SF, ZF, AF, PF, CF
                reads = ALL_FLAGS;
            } else {
                switch (opcode) {
                    // Group 1: ADD, OR, ADC, SBB, AND, SUB, XOR, CMP
                    case 0x00:
                    case 0x01:
                    case 0x02:
                    case 0x03:
                    case 0x04:
                    case 0x05:
                        writes = ALL_FLAGS;
                        break;  // ADD
                    case 0x08:
                    case 0x09:
                    case 0x0A:
                    case 0x0B:
                    case 0x0C:
                    case 0x0D:
                        writes = ALL_FLAGS;
                        break;  // OR
                    case 0x10:
                    case 0x11:
                    case 0x12:
                    case 0x13:
                    case 0x14:
                    case 0x15:
                        writes = ALL_FLAGS;
                        reads = CF_MASK;
                        break;  // ADC
                    case 0x18:
                    case 0x19:
                    case 0x1A:
                    case 0x1B:
                    case 0x1C:
                    case 0x1D:
                        writes = ALL_FLAGS;
                        reads = CF_MASK;
                        break;  // SBB
                    case 0x20:
                    case 0x21:
                    case 0x22:
                    case 0x23:
                    case 0x24:
                    case 0x25:
                        writes = ALL_FLAGS;
                        break;  // AND
                    case 0x28:
                    case 0x29:
                    case 0x2A:
                    case 0x2B:
                    case 0x2C:
                    case 0x2D:
                        writes = ALL_FLAGS;
                        break;  // SUB
                    case 0x30:
                    case 0x31:
                    case 0x32:
                    case 0x33:
                    case 0x34:
                    case 0x35:
                        writes = ALL_FLAGS;
                        break;  // XOR
                    case 0x38:
                    case 0x39:
                    case 0x3A:
                    case 0x3B:
                    case 0x3C:
                    case 0x3D:
                        // CMP: Writes flags, reads none (producer)
                        writes = ALL_FLAGS;
                        break;
                    case 0x84:
                    case 0x85:
                    case 0xA8:
                    case 0xA9:
                        // TEST: Writes flags, reads none (producer)
                        writes = ALL_FLAGS;
                        break;
                }
            }
        } else if (map == 1) {
            if (opcode >= 0x40 && opcode <= 0x4F) {
                reads = ALL_FLAGS;  // CMOVcc
            } else if (opcode >= 0x80 && opcode <= 0x8F) {
                reads = ALL_FLAGS;  // Jcc near
            } else if (opcode >= 0x90 && opcode <= 0x9F) {
                reads = ALL_FLAGS;  // SETcc
            }
        }

        // 2. Optimization: If op writes flags that are NOT live, and we have an NF
        // handler
        if (writes != 0 && (writes & live_flags) == 0) {
            HandlerFunc nf_h = g_Handlers_NF[h_idx];
            if (nf_h) {
                // Update Offset directly to NF version
                op.handler_offset = (int32_t)((intptr_t)nf_h - (intptr_t)g_HandlerBase);
            }
        }

        // 3. Update live_flags for next (previous) instruction
        live_flags &= ~writes;
        live_flags |= reads;
    }
#endif

    block->end_eip = current_eip;
    return !block->ops.empty();
}

// ----------------------------------------------------------------------------
// BasicBlock Implementation
// ----------------------------------------------------------------------------

BasicBlock::BasicBlock() = default;

BasicBlock::~BasicBlock() {
    // 1. Clear outgoing links (I -> Others)
    if (!ops.empty()) {
        BasicBlock* target = ops.back().next_block;
        // Check if this op is indeed a chained branch. 
        // In our current engine, only the sentinel at the end of a block holds next_block.
        // And it's only set during chaining in X86_Run.
        if (target) {
            target->RemoveIncoming(this);
        }
    }

    // 2. Clear incoming links (Others -> I)
    UnlinkAll();
}

void BasicBlock::LinkFrom(BasicBlock* source) {
    if (!source) return;
    
    // 1. Add source to our incoming list
    incoming_jumps.push_back(source);

    // 2. Set source's last op to point to us
    if (!source->ops.empty()) {
        source->ops.back().next_block = this;
    }
}

void BasicBlock::RemoveIncoming(BasicBlock* source) {
    if (!source) return;
    auto it = std::find(incoming_jumps.begin(), incoming_jumps.end(), source);
    if (it != incoming_jumps.end()) {
        // Swap with last element and pop for O(1) removal (relative to search)
        *it = incoming_jumps.back();
        incoming_jumps.pop_back();
    }
}

void BasicBlock::UnlinkAll() {
    // For every block that jumps to us...
    for (BasicBlock* src : incoming_jumps) {
        if (!src->ops.empty()) {
            // Clear the pointer in the source block
            // Note: We assume the source block is still alive because it holds a raw pointer to us.
            // But wait, in our new model, source does NOT hold a strong ref to us (to avoid cycles).
            // Source holds a raw pointer. We must clear it.
            if (src->ops.back().next_block == this) {
                 src->ops.back().next_block = nullptr;
            }
        }
    }
    incoming_jumps.clear();
}

}  // namespace x86emu
