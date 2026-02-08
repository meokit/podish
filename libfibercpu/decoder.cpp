#include "decoder.h"
#include <cstdio>
#include <cstring>
#include "decoder_lut.h"
#include "dfe_lut.h"
#include "dispatch.h"
#include "exec_utils.h"  // For Flag Masks
#include "ops.h"         // For g_Handlers
#include "specialization.h"
#include "state.h"

namespace fiberish {

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
bool DecodeInstruction(const uint8_t* code, DecodedOp* op, uint16_t* handler_index) {
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
#define ENABLE_UNUSED_SEGMENTS_DECODING 1
#if ENABLE_UNUSED_SEGMENTS_DECODING
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
#endif
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
    *handler_index = (map << 8) | opcode;

    // 3. ModRM
    uint8_t has_modrm = kHasModRM[map][opcode];
    if (has_modrm) {
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
            *handler_index = (mod == 3) ? OP_MOV_RR_STORE : OP_MOV_RM_STORE;
        } else if (opcode == 0x8B) {
            uint8_t mod = (op->modrm >> 6) & 3;
            *handler_index = (mod == 3) ? OP_MOV_RR_LOAD : OP_MOV_MR_LOAD;
        }
    }

    HandlerFunc h = g_Handlers[*handler_index];
    if (h) {
        op->handler = h;
    } else {
        // Fallback to UD2
        HandlerFunc ud2 = g_Handlers[0x10B];
        op->handler = ud2;
    }

    return true;
}

// Helper to check if opcode is Control Flow
static bool IsControlFlow(const DecodedOp* op) { return op->meta.flags.is_control_flow; }

BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts) {
    // 1. Decode into temporary storage
    // Use a small buffer on stack to avoid heap allocation for common small blocks?
    // Or just std::vector. std::vector is safer for now.
    std::vector<DecodedOp> temp_ops;
    constexpr uint64_t MAX_INSTS = 64;
    temp_ops.reserve(MAX_INSTS + 2);  // + Sentinel + potential Fault

    uint32_t current_eip = start_eip;
    uint32_t inst_count = 0;

    uint64_t effective_limit = std::min(MAX_INSTS, max_insts == 0 ? MAX_INSTS : max_insts);

    // DFE Live Flags - declared here to avoid goto skip error
    uint32_t live_flags = DFE_ALL_FLAGS;

    // Temporary storage for indices to support DFE
    std::vector<uint16_t> op_indices;
    op_indices.reserve(effective_limit);

    // Page boundary check helper
    auto is_page_cross = [](uint32_t start, uint32_t len) {
        return (start & 0xFFFFF000) != ((start + len - 1) & 0xFFFFF000);
    };

    uint32_t end_eip = start_eip;
    bool success = true;

    while (temp_ops.size() < effective_limit) {
        // 0. Check Limit
        if (limit_eip != 0 && current_eip >= limit_eip) {
            break;
        }
        // 1. Fetch
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
            if (temp_ops.empty()) {
                // If first instruction fails, we return nullptr effectively (or special block?)
                // Original code returned false.
                success = false;
            }
            break;
        }

        DecodedOp op;
        uint16_t handler_index;
        if (!DecodeInstruction(buf, &op, &handler_index)) {
            // Decode error: Insert Fault Op
            fprintf(stderr,
                    "[DecodeBlock] DecodeInstruction Failed at %08X. Bytes: %02X %02X "
                    "%02X %02X\n",
                    current_eip, buf[0], buf[1], buf[2], buf[3]);
            std::memset(&op, 0, sizeof(op));
            op.length = 0;  // Fault: EIP points to instruction

            HandlerFunc ud2 = g_Handlers[0x10B];  // UD2
            op.handler = ud2;

            op.next_eip = current_eip;
            temp_ops.push_back(op);

            // Append Sentinel for dispatch safety
            DecodedOp sentinel;
            std::memset(&sentinel, 0, sizeof(sentinel));

            HandlerFunc exit_h = g_ExitHandlers[0];
            sentinel.handler = exit_h;

            // Sentinel next_block initialization to dummy
            sentinel.next_block = state->dummy_invalid_block;
            sentinel.next_eip = current_eip;

            temp_ops.push_back(sentinel);

            end_eip = current_eip + 1;
            goto finalize;
        }

        // Check if a specialized handler exists for this opcode + modrm/etc.
        HandlerFunc specialized_h = FindSpecializedHandler(handler_index, &op);
        if (specialized_h) {
            op.handler = specialized_h;
        }

        // Recover index logic (to keep op_indices in sync)
        uint8_t map = 0;
        uint8_t opcode = buf[0];
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
        op.next_eip = current_eip + op.length;

        // Optimization: Skip NOPs by absorbing them into the previous instruction
        // unless it's the first instruction of the block
        bool is_nop = (handler_index == 0x90 || handler_index == 0x11F);
        if (is_nop && !temp_ops.empty() && !IsControlFlow(&temp_ops.back())) {
            // "Absorb" NOP into the previous instruction
            DecodedOp& prev = temp_ops.back();
            prev.next_eip += op.length;
            prev.length += op.length;
            op_indices.pop_back();
            // Advance EIP and continue
            current_eip += op.length;
            continue;
        }

        temp_ops.push_back(op);
        inst_count++;
        uint32_t inst_len = op.length;

        // Stop if Control Flow
        if (IsControlFlow(&op)) {
            current_eip += inst_len;
            break;
        }

        // Advance
        current_eip += inst_len;

        // Stop if Control Flow (checked again on last op? redundancy from original code)
        if (IsControlFlow(&temp_ops.back())) {
            break;
        }

        // Stop if Page Cross
        if (is_page_cross(current_eip, 1)) {
            break;
        }
    }

    // Append Sentinel Op
    {
        DecodedOp sentinel;
        std::memset(&sentinel, 0, sizeof(sentinel));

        uint32_t k = (uint32_t)current_eip;
        k ^= k >> 12;
        k ^= k >> 4;
        uint8_t exit_idx = k % (sizeof(g_ExitHandlers) / sizeof(g_ExitHandlers[0]));
        HandlerFunc exit_h = g_ExitHandlers[exit_idx];
        sentinel.handler = exit_h;
        sentinel.next_block = state->dummy_invalid_block;  // Important!
        temp_ops.push_back(sentinel);
    }
    end_eip = current_eip;

#if 1
    // DFE Optimization (Backward Pass)
    live_flags = DFE_ALL_FLAGS;
    for (int i = (int)temp_ops.size() - 2; i >= 0; --i) {
        DecodedOp& op = temp_ops[i];
        if (i >= (int)op_indices.size()) break;

        uint16_t h_idx = op_indices[i];
        uint16_t flat_idx = h_idx & 0x1FF;

        const auto& info = kOpFlagTable[flat_idx];
        uint32_t reads = 0;
        uint32_t writes = 0;

        switch (info.type) {
            case DFE_TYPE_SIMPLE:
                reads = info.read_mask;
                writes = info.write_mask;
                break;
            case DFE_TYPE_GROUP1: {
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 2 || reg == 3) {  // ADC, SBB
                    reads = DFE_CF_MASK;
                    writes = DFE_ALL_FLAGS;
                } else if (reg == 7) {  // CMP
                    writes = DFE_ALL_FLAGS;
                } else {
                    writes = DFE_ALL_FLAGS;
                }
                break;
            }
            case DFE_TYPE_GROUP2:
                writes = DFE_ALL_FLAGS;
                break;
            case DFE_TYPE_GROUP3: {
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 0 || reg == 1) {  // TEST
                    writes = DFE_ALL_FLAGS;
                } else if (reg == 2) {  // NOT
                } else {                // NEG, MUL
                    writes = DFE_ALL_FLAGS;
                }
                break;
            }
            case DFE_TYPE_GROUP4: {
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 0 || reg == 1) {  // INC, DEC
                    writes = DFE_ALL_FLAGS & ~DFE_CF_MASK;
                }
                break;
            }
            default:
                reads = DFE_ALL_FLAGS;
                break;
        }

        if (writes != 0 && (writes & live_flags) == 0) {
            HandlerFunc nf_h = g_Handlers_NF[h_idx];
            if (nf_h) {
                op.handler = nf_h;
            }
        }
        live_flags &= ~writes;
        live_flags |= reads;
    }
#endif

finalize:
    if (!success || temp_ops.empty()) return nullptr;

    // 2. Allocate BasicBlock with Flexible Array Member
    size_t alloc_size = BasicBlock::CalculateSize(temp_ops.size());
    void* mem = state->block_pool.allocate(alloc_size);
    // BasicBlock is POD-like now, no constructor call needed, but we can placement new to be safe/clean
    BasicBlock* block = new (mem) BasicBlock;

    block->start_eip = start_eip;
    block->end_eip = end_eip;
    block->inst_count = inst_count;
    block->is_valid = true;

    // Copy ops
    std::memcpy(block->ops, temp_ops.data(), temp_ops.size() * sizeof(DecodedOp));

    return block;
}

// ----------------------------------------------------------------------------
// BasicBlock Implementation
// ----------------------------------------------------------------------------

void BasicBlock::Invalidate() {
    if (!is_valid) return;
    is_valid = false;
    // No unlinking needed because OpExitBlock checks is_valid
}

}  // namespace fiberish
