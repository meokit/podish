#include "decoder.h"
#include <cstring>
#include <cstdio>
#include "decoder_lut.h"
#include "state.h"
#include "ops.h" // For g_Handlers
#include "dispatch.h"

namespace x86emu {

alignas(64) static const uint8_t kControlFlowMaps[2][32] = {
    // Map 0: Primary opcodes (Jcc short, CALL, JMP, RET, LOOP, INT, HLT, etc.)
    { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 
      0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x0C, 0xFC, 0x00, 0x00, 0x0F, 0x0F, 0x10, 0x80 },
    // Map 1: 0x0F prefixed opcodes (Jcc near, SYSCALL, SYSENTER)
    { 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 
      0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
};


// Helper to determine Immediate Size (in bytes) based on Blink Type and Prefixes
static int GetImmLength(uint8_t type, const DecodedOp* op) {
    bool osz = op->prefixes.flags.opsize; // 0x66
    
    switch (type) {
        case 0: return -1; // Error
        case 1: return 0;  // None
        case 3: // Group 3 Byte (TEST=1, others 0) / Group 11 Byte (MOV=1)
             {
                 uint8_t reg = (op->modrm >> 3) & 7;
                 // 0xF6/0xC6: Only Reg 0 (TEST/MOV) and 1 (TEST reserved) have Imm8
                 return (reg <= 1) ? 1 : 0;
             }
        case 4: // Group 3 Word/Dword (TEST=1, others 0) / Group 11 (MOV=1)
             {
                 uint8_t reg = (op->modrm >> 3) & 7;
                 // 0xF7/0xC7: Only Reg 0 and 1 have Imm
                 return (reg <= 1) ? (osz ? 2 : 4) : 0;
             }
        case 5: return 1;  // Byte Signed (really just 1 byte)
        case 6: return osz ? 2 : 4; // v (Word or Dword) Same as 7 for 32-bit
        case 7: return osz ? 2 : 4; // v (Word or Dword)
        case 8: return 2;  // Word
        case 9: return 1;  // Byte
        case 10: return osz ? 2 : 4; // v (Word or Dword)
        case 11: return 3; // uimm0(2) + uimm1(1) (ENTER)
        default: return 0;
    }
}

// Decoder Logic
// Returns true on success, false on failure/invalid instruction
bool DecodeInstruction(const uint8_t* code, DecodedOp* op) {
    // Reset op
    std::memset(op, 0, sizeof(DecodedOp));
    
    const uint8_t* start = code;
    const uint8_t* ptr = code;
    
    // 1. Legacy Prefixes
    bool prefix_done = false;
    while (!prefix_done) {
        uint8_t b = *ptr;
        switch (b) {
            // Group 1: Lock / REP
            case 0xF0: op->prefixes.flags.lock = 1; break;
            case 0xF2: op->prefixes.flags.repne = 1; break;
            case 0xF3: op->prefixes.flags.rep = 1; break;
            
            // Group 2: Segment Overrides
            case 0x2E: op->prefixes.flags.segment = 2; break; // CS
            case 0x36: op->prefixes.flags.segment = 3; break; // SS
            case 0x3E: op->prefixes.flags.segment = 4; break; // DS 
            case 0x26: op->prefixes.flags.segment = 1; break; // ES
            case 0x64: op->prefixes.flags.segment = 5; break; // FS
            case 0x65: op->prefixes.flags.segment = 6; break; // GS
            
            // Group 3: Op Size
            case 0x66: op->prefixes.flags.opsize = 1; break;
            
            // Group 4: Addr Size
            case 0x67: op->prefixes.flags.addrsize = 1; break;
            
            default:
                prefix_done = true;
                continue; // Do not increment ptr for non-prefix
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
        opcode = *ptr; // Next byte is opcode
    }
    ptr++;
    
    // Set Handler Index (Map 0 or 1)
    op->handler_index = (map << 8) | opcode;
    
    // 3. ModRM
    uint8_t has_modrm = kHasModRM[map][opcode]; 
    if (has_modrm) {
        op->meta.flags.has_modrm = 1;
        uint8_t modrm = *ptr++;
        op->modrm = modrm;
        
        uint8_t mod = (modrm >> 6) & 3;
        uint8_t rm = modrm & 7;
        
        // SIB? (If Mod != 3 and RM == 4)
        if (mod != 3 && rm == 4) {
            op->meta.flags.has_sib = 1;
            op->sib = *ptr++;
        }
        
        // Displacement?
        uint8_t disp_size = 0;
        if (mod == 0) {
            if (rm == 5) disp_size = 4; // Disp32 (EBP replaced by Disp32)
        } else if (mod == 1) {
            disp_size = 1; // Disp8
        } else if (mod == 2) {
            disp_size = 4; // Disp32
        }
        
        // SIB Base Special Case?
        if (op->meta.flags.has_sib) {
            uint8_t base = op->sib & 7;
            if (mod == 0 && base == 5) {
                disp_size = 4; // Disp32 (Mod=0, Base=5 -> Disp32, no Base)
            }
        }
        
        if (disp_size > 0) {
            op->meta.flags.has_disp = 1;
            if (disp_size == 1) {
                 int8_t d8 = (int8_t)*ptr;
                 op->disp = (uint32_t)(int32_t)d8; // Sign extend!
                 ptr += 1;
            } else {
                 op->disp = *reinterpret_cast<const uint32_t*>(ptr);
                 ptr += 4;
            }
        }
    }
    
    // 4. Immediate
    uint8_t imm_type = kImmType[map][opcode];
    int imm_len = GetImmLength(imm_type, op);
    
    // Override for MOV moffs (A0-A3) - Address Size determines Imm Size
    if (map == 0 && (opcode >= 0xA0 && opcode <= 0xA3)) {
        if (op->prefixes.flags.addrsize) imm_len = 2;
        else imm_len = 4;
    }

    if (imm_len > 0) {
        op->meta.flags.has_imm = 1;
        if (imm_len == 1) op->imm = *reinterpret_cast<const uint8_t*>(ptr);
        else if (imm_len == 2) op->imm = *reinterpret_cast<const uint16_t*>(ptr);
        else if (imm_len == 4) op->imm = *reinterpret_cast<const uint32_t*>(ptr);
        else if (imm_len == 3) {
            // Special Case for ENTER (Iw Ib)
            uint16_t iw = *reinterpret_cast<const uint16_t*>(ptr);
            uint8_t ib = *(ptr + 2);
            op->imm = iw | (ib << 16);
        }
        ptr += imm_len;
    }
    
    op->length = (ptr - start);

    // Rule 1: REP/REPNE prefixes always terminate blocks as they can loop (modify EIP)
    bool is_rep = op->prefixes.flags.rep || op->prefixes.flags.repne;

    // Rule 2: Table-based lookup for branching opcodes
    // Access bitmap using map & 1 to ensure safe indexing. 
    // Filter map > 1 later to ensure strict correctness.
    bool is_map_match = (kControlFlowMaps[map & 1][opcode >> 3] >> (opcode & 7)) & 1;

    // Rule 3: Handle Group 5 (0xFF) Special Case
    // Only sub-opcodes 2 (CALL), 3 (CALLF), 4 (JMP), 5 (JMPF) are control flow.
    // Use __builtin_expect to hint that 0xFF is less common than other instructions.
    if (__builtin_expect((opcode == 0xFF) && (map == 0), 0)) {
        // Optimized range check: (reg - 2) <= 3 is equivalent to (reg >= 2 && reg <= 5)
        uint8_t reg = (op->modrm >> 3) & 7;
        is_map_match = ((uint8_t)(reg - 2) <= 3);
    }

    // Combine results and apply map safety filter (ensures 0x0F 0x0F maps don't false positive)
    op->meta.flags.is_control_flow = is_rep | (is_map_match & (map <= 1));

    return true;

}

// Helper to check if opcode is Control Flow
static bool IsControlFlow(const DecodedOp* op) {
    return op->meta.flags.is_control_flow;
}

bool DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts, BasicBlock* block) {
    block->start_eip = start_eip;
    block->ops.clear();
    block->inst_count = 0;
    
    uint32_t current_eip = start_eip;
    uint64_t effective_limit = 128;
    if (max_insts > 0 && max_insts < 128) effective_limit = max_insts;
    
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
        for (int i=0; i<16; ++i) {
             buf[i] = state->mmu.read_for_exec<uint8_t>(current_eip + i); 
             if (state->status != EmuStatus::Running) {
                 fetch_fault = true;
                 break;
             }
        }
        
        if (fetch_fault) {
             if (block->ops.empty()) return false; // First instruction failed
             break; // Terminate block
        }
        
        DecodedOp op;
        if (!DecodeInstruction(buf, &op)) {
            // Decode error: Insert Fault Op
            printf("[DecodeBlock] DecodeInstruction Failed at %08X. Bytes: %02X %02X %02X %02X\n", current_eip, buf[0], buf[1], buf[2], buf[3]);
            std::memset(&op, 0, sizeof(op));
            op.length = 0; // Fault: EIP points to instruction
            op.handler_index = 0x10B; // UD2
            block->ops.push_back(op);
            
            // Append Sentinel for dispatch safety
            DecodedOp sentinel;
            std::memset(&sentinel, 0, sizeof(sentinel));
            sentinel.handler_index = 1023;
            block->ops.push_back(sentinel);
            
            block->end_eip = current_eip + 1;
            return true; // Return true to execute what we have (including the fault)
        }
        
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
        
        // Stop if Page Cross
        // (Execution engine might need to re-check TLB)
        if (is_page_cross(current_eip, 1)) {
            break; 
        }
    }
    
    // Append Sentinel Op (1023) to terminate Threaded Dispatch
    DecodedOp sentinel;
    std::memset(&sentinel, 0, sizeof(sentinel));
    sentinel.handler_index = 1023;
    block->ops.push_back(sentinel);
    
    block->end_eip = current_eip;
    return !block->ops.empty();
}

} // namespace x86emu
