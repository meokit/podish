#include "decoder.h"
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include "decoder_lut.h"
#include "dfe_lut.h"
#include "dispatch.h"
#include "exec_utils.h"  // For Flag Masks
#if FIBERCPU_ENABLE_JIT
#include "jit/block_builder.h"
#endif
#include "ops.h"  // For g_Handlers
#include "specialization.h"
#include "state.h"
#include "superopcodes.h"

namespace fiberish {

namespace {
#if FIBERCPU_ENABLE_JIT
bool JitDebugEnabled() {
#ifdef FIBERCPU_ENABLE_JIT_DEBUG_LOG
    static bool enabled = [] {
        const char* value = std::getenv("FIBERCPU_JIT_DEBUG");
        return value != nullptr && value[0] != '\0' && value[0] != '0';
    }();
    return enabled;
#else
    return false;
#endif
}

void JitDebugLog(const char* fmt, ...) {
#ifdef FIBERCPU_ENABLE_JIT_DEBUG_LOG
    if (!JitDebugEnabled()) return;
    FILE* fp = std::fopen("/tmp/fibercpu_jit.log", "a");
    if (!fp) return;
    va_list args;
    va_start(args, fmt);
    std::vfprintf(fp, fmt, args);
    va_end(args);
    std::fclose(fp);
#else
    (void)fmt;
#endif
}
#endif
}  // namespace

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

static bool UsesControlFlowCacheStorage(uint16_t handler_index) {
    if (handler_index == 0xE8 || handler_index == 0xE9 || handler_index == 0xEB || handler_index == 0xC2 ||
        handler_index == 0xC3 || (handler_index >= 0xE0 && handler_index <= 0xE3)) {
        return true;
    }
    return (handler_index >= 0x70 && handler_index <= 0x7F) || (handler_index >= 0x180 && handler_index <= 0x18F);
}

static bool IsConditionalBranchHandlerIndex(uint16_t handler_index) {
    if (handler_index >= 0xE0 && handler_index <= 0xE3) return true;
    return (handler_index >= 0x70 && handler_index <= 0x7F) || (handler_index >= 0x180 && handler_index <= 0x18F);
}

// Decoder Logic
// Returns true on success, false on failure/invalid instruction
bool DecodeInstruction(const uint8_t* code, DecodedInstTmp* inst, uint16_t* handler_index) {
    std::memset(inst, 0, sizeof(*inst));

    DecodedOp* op = &inst->head;

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
    *handler_index = (map << 8) | opcode;
    if (UsesControlFlowCacheStorage(*handler_index)) {
        SetExtKind(op, ExtKind::ControlFlow);
    }

    // 3. ModRM
    uint8_t has_modrm = kHasModRM[map][opcode];
    if (has_modrm) {
        op->meta.flags.has_modrm = 1;
        uint8_t modrm = *ptr++;
        op->modrm = modrm;

        uint8_t mod = (modrm >> 6) & 3;
        uint8_t rm = modrm & 7;
        bool is_mem_operand = mod != 3;
        uint8_t base_offset = kNoRegOffset;
        uint8_t index_offset = kNoRegOffset;
        uint8_t scale = 0;

        // SIB? (If Mod != 3 and RM == 4)
        uint8_t sib_byte = 0;
        if (is_mem_operand && rm == 4) {
            sib_byte = *ptr++;
        }

        // Displacement?
        uint8_t disp_size = 0;
        if (is_mem_operand) {
            op->meta.flags.has_mem = 1;

            if (mod == 0) {
                if (rm == 5) disp_size = 4;  // Disp32 (EBP replaced by Disp32)
            } else if (mod == 1) {
                disp_size = 1;  // Disp8
            } else if (mod == 2) {
                disp_size = 4;  // Disp32
            }

            // SIB Base Special Case?
            if (rm == 4) {
                uint8_t base = sib_byte & 7;
                if (mod == 0 && base == 5) {
                    disp_size = 4;  // Disp32 (Mod=0, Base=5 -> Disp32, no Base)
                }
            }

            if (disp_size > 0) {
                if (disp_size == 1) {
                    int8_t d8 = (int8_t)*ptr;
                    inst->head.ext.data.disp = (uint32_t)(int32_t)d8;  // Sign extend!
                    ptr += 1;
                } else {
                    inst->head.ext.data.disp = *reinterpret_cast<const uint32_t*>(ptr);
                    ptr += 4;
                }
            }

            // Pre-calculate EA components for faster execution
            if (rm == 4) {
                scale = (sib_byte >> 6) & 3;
                uint8_t index = (sib_byte >> 3) & 7;
                uint8_t base_reg = sib_byte & 7;

                if (index != 4) index_offset = index * 4;

                if (mod == 0 && base_reg == 5) {
                    // Base is None (Disp32) - already initialized to kNoRegOffset
                } else {
                    base_offset = base_reg * 4;
                }
            } else {
                // No SIB
                if (mod == 0 && rm == 5) {
                    // Base is None (Disp32) - already initialized to kNoRegOffset
                } else {
                    base_offset = rm * 4;
                }
            }

            inst->head.ext.data.ea_desc = memdesc::PackEA(base_offset, index_offset, scale, op->prefixes.flags.segment);
        }
    } else {
        // No ModRM: Use modrm field to store opcode low bits (for Reg index in
        // INC/DEC/PUSH/POP)
        op->modrm = opcode & 7;
    }

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
        uint32_t decoded_imm = 0;
        if (imm_len == 1)
            decoded_imm = *reinterpret_cast<const uint8_t*>(ptr);
        else if (imm_len == 2)
            decoded_imm = *reinterpret_cast<const uint16_t*>(ptr);
        else if (imm_len == 4)
            decoded_imm = *reinterpret_cast<const uint32_t*>(ptr);
        else if (imm_len == 3) {
            // Special Case for ENTER (Iw Ib)
            uint16_t iw = *reinterpret_cast<const uint16_t*>(ptr);
            uint8_t ib = *(ptr + 2);
            decoded_imm = iw | (ib << 16);
        }
        inst->head.ext.data.imm = decoded_imm;
        ptr += imm_len;
    }

    op->SetLength(ptr - start);

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

    op->meta.flags.is_conditional_branch = IsConditionalBranchHandlerIndex(*handler_index);

    return true;
}

// Helper to check if opcode is Control Flow
static bool IsControlFlow(const DecodedInstTmp& inst) { return inst.head.meta.flags.is_control_flow; }

static bool IsDirectRelativeJmpHandlerIndex(uint16_t handler_index) {
    return handler_index == 0xE9 || handler_index == 0xEB;
}

static bool IsDirectRelativeJccHandlerIndex(uint16_t handler_index) {
    return (handler_index >= 0xE0 && handler_index <= 0xE3) || (handler_index >= 0x70 && handler_index <= 0x7F) ||
           (handler_index >= 0x180 && handler_index <= 0x18F);
}

static bool IsDirectRelativeCallHandlerIndex(uint16_t handler_index) { return handler_index == 0xE8; }

static bool UsesBranchCarryingSentinel(uint16_t handler_index, const DecodedOp& op) {
    if (handler_index != 0xFF) return false;
    const uint8_t subop = (op.modrm >> 3) & 7;
    return subop == 2 || subop == 4;
}

static uint32_t GetDirectRelativeJmpTarget(uint16_t handler_index, const DecodedOp& op) {
    if (handler_index == 0xEB) {
        return op.next_eip + static_cast<int8_t>(GetImm(&op));
    }
    return op.next_eip + static_cast<int32_t>(GetImm(&op));
}

static uint32_t GetDirectRelativeJccTarget(uint16_t handler_index, const DecodedOp& op) {
    if ((handler_index >= 0xE0 && handler_index <= 0xE3) || (handler_index >= 0x70 && handler_index <= 0x7F)) {
        return op.next_eip + static_cast<int8_t>(GetImm(&op));
    }
    return op.next_eip + static_cast<int32_t>(GetImm(&op));
}

static bool TryGetDirectRelativeTargetEip(uint16_t handler_index, const DecodedOp& op, uint32_t* target_eip) {
    if (IsDirectRelativeJmpHandlerIndex(handler_index)) {
        *target_eip = GetDirectRelativeJmpTarget(handler_index, op);
        return true;
    }
    if (IsDirectRelativeJccHandlerIndex(handler_index)) {
        *target_eip = GetDirectRelativeJccTarget(handler_index, op);
        return true;
    }
    if (IsDirectRelativeCallHandlerIndex(handler_index)) {
        *target_eip = op.next_eip + static_cast<int32_t>(GetImm(&op));
        return true;
    }
    return false;
}

static void ApplySpecializedHandler(uint16_t handler_index, DecodedOp& op) {
    HandlerFunc specialized_h = FindSpecializedHandler(handler_index, &op);
    if (specialized_h) {
        op.handler = specialized_h;
    }
}

BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts) {
    // 1. Decode into temporary storage
    // Use a small buffer on stack to avoid heap allocation for common small blocks?
    // Or just std::vector. std::vector is safer for now.
    std::vector<DecodedInstTmp> temp_ops;
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
    BlockStopReason stop_reason = BlockStopReason::Unknown;

    while (temp_ops.size() < effective_limit) {
        // 0. Check Limit
        if (limit_eip != 0 && current_eip >= limit_eip) {
            stop_reason = BlockStopReason::LimitEip;
            break;
        }
        // 1. Fetch with Probe
        uint8_t buf[16];
        std::memset(buf, 0, 16);
        bool fetch_fault = false;

        uint32_t page_offset = current_eip & 0xFFF;
        uint32_t bytes_to_page_end = 0x1000 - page_offset;

        if (bytes_to_page_end >= 16) {
            // Fast Path
            for (int i = 0; i < 16; ++i) {
                buf[i] = state->mmu.read_for_exec<uint8_t>(state, current_eip + i);
                if (state->status != EmuStatus::Running) {
                    fetch_fault = true;
                    break;
                }
            }
        } else {
            // Slow Path: Check boundary
            // Fetch first part
            for (uint32_t i = 0; i < bytes_to_page_end; ++i) {
                buf[i] = state->mmu.read_for_exec<uint8_t>(state, current_eip + i);
                if (state->status != EmuStatus::Running) {
                    fetch_fault = true;
                    break;
                }
            }

            // Check next page if no fault so far
            if (!fetch_fault) {
                const uint32_t next_page_addr = current_eip + bytes_to_page_end;
                const bool next_exec_ok = state->mmu.probe_exec(next_page_addr);
                if (next_exec_ok) {
                    // Safe to fetch remaining
                    for (uint32_t i = bytes_to_page_end; i < 16; ++i) {
                        buf[i] = state->mmu.read_for_exec<uint8_t>(state, current_eip + i);
                        if (state->status != EmuStatus::Running) {
                            fetch_fault = true;
                            break;
                        }
                    }
                }
                // Else: Ignore, buffer already zero-padded
            }
        }

        if (fetch_fault) {
            if (temp_ops.empty()) {
                // If first instruction fails, we return nullptr effectively (or special block?)
                // Original code returned false.
                success = false;
            }
            stop_reason = BlockStopReason::FetchFault;
            break;
        }

        DecodedInstTmp inst;
        uint16_t handler_index;
        if (!DecodeInstruction(buf, &inst, &handler_index)) {
            // Decode error: Insert Fault Op
            fprintf(stderr,
                    "[DecodeBlock] DecodeInstruction Failed at %08X. Bytes: %02X %02X "
                    "%02X %02X\n",
                    current_eip, buf[0], buf[1], buf[2], buf[3]);
            std::memset(&inst, 0, sizeof(inst));
            inst.head.SetLength(0);  // Fault: EIP points to instruction

            HandlerFunc ud2 = g_Handlers[0x10B];  // UD2
            inst.head.handler = ud2;

            inst.head.next_eip = current_eip;
            temp_ops.push_back(inst);

            // Append Sentinel for dispatch safety
            DecodedInstTmp sentinel;
            std::memset(&sentinel, 0, sizeof(sentinel));

            HandlerFunc exit_h = g_ExitHandlersFallthrough[0];
            sentinel.head.handler = exit_h;

            // Sentinel next_block initialization to dummy
            SetNextBlock(&sentinel.head, &state->dummy_invalid_block);
            sentinel.head.next_eip = current_eip;

            temp_ops.push_back(sentinel);

            end_eip = current_eip + 1;
            stop_reason = BlockStopReason::DecodeFault;
            goto finalize;
        }

        // Verify length does not exceed valid fetch (Post-Decode Check)
        if (bytes_to_page_end < 16 && inst.head.GetLength() > bytes_to_page_end) {
            // Instruction spans into the probed-invalid page!
            // Trigger fault explicitly by reading the first byte of the invalid region.
            const uint32_t next_page_addr = current_eip + bytes_to_page_end;
            if (!state->mmu.probe_exec(next_page_addr)) {
                (void)state->mmu.read_for_exec<uint8_t>(state, next_page_addr);
                if (state->status != EmuStatus::Running) {
                    if (temp_ops.empty()) success = false;
                    break;
                }

                // Fault handler reported success. Ensure the target page really became executable.
                // If yes, retry decoding the same instruction with fresh bytes from the new mapping.
                if (state->mmu.probe_exec(next_page_addr)) {
                    continue;
                }

                if (temp_ops.empty()) success = false;
                state->status = EmuStatus::Fault;
                stop_reason = BlockStopReason::CrossPageFault;
                break;
            }
        }

        // Check if a specialized handler exists for this opcode + modrm/etc.
        ApplySpecializedHandler(handler_index, inst.head);

        if (GetExtKind(&inst.head) == ExtKind::ControlFlow) {
            SetCachedTarget(&inst.head, &state->dummy_invalid_block);
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
        inst.head.next_eip = current_eip + inst.head.GetLength();

        uint32_t target_eip;
        if (TryGetDirectRelativeTargetEip(handler_index, inst.head, &target_eip)) {
            SetControlTargetEip(&inst.head, target_eip);
        }

        // Optimization: Skip NOPs by absorbing them into the previous instruction
        // unless it's the first instruction of the block
        bool is_nop = (handler_index == 0x90 || handler_index == 0x11F);
        if (is_nop && !temp_ops.empty() && !IsControlFlow(temp_ops.back())) {
            // "Absorb" NOP into the previous instruction
            DecodedInstTmp& prev = temp_ops.back();
            uint32_t new_len = (uint32_t)prev.head.GetLength() + inst.head.GetLength();
            if (new_len <= 255) {
                prev.head.next_eip += inst.head.GetLength();
                prev.head.SetLength((uint8_t)new_len);
                op_indices.pop_back();
                // Advance EIP and continue
                current_eip += inst.head.GetLength();
                continue;
            }
        }

        temp_ops.push_back(inst);
        inst_count++;
        uint32_t inst_len = inst.head.GetLength();

        // Stop if Control Flow
        if (IsControlFlow(inst)) {
            current_eip += inst_len;
            stop_reason = BlockStopReason::ControlFlow;
            break;
        }

        // Advance
        current_eip += inst_len;

        // Stop if Control Flow (checked again on last op? redundancy from original code)
        if (IsControlFlow(temp_ops.back())) {
            stop_reason = BlockStopReason::ControlFlow;
            break;
        }

        // Stop if Page Cross
        if (is_page_cross(current_eip, 1)) {
            stop_reason = BlockStopReason::PageCross;
            break;
        }
    }

    if (stop_reason == BlockStopReason::Unknown && temp_ops.size() >= effective_limit) {
        stop_reason = BlockStopReason::MaxInsts;
    }

    // Append Sentinel Op
    {
        DecodedInstTmp sentinel;
        std::memset(&sentinel, 0, sizeof(sentinel));

        uint32_t k = (uint32_t)current_eip;
        k ^= k >> 12;
        k ^= k >> 4;
        const bool branch_carrying_sentinel =
            !op_indices.empty() && UsesBranchCarryingSentinel(op_indices.back(), temp_ops[temp_ops.size() - 1].head);
        uint8_t exit_idx = k % kExitHandlerReplicaCount;
        HandlerFunc exit_h =
            branch_carrying_sentinel ? g_ExitHandlersBranch[exit_idx] : g_ExitHandlersFallthrough[exit_idx];
        sentinel.head.handler = exit_h;
        sentinel.head.next_eip = temp_ops.back().head.next_eip;  // Copy next_eip from last op
        SetNextBlock(&sentinel.head, &state->dummy_invalid_block);
        temp_ops.push_back(sentinel);
    }
    end_eip = current_eip;

#if 1
    // DFE Optimization (Backward Pass)
    live_flags = DFE_ALL_FLAGS;
    for (int i = (int)temp_ops.size() - 2; i >= 0; --i) {
        DecodedOp& op = temp_ops[i].head;
        if (i >= (int)op_indices.size()) break;

        uint16_t h_idx = op_indices[i];
        uint32_t reads = 0;
        uint32_t writes = 0;
        uint16_t flat_idx = h_idx & 0x1FF;
        const auto& info = kOpFlagTable[flat_idx];

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
            // Dead Flags!
            op.meta.flags.no_flags = 1;

            // Re-resolve Handler with no_flags set
            // Note: We might have resolved a specialized handler earlier.
            // If we have a specialized handler that also supports NoFlags, fantastic.
            // But currently registered handlers are either Generic, Generic-NF, or Specialized-Attr.
            // If we have Specialized-Attr (e.g. Add_RegEax), we need Add_RegEax_NF.
            // But if we only have Add_Generic_NF, we might lose RegEax optimization.
            // The user implies we should use specialization system to select the best one.
            HandlerFunc special_h = FindSpecializedHandler(h_idx, &op);
            if (special_h) {
                op.handler = special_h;
            } else {
                // If no specialized handler found for NoFlags, keep original?
                // Or try to find generic NF handler?
                // RegisterNF implementation registers a specialized handler with just no_flags=true.
                // FindSpecialized will match it if no other constraints block it.
            }
        }
        live_flags &= ~writes;
        live_flags |= reads;
    }
#endif

finalize:
    if (!success || temp_ops.empty()) return nullptr;

    state->block_stats.Record(inst_count, stop_reason);

    size_t slot_count = temp_ops.size();

    size_t alloc_size = BasicBlock::CalculateSize(slot_count);
    void* mem = state->block_pool.allocate(alloc_size);
    BasicBlock* block = new (mem) BasicBlock;
    state->RememberAllocatedBlock(block);

    assert((start_eip & BasicBlock::kInvalidStartEipBit) == 0 && "BasicBlock start_eip must stay in low 2G");
    block->set_start_eip(start_eip);
    block->end_eip = end_eip;
    block->set_inst_count(inst_count);
    block->slot_count = (uint32_t)slot_count;
    block->exec_count = 0;
    block->sentinel_slot_index = 0;
    block->branch_target_eip = 0;
    block->fallthrough_eip = end_eip;
    block->set_terminal_kind(BlockTerminalKind::None);

    DecodedOp* dst = block->FirstOp();
    for (size_t i = 0; i < temp_ops.size(); ++i) {
        const auto& inst = temp_ops[i];
        if (i == temp_ops.size() - 1) {
            block->sentinel_slot_index = static_cast<uint32_t>(i);
        }
        dst[i] = inst.head;
    }

    const uint32_t decoded_inst_count = slot_count == 0 ? 0 : static_cast<uint32_t>(slot_count - 1);
    ApplySuperOpcodesToBlockOps(dst, decoded_inst_count);
    if (decoded_inst_count != 0 && !op_indices.empty()) {
        const uint16_t last_handler_index = op_indices.back();
        const DecodedOp& last_op = dst[decoded_inst_count - 1];
        if (IsDirectRelativeJmpHandlerIndex(last_handler_index)) {
            block->set_terminal_kind(BlockTerminalKind::DirectJmpRel);
            block->branch_target_eip = GetControlTargetEip(&last_op);
        } else if (IsDirectRelativeJccHandlerIndex(last_handler_index)) {
            block->set_terminal_kind(BlockTerminalKind::DirectJccRel);
            block->branch_target_eip = GetControlTargetEip(&last_op);
        } else if (last_op.meta.flags.is_control_flow) {
            block->set_terminal_kind(BlockTerminalKind::OtherControlFlow);
        }
    }

    block->entry = block->FirstOp()->handler;
    block->jit_code = nullptr;

    // Try JIT compilation
#if FIBERCPU_ENABLE_JIT
    if constexpr (true) {
        state->block_stats.jit_compile_attempts++;
        auto* jcb = jit::BlockBuilder::Get().CompileBlock(block);
        if (jcb) {
            state->block_stats.jit_compile_success++;
            block->entry = reinterpret_cast<HandlerFunc>(jcb->entry);
            if (JitDebugEnabled()) {
                JitDebugLog("[jit] enable block start=%08x entry=%p code=%p size=%zu\n", block->start_eip(),
                            reinterpret_cast<void*>(block->entry), jcb->entry, jcb->code_size);
            }
        } else if (JitDebugEnabled()) {
            state->block_stats.jit_compile_failure++;
            JitDebugLog("[jit] fallback block start=%08x entry=%p\n", block->start_eip(),
                        reinterpret_cast<void*>(block->entry));
        } else {
            state->block_stats.jit_compile_failure++;
        }
    }
#endif

    return block;
}

// ----------------------------------------------------------------------------
// BasicBlock Implementation
// ----------------------------------------------------------------------------

void BasicBlock::Invalidate() {
    if (!is_valid()) return;
    chain.start_eip = start_eip() | kInvalidStartEipBit;
    // No unlinking needed because OpExitBlock checks start_eip's invalid bit.
}

}  // namespace fiberish
