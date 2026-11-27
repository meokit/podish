#include "decoder.h"
#include <array>
#include <cassert>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <span>
#include "decoder_lut.h"
#include "dfe_lut.h"
#include "dispatch.h"
#include "exec_utils.h"  // For Flag Masks
#include "instruction_variants.h"
#include "ops.h"  // For g_Handlers
#include "specialization.h"
#include "state.h"
#include "superopcodes.h"
#if FIBERCPU_BLOCK_COMPILATION
#include "jit/block_recipe.h"
#endif
#if FIBERCPU_LINKED_BLOCK_CACHE
#include "jit/linked_block_cache.h"
#endif

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
static int GetImmLength(uint8_t type, const DecodedInstruction* op) {
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
    switch (handler_index) {
#include "block_link_opcodes.inc"
        return true;
        default:
            return false;
    }
}

static bool IsLockableOpcode(uint16_t opcode) {
    // Arithmetic forms with r/m as destination, not source.
    if (opcode <= 0x31 && (opcode & 7) <= 1) return true;
    switch (opcode) {
        case 0x80:
        case 0x81:
        case 0x82:
        case 0x83:  // Group 1, except CMP
        case 0x86:
        case 0x87:  // XCHG
        case 0xF6:
        case 0xF7:  // NOT / NEG
        case 0xFE:
        case 0xFF:  // INC / DEC
        case 0x1AB:
        case 0x1B3:
        case 0x1BB:  // BTS / BTR / BTC
        case 0x1BA:  // Immediate BTS / BTR / BTC
        case 0x1B0:
        case 0x1B1:  // CMPXCHG
        case 0x1C0:
        case 0x1C1:  // XADD
        case 0x1C7:  // CMPXCHG8B
            return true;
        default:
            return false;
    }
}

static bool IsValidLockModRM(uint16_t opcode, uint8_t modrm) {
    if ((modrm & 0xC0) == 0xC0) return false;
    const uint8_t subop = (modrm >> 3) & 7;
    switch (opcode) {
        case 0x80:
        case 0x81:
        case 0x82:
        case 0x83:
            return subop != 7;
        case 0xF6:
        case 0xF7:
            return subop == 2 || subop == 3;
        case 0xFE:
        case 0xFF:
            return subop <= 1;
        case 0x1BA:
            return subop >= 5;
        case 0x1C7:
            return subop == 1;
        default:
            return true;
    }
}

struct ExtendedOpcode {
    uint16_t handler_index;
    uint8_t imm_type;
    bool require_66;
};

static ExtendedOpcode DecodeExtendedOpcode(uint8_t escape, uint8_t opcode) {
    switch ((uint16_t{escape} << 8) | opcode) {
#include "extended_decode.inc"
        default:
            return {};
    }
}

// Decoder Logic
// Distinguishes an invalid encoding from a valid but incomplete byte sequence.
InstructionDecodeResult DecodeInstructionBounded(const uint8_t* code, size_t available, DecodedInstTmp* inst,
                                                 uint16_t* handler_index, uint16_t* raw_opcode,
                                                 DecodeHandlerMode mode) {
    *inst = {};

    DecodedInstruction* op = &inst->head;

    const uint8_t* start = code;
    const uint8_t* ptr = code;
    auto require = [&](size_t count) {
        const size_t end = static_cast<size_t>(ptr - start) + count;
        if (end > 15) return InstructionDecodeResult::Invalid;
        return end <= available ? InstructionDecodeResult::Success : InstructionDecodeResult::NeedMoreBytes;
    };

    // 1. Legacy Prefixes
    bool prefix_done = false;
    while (!prefix_done) {
        if (auto result = require(1); result != InstructionDecodeResult::Success) return result;
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
        if (ptr - start > 14) return InstructionDecodeResult::Invalid;
    }

    // 2. Opcode Dispatch
    uint8_t map = 0;
    uint8_t opcode = *ptr;
    ExtendedOpcode extended{};

    if (opcode == 0x0F) {
        map = 1;
        ptr++;
        if (auto result = require(1); result != InstructionDecodeResult::Success) return result;
        opcode = *ptr;  // Next byte is opcode
        if (opcode == 0x38 || opcode == 0x3A) [[unlikely]] {
            const auto escape = opcode;
            ++ptr;
            if (auto result = require(1); result != InstructionDecodeResult::Success) return result;
            opcode = *ptr;
            extended = DecodeExtendedOpcode(escape, opcode);
            if (!extended.handler_index ||
                (extended.require_66 &&
                 (op->prefixes.all & (prefix::REP | prefix::REPNE | prefix::OPSIZE)) != prefix::OPSIZE))
                return InstructionDecodeResult::Invalid;
            map = escape == 0x38 ? 2 : 3;
        }
    }
    ptr++;

    // Handler indices for extended maps come from the instruction catalog.
    *handler_index = extended.handler_index ? extended.handler_index : (map << 8) | opcode;
    if (raw_opcode) *raw_opcode = *handler_index;
    if (op->prefixes.flags.lock && !IsLockableOpcode(*handler_index)) return InstructionDecodeResult::Invalid;
    if (UsesControlFlowCacheStorage(*handler_index)) {
        SetExtKind(op, ExtKind::ControlFlow);
    }

    // 3. ModRM
    uint8_t has_modrm = map > 1 ? 1 : kHasModRM[map][opcode];
    if (has_modrm) {
        op->meta.flags.has_modrm = 1;
        if (auto result = require(1); result != InstructionDecodeResult::Success) return result;
        uint8_t modrm = *ptr++;
        op->modrm = modrm;
        if (op->prefixes.flags.lock && !IsValidLockModRM(*handler_index, modrm)) {
            return InstructionDecodeResult::Invalid;
        }

        uint8_t mod = (modrm >> 6) & 3;
        uint8_t rm = modrm & 7;
        bool is_mem_operand = mod != 3;
        uint8_t base_offset = kNoRegOffset;
        uint8_t index_offset = kNoRegOffset;
        uint8_t scale = 0;

        // SIB? (If Mod != 3 and RM == 4)
        uint8_t sib_byte = 0;
        if (is_mem_operand && !op->prefixes.flags.addrsize && rm == 4) {
            if (auto result = require(1); result != InstructionDecodeResult::Success) return result;
            sib_byte = *ptr++;
        }

        // Displacement?
        uint8_t disp_size = 0;
        if (is_mem_operand) {
            op->meta.flags.has_mem = 1;

            if (op->prefixes.flags.addrsize) {
                if (mod == 0 && rm == 6) disp_size = 2;  // Direct disp16, no BP
                if (mod == 1) disp_size = 1;
                if (mod == 2) disp_size = 2;
            } else {
                if (mod == 0 && rm == 5) disp_size = 4;
                if (mod == 1) disp_size = 1;
                if (mod == 2) disp_size = 4;
                if (rm == 4 && mod == 0 && (sib_byte & 7) == 5) {
                    disp_size = 4;  // SIB with no base
                }
            }

            if (disp_size > 0) {
                if (auto result = require(disp_size); result != InstructionDecodeResult::Success) return result;
                if (disp_size == 1) {
                    int8_t d8 = (int8_t)*ptr;
                    inst->head.ext.data.disp = (uint32_t)(int32_t)d8;  // Sign extend!
                    ptr += 1;
                } else if (disp_size == 2) {
                    uint16_t d16;
                    std::memcpy(&d16, ptr, 2);
                    inst->head.ext.data.disp =
                        mod == 0 ? d16 : static_cast<uint32_t>(static_cast<int32_t>(static_cast<int16_t>(d16)));
                    ptr += 2;
                } else {
                    std::memcpy(&inst->head.ext.data.disp, ptr, 4);
                    ptr += 4;
                }
            }

            // Pre-calculate EA components for faster execution
            if (op->prefixes.flags.addrsize) {
                // No SIB in 16-bit addressing. Keep BP in the base component
                // so implicit SS selection is distinguishable from DS.
                constexpr uint8_t bases[8] = {EBX, EBX, EBP, EBP, ESI, EDI, EBP, EBX};
                constexpr uint8_t indices[8] = {ESI, EDI, ESI, EDI, 8, 8, 8, 8};
                if (!(mod == 0 && rm == 6)) base_offset = bases[rm] * 4;
                index_offset = indices[rm] * 4;
            } else if (rm == 4) {
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
    uint8_t imm_type = map > 1 ? extended.imm_type : kImmType[map][opcode];
    int imm_len = GetImmLength(imm_type, op);

    // Override for MOV moffs (A0-A3) - Address Size determines Imm Size
    if (map == 0 && (opcode >= 0xA0 && opcode <= 0xA3)) {
        if (op->prefixes.flags.addrsize)
            imm_len = 2;
        else
            imm_len = 4;
    }

    if (imm_len > 0) {
        if (auto result = require(imm_len); result != InstructionDecodeResult::Success) return result;
        op->meta.flags.has_imm = 1;
        uint32_t decoded_imm = 0;
        switch (imm_len) {
            case 1:
                std::memcpy(&decoded_imm, ptr, 1);
                break;
            case 2:
                std::memcpy(&decoded_imm, ptr, 2);
                break;
            case 3:
                std::memcpy(&decoded_imm, ptr, 3);
                break;
            case 4:
                std::memcpy(&decoded_imm, ptr, 4);
                break;
            default:
                __builtin_unreachable();
        }
        inst->head.ext.data.imm = decoded_imm;
        ptr += imm_len;
    }

    op->SetLength(ptr - start);

    // For these scalar SSE encodings F3 selects the instruction, so it
    // cannot repeat or change EIP. Keep the legacy repeat boundary otherwise.
    const bool scalar_single = map == 1 &&
                               (op->prefixes.all & (prefix::REP | prefix::REPNE | prefix::OPSIZE)) == prefix::REP &&
                               (opcode == 0x10 || opcode == 0x11 || opcode == 0x58 || opcode == 0x59);
    bool is_rep = map <= 1 && (op->prefixes.flags.rep || op->prefixes.flags.repne) && !scalar_single;

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

    HandlerFunc h = g_Handlers[*handler_index];
    if (h) {
        op->handler = h;
    } else {
        // Fallback to UD2
        HandlerFunc ud2 = g_Handlers[0x10B];
        op->handler = ud2;
    }

    return InstructionDecodeResult::Success;
}

bool DecodeInstruction(const uint8_t* code, DecodedInstTmp* inst, uint16_t* handler_index) {
    return DecodeInstructionBounded(code, 15, inst, handler_index) == InstructionDecodeResult::Success;
}

InstructionDecodeResult FetchAndDecodeInstruction(EmuState* state, uint32_t eip, uint8_t* buffer, DecodedInstTmp* inst,
                                                  uint16_t* handler_index, uint16_t* raw_opcode,
                                                  DecodeHandlerMode mode) {
    const auto fetch = [&](uint32_t address) -> const std::byte* {
        if (mode == DecodeHandlerMode::Generic) {
            return state->mmu.resolve_safe_for_read(address, mem::Property::Exec);
        }
        const auto* code = state->mmu.translate_exec(state, address);
        if (state->status != EmuStatus::Running) return nullptr;
        if (!code) {
            state->status = EmuStatus::Fault;
            state->fault_vector = 14;
            state->fault_addr = address;
        }
        return code;
    };
    // A mapping is page-granular: copying bytes within the first page is safe,
    // but even probing the next mapping must wait until we know it is needed.
    size_t available = std::min<size_t>(15, 0x1000u - (eip & 0xFFFu));
    const std::byte* code = fetch(eip);
    if (!code) return InstructionDecodeResult::FetchFault;
    auto result = DecodeInstructionBounded(reinterpret_cast<const uint8_t*>(code), available, inst, handler_index,
                                           raw_opcode, mode);
    if (result == InstructionDecodeResult::Success) return result;
    std::memcpy(buffer, code, available);
    if (result != InstructionDecodeResult::NeedMoreBytes) return result;
    for (;;) {
        // Bounded decoding returns Invalid rather than requesting a 16th byte.
        code = fetch(eip + static_cast<uint32_t>(available));
        if (!code) return InstructionDecodeResult::FetchFault;
        buffer[available++] = static_cast<uint8_t>(*code);
        result = DecodeInstructionBounded(buffer, available, inst, handler_index, raw_opcode, mode);
        if (result != InstructionDecodeResult::NeedMoreBytes) return result;
    }
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

static bool UsesBranchCarryingSentinel(uint16_t handler_index, const DecodedInstruction& op) {
    if (handler_index != 0xFF) return false;
    const uint8_t subop = (op.modrm >> 3) & 7;
    return subop == 2 || subop == 4;
}

static RelativeBranchWidth GetRelativeBranchWidth(uint16_t opcode, bool operand16) {
    if (!IsDirectRelativeJmpHandlerIndex(opcode) && !IsDirectRelativeJccHandlerIndex(opcode) &&
        !IsDirectRelativeCallHandlerIndex(opcode))
        return RelativeBranchWidth::None;
    if (opcode == 0xEB || (opcode >= 0x70 && opcode <= 0x7F) || (opcode >= 0xE0 && opcode <= 0xE3))
        return RelativeBranchWidth::Byte;
    return operand16 ? RelativeBranchWidth::Word : RelativeBranchWidth::Dword;
}

static ScalarBindingIndex FinalizeDecodedInstructionAt(uint32_t eip, uint16_t handler_index, DecodedInstruction* op,
                                                       DecodeHandlerMode mode) {
    op->next_eip = eip + op->GetLength();
    auto binding = ScalarBindingIndex::Unknown;
    if (mode == DecodeHandlerMode::Native) {
        if (handler_index < kOpcodeBindings.size()) {
            const auto& opcode = kOpcodeBindings[handler_index];
            binding = opcode.fallback;
            if (opcode.has_variants || g_HasRuntimeSpecializations.load(std::memory_order_acquire)) {
                if (const auto selected = FindSpecializedHandler(handler_index, op); selected.handler) {
                    op->handler = selected.handler;
                    binding = selected.binding;
                }
            }
        }
    } else if (auto generic = FindGenericInstructionHandler(handler_index, op)) {
        op->handler = generic;
    }
    const auto width = GetRelativeBranchWidth(handler_index, op->prefixes.flags.opsize);
    if (width != RelativeBranchWidth::None) {
        SetExtKind(op, ExtKind::ControlFlow);
        op->ext.control.target_eip = RelativeBranchTarget(op->next_eip, GetImm(op), width);
        op->ext.control.relative_width = width;
    }
    return binding;
}

void FinalizeDecodedInstruction(EmuState*, uint32_t eip, uint16_t handler_index, DecodedInstruction* op,
                                DecodeHandlerMode mode) {
    FinalizeDecodedInstructionAt(eip, handler_index, op, mode);
}

static bool DecodedInstructionSemanticallyEquals(const DecodedInstruction& decoded, const DecodedInstruction& cached) {
    if (decoded.handler != cached.handler || decoded.next_eip != cached.next_eip || decoded.len != cached.len ||
        decoded.modrm != cached.modrm || decoded.prefixes.all != cached.prefixes.all ||
        decoded.meta.all != cached.meta.all) {
        return false;
    }

    switch (GetExtKind(&decoded)) {
        case ExtKind::Data:
            return std::memcmp(&decoded.ext.data, &cached.ext.data, sizeof(decoded.ext.data)) == 0;
        case ExtKind::Link:
            return decoded.ext.link.reserved == cached.ext.link.reserved;
        case ExtKind::ControlFlow:
            return decoded.ext.control.imm == cached.ext.control.imm &&
                   decoded.ext.control.target_eip == cached.ext.control.target_eip;
    }

    return false;
}

bool CopyDecodedInstructionBytes(EmuState* state, const DecodedInstruction& published, std::span<uint8_t> bytes) {
    if (bytes.empty() || bytes.size() != published.len) return false;
    uint32_t eip = published.next_eip - published.len;
    while (!bytes.empty()) {
        const auto* code = state->mmu.resolve_safe_for_read(eip, mem::Property::Exec);
        if (!code) return false;
        const size_t count = std::min<size_t>(bytes.size(), 0x1000u - (eip & 0xFFFu));
        std::memcpy(bytes.data(), code, count);
        bytes = bytes.subspan(count);
        eip += static_cast<uint32_t>(count);
    }
    return true;
}

bool RedecodeGenericInstruction(std::span<const uint8_t> bytes, const DecodedInstruction& published,
                                DecodedInstruction& generic) {
    if (bytes.empty() || bytes.size() != published.len) return false;
    constexpr auto mode = DecodeHandlerMode::Generic;
    const uint32_t eip = published.next_eip - published.len;
    uint16_t opcode;
    DecodedInstTmp inst;
    if (DecodeInstructionBounded(bytes.data(), bytes.size(), &inst, &opcode, nullptr, mode) !=
        InstructionDecodeResult::Success)
        return false;
    FinalizeDecodedInstructionAt(eip, opcode, &inst.head, mode);

    // Use the final block's slot boundaries. In particular, DecodeBlock can
    // absorb one or more NOPs into an instruction's length.
    bytes = bytes.subspan(inst.head.len);
    while (!bytes.empty()) {
        DecodedInstTmp nop;
        if (IsControlFlow(inst) ||
            DecodeInstructionBounded(bytes.data(), bytes.size(), &nop, &opcode, nullptr, mode) !=
                InstructionDecodeResult::Success ||
            (opcode != 0x90 && opcode != 0x11f))
            return false;
        bytes = bytes.subspan(nop.head.len);
    }
    generic = inst.head;
    generic.len = published.len;
    generic.next_eip = published.next_eip;
    generic.meta.flags.no_flags = published.meta.flags.no_flags;
    auto expected = published;
    expected.handler = generic.handler;
    expected.meta.flags.operand_lowered = 0;
    return DecodedInstructionSemanticallyEquals(generic, expected);
}

bool RedecodeGenericInstruction(EmuState* state, const DecodedInstruction& published, DecodedInstruction& generic) {
    uint8_t buffer[UINT8_MAX];
    const auto bytes = std::span(buffer).first(published.len);
    return CopyDecodedInstructionBytes(state, published, bytes) &&
           RedecodeGenericInstruction(bytes, published, generic);
}

static bool BasicBlockMatchesDecodedCandidate(const BasicBlock* candidate, uint32_t start_eip, uint32_t end_eip,
                                              uint32_t inst_count, uint32_t slot_count, BlockTerminalKind terminal_kind,
                                              bool compare_scalar, std::span<const DecodedInstTmp> temp_ops,
                                              std::span<const ScalarBindingIndex> binding_indices) {
    if (!candidate || candidate->is_valid()) return false;
    if (candidate->canonical_start_eip() != start_eip || candidate->end_eip != end_eip ||
        candidate->inst_count() != inst_count || candidate->terminal_kind() != terminal_kind) {
        return false;
    }

    for (uint32_t i = 0; i < slot_count; ++i) {
        auto slot = candidate->ReadOp(i);
        if (compare_scalar && i < inst_count) {
            slot.handler = candidate->UnfusedHandlers()[i];
            slot.meta.flags.operand_lowered = 0;
        }
        auto decoded = temp_ops[i].head;
        if (!compare_scalar && i < inst_count) {
            if (const auto binding = BindScalarHandler(decoded, binding_indices[i]); binding.handler)
                decoded.handler = binding.handler;
        }
        if (!DecodedInstructionSemanticallyEquals(decoded, slot)) return false;
    }

    return true;
}

BasicBlock* DecodeBlock(EmuState* state, uint32_t start_eip, uint32_t limit_eip, uint64_t max_insts,
                        BasicBlock* invalidated_candidate, bool* use_single_step) {
    if (use_single_step) *use_single_step = false;
    const uint64_t reset_request_epoch = state->mmu.code_cache().reset_request_epoch;
    if (state->mmu.code_cache_flush_pending()) {
        if (use_single_step) *use_single_step = true;
        return nullptr;
    }
    // At most 64 decoded ops, followed by a sentinel. A fault can append a
    // synthetic op and sentinel while the loop still has room for an op.
    constexpr size_t MAX_INSTS = 64;
    std::array<DecodedInstTmp, MAX_INSTS + 1> op_storage;
    size_t op_count = 0;
    std::array<uint16_t, MAX_INSTS> index_storage;
    std::array<ScalarBindingIndex, MAX_INSTS> binding_storage;
    size_t index_count = 0;

    uint32_t current_eip = start_eip;
    uint32_t inst_count = 0;

    uint64_t effective_limit = std::min<uint64_t>(MAX_INSTS, max_insts == 0 ? MAX_INSTS : max_insts);

    // DFE Live Flags - declared here to avoid goto skip error
    uint32_t live_flags = DFE_ALL_FLAGS;

    uint32_t end_eip = start_eip;
    bool success = true;
    BlockStopReason stop_reason = BlockStopReason::Unknown;

    while (op_count < effective_limit) {
        // 0. Check Limit
        if (limit_eip != 0 && current_eip >= limit_eip) {
            stop_reason = BlockStopReason::LimitEip;
            break;
        }
        // A block may span already executable pages (its cache tracks every
        // backing page), but decoding must not raise a later fetch fault before
        // earlier instructions have committed. Defer potentially missing pages
        // to a fresh block without invoking a guest fault resolver speculatively.
        if (op_count != 0) {
            const uint32_t remaining = 0x1000u - (current_eip & 0xFFFu);
            if (!state->mmu.probe_exec(current_eip) ||
                (remaining < 15 && !state->mmu.probe_exec(current_eip + remaining))) {
                stop_reason = BlockStopReason::PageCross;
                break;
            }
        }
        uint8_t buf[15] = {};
        DecodedInstTmp inst{};
        uint16_t handler_index = 0;
        uint16_t raw_opcode = 0;
        const auto decode_result =
            FetchAndDecodeInstruction(state, current_eip, buf, &inst, &handler_index, &raw_opcode);
        if (decode_result == InstructionDecodeResult::FetchFault) {
            success = false;
            stop_reason = BlockStopReason::FetchFault;
            break;
        }

        if (decode_result != InstructionDecodeResult::Success) {
            // Decode error: Insert Fault Op
            fprintf(stderr,
                    "[DecodeBlock] DecodeInstruction Failed at %08X. Bytes: %02X %02X "
                    "%02X %02X\n",
                    current_eip, buf[0], buf[1], buf[2], buf[3]);
            inst = {};
            inst.head.SetLength(0);  // Fault: EIP points to instruction

            HandlerFunc ud2 = g_Handlers[0x10B];  // UD2
            inst.head.handler = ud2;

            inst.head.next_eip = current_eip;
            binding_storage[op_count] = kOpcodeBindings[0x10B].fallback;
            op_storage[op_count++] = inst;
            inst_count++;  // The synthetic #UD op must be executable, not an empty block.

            // Append Sentinel for dispatch safety
            DecodedInstTmp sentinel{};

            HandlerFunc exit_h = g_ExitHandlersFallthrough[0];
            sentinel.head.handler = exit_h;

            // Sentinel next_block initialization to dummy
            SetExtKind(&sentinel.head, ExtKind::Link);
            sentinel.head.next_eip = current_eip;

            op_storage[op_count++] = sentinel;

            end_eip = current_eip + 1;
            stop_reason = BlockStopReason::DecodeFault;
            goto finalize;
        }

        const auto binding =
            FinalizeDecodedInstructionAt(current_eip, handler_index, &inst.head, DecodeHandlerMode::Native);

        index_storage[index_count++] = raw_opcode;

        // Optimization: Skip NOPs by absorbing them into the previous instruction
        // unless it's the first instruction of the block
        bool is_nop = (handler_index == 0x90 || handler_index == 0x11F);
        if (is_nop && op_count != 0 && !IsControlFlow(op_storage[op_count - 1])) {
            // "Absorb" NOP into the previous instruction
            DecodedInstTmp& prev = op_storage[op_count - 1];
            uint32_t new_len = (uint32_t)prev.head.GetLength() + inst.head.GetLength();
            if (new_len <= 255) {
                prev.head.next_eip += inst.head.GetLength();
                prev.head.SetLength((uint8_t)new_len);
                --index_count;
                // Advance EIP and continue
                current_eip += inst.head.GetLength();
                continue;
            }
        }

        binding_storage[op_count] = binding;
        op_storage[op_count++] = inst;
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
        if (IsControlFlow(op_storage[op_count - 1])) {
            stop_reason = BlockStopReason::ControlFlow;
            break;
        }

        // Continue across mapped executable pages. The next iteration probes
        // before decoding so a missing mapping cannot fault this block early.
    }

    if (stop_reason == BlockStopReason::Unknown && op_count >= effective_limit) {
        stop_reason = BlockStopReason::MaxInsts;
    }

    if (!success || op_count == 0) return nullptr;

    // Append Sentinel Op
    {
        DecodedInstTmp sentinel{};

        uint32_t k = (uint32_t)current_eip;
        k ^= k >> 12;
        k ^= k >> 4;
        const bool branch_carrying_sentinel =
            index_count != 0 &&
            UsesBranchCarryingSentinel(index_storage[index_count - 1], op_storage[op_count - 1].head);
        uint8_t exit_idx = k % kExitHandlerReplicaCount;
        HandlerFunc exit_h =
            branch_carrying_sentinel ? g_ExitHandlersBranch[exit_idx] : g_ExitHandlersFallthrough[exit_idx];
        sentinel.head.handler = exit_h;
        sentinel.head.next_eip = op_storage[op_count - 1].head.next_eip;  // Copy next_eip from last op
        SetExtKind(&sentinel.head, ExtKind::Link);
        op_storage[op_count++] = sentinel;
    }
    end_eip = current_eip;

#if 1
    // DFE Optimization (Backward Pass)
    live_flags = DFE_ALL_FLAGS;
    for (int i = (int)op_count - 2; i >= 0; --i) {
        DecodedInstruction& op = op_storage[i].head;
        if (i >= (int)index_count) break;

        uint16_t h_idx = index_storage[i];
        uint32_t reads = 0;
        uint32_t writes = 0;
        const uint16_t flat_idx = h_idx;
        // The supported 0F 38/3A SIMD forms do not read or modify flags.
        const auto info = flat_idx < std::size(kOpFlagTable) ? kOpFlagTable[flat_idx] : DfeOpInfo{};

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
            case DFE_TYPE_GROUP2: {
                const uint16_t opcode_idx = flat_idx;
                const uint8_t subop = (op.modrm >> 3) & 7;
                const bool carry_rotate = subop == 2 || subop == 3;
                reads = carry_rotate ? DFE_CF_MASK : 0;
                // CL can be zero at runtime, so none of the incoming flags can
                // be killed unconditionally. This only affects decode-time DFE.
                if (opcode_idx == 0xD2 || opcode_idx == 0xD3) break;
                const uint32_t count = (opcode_idx == 0xD0 || opcode_idx == 0xD1) ? 1 : GetImm(&op) & 31;
                const uint32_t width = (opcode_idx == 0xC0 || opcode_idx == 0xD0) ? 8
                                       : op.prefixes.flags.opsize                 ? 16
                                                                                  : 32;
                if (count == 0 || (carry_rotate && count % (width + 1) == 0)) break;
                // Rotates preserve OF for multiple bits in the interpreter.
                // Shifts clear the arithmetic flags before calculating new ones.
                writes = subop < 4 ? CF_MASK | (count == 1 ? OF_MASK : 0) : DFE_ALL_FLAGS;
                break;
            }
            case DFE_TYPE_GROUP3: {
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg < 2 || reg == 3) {  // TEST, NEG
                    writes = DFE_ALL_FLAGS;
                } else if (reg == 4 || reg == 5) {  // MUL, IMUL
                    writes = CF_MASK | OF_MASK;
                } else if (reg >= 6) {  // DIV, IDIV may fault and preserve the cached flags.
                    reads = DFE_ALL_FLAGS;
                }
                break;
            }
            case DFE_TYPE_GROUP4: {
                uint8_t reg = (op.modrm >> 3) & 7;
                if (reg == 0 || reg == 1) {  // INC, DEC
                    writes = DFE_ALL_FLAGS & ~DFE_CF_MASK;
                }
                // Group 5 may fault before updating flags or access the stack
                // even when its explicit operand is a register (CALL, PUSH).
                if (reg > 1 && (flat_idx == 0xfe || reg != 4)) reads = DFE_ALL_FLAGS;
                break;
            }
            case DFE_TYPE_REGISTER_FLAGS:
                // Leave flags materialized at a possible memory fault.
                if (HasMem(&op) || (flat_idx == 0x1ba && ((op.modrm >> 3) & 7) < 4)) {
                    reads = DFE_ALL_FLAGS;
                } else {
                    writes = info.write_mask;
                    if (flat_idx == 0x1bc && op.prefixes.flags.rep) writes |= CF_MASK;
                }
                break;
            case DFE_TYPE_DOUBLE_SHIFT_IMMEDIATE:
                if (HasMem(&op)) {
                    reads = DFE_ALL_FLAGS;
                } else if (GetImm(&op) & (op.prefixes.flags.opsize ? 15 : 31)) {
                    writes = info.write_mask;
                }
                break;
            default:
                reads = DFE_ALL_FLAGS;
                break;
        }

        // A restart or fault may expose the flags from before the instruction.
        if (HasMem(&op) && flat_idx != 0x8d) reads |= DFE_ALL_FLAGS;  // LEA has no memory access.

        if (writes != 0 && (writes & live_flags) == 0) op.meta.flags.no_flags = 1;
        live_flags &= ~writes;
        live_flags |= reads;
    }
#endif

finalize:
    if (!success || op_count == 0) return nullptr;

    // An executable-fetch callback may have invalidated the candidate (or the
    // source block held by a concatenation builder). The pool is still alive,
    // but neither the candidate nor the decoded bytes may now be published.
    if (state->mmu.code_cache_flush_pending() || state->mmu.code_cache().reset_request_epoch != reset_request_epoch) {
        if (use_single_step) *use_single_step = true;
        return nullptr;
    }

    state->block_stats.Record(inst_count, stop_reason);

    const uint32_t slot_count = static_cast<uint32_t>(op_count);
    const uint32_t decoded_inst_count = slot_count == 0 ? 0 : slot_count - 1;
    auto* scalar_ops = reinterpret_cast<DecodedInstruction*>(op_storage.data());
    const auto binding_indices = std::span(binding_storage).first(decoded_inst_count);

    BlockTerminalKind terminal_kind = BlockTerminalKind::None;
    if (decoded_inst_count != 0 && index_count != 0) {
        const uint16_t last_handler_index = index_storage[index_count - 1];
        const DecodedInstruction& last_op = op_storage[decoded_inst_count - 1].head;
        if (IsDirectRelativeJmpHandlerIndex(last_handler_index)) {
            terminal_kind = BlockTerminalKind::DirectJmpRel;
        } else if (IsDirectRelativeJccHandlerIndex(last_handler_index)) {
            terminal_kind = BlockTerminalKind::DirectJccRel;
        } else if (last_op.meta.flags.is_control_flow) {
            terminal_kind = BlockTerminalKind::OtherControlFlow;
        }
    }

    const auto matches_candidate = [&](bool compare_scalar) {
        return BasicBlockMatchesDecodedCandidate(invalidated_candidate, start_eip, end_eip, inst_count, slot_count,
                                                 terminal_kind, compare_scalar, std::span(op_storage.data(), op_count),
                                                 binding_indices);
    };
    const bool candidate_has_jit = invalidated_candidate && invalidated_candidate->has_unfused_handlers &&
                                   invalidated_candidate->entry != invalidated_candidate->FirstOp()->handler;
    uint16_t hot_count = 0;
#if FIBERCPU_BLOCK_COMPILATION
    if (candidate_has_jit && matches_candidate(true)) {
        invalidated_candidate->Revalidate();
        return invalidated_candidate;
    }
    hot_count = decoded_inst_count ? block_recipe::HotThreshold(*state) : 0;
    if (hot_count && candidate_has_jit) hot_count = 1;
    std::array<HandlerFunc, MAX_INSTS> scalar_handlers;
    if (hot_count) {
        for (uint32_t i = 0; i < decoded_inst_count; ++i) scalar_handlers[i] = scalar_ops[i].handler;
    }
#endif
    ApplySuperOpcodesToBlockOps(scalar_ops, decoded_inst_count, false, binding_indices);
    if (!candidate_has_jit && matches_candidate(false)) {
        invalidated_candidate->Revalidate();
        return invalidated_candidate;
    }

    const std::span<const DecodedInstruction> final_ops(scalar_ops, slot_count);
    const auto layout = BlockSlotLayout::Inspect(start_eip, final_ops);
    bool retain_region_context = false;
#if FIBERCPU_LLVM_JIT
    retain_region_context = hot_count && LLVMPageRegionsEnabled(*state);
#endif
#if FIBERCPU_LINKED_BLOCK_CACHE
    retain_region_context |= hot_count && LinkedPageRegionsEnabled();
#endif
    bool weighted_profile = false;
#if FIBERCPU_LLVM_JIT
    weighted_profile = hot_count && LLVMWeightedProfileEnabled(*state);
#endif
#if defined(FIBERCPU_ENABLE_BLOCK_PROFILE) || defined(FIBERCPU_ENABLE_HANDLER_PROFILE)
    const bool retain_exec_count = true;
#elif FIBERCPU_LLVM_JIT
    const bool retain_exec_count = weighted_profile || (hot_count && LLVMJitTraceEnabled());
#else
    const bool retain_exec_count = false;
#endif
    size_t alloc_size = BasicBlock::CalculateSize(layout, hot_count != 0, retain_region_context, retain_exec_count);
    void* mem = state->mmu.try_allocate_block_bytes(alloc_size, start_eip);
    if (!mem) {
        if (use_single_step) *use_single_step = true;
        return nullptr;
    }
    BasicBlock* block = new (mem) BasicBlock;
    state->mmu.remember_allocated_block(block);

    block->set_start_eip(start_eip);
    block->set_valid(true);
    block->end_eip = end_eip;
    block->set_inst_count(inst_count);
    block->set_terminal_kind(terminal_kind);
    block->chain.llvm_hot_count = hot_count;
    block->chain.weighted_profile = weighted_profile;
    block->has_unfused_handlers = hot_count != 0;
    block->chain.has_region_context = retain_region_context;
    block->chain.has_exec_count = retain_exec_count;

    block->WriteOps(layout, final_ops, state->mmu.invalid_code_block(), false, binding_indices);
    if (retain_region_context) block->SetRegionContext(nullptr);
    if (retain_exec_count) block->ExecCountStorage() = 0;
#if FIBERCPU_BLOCK_COMPILATION
    if (hot_count) std::copy_n(scalar_handlers.data(), decoded_inst_count, block->UnfusedHandlers());
#endif

    return block;
}

// ----------------------------------------------------------------------------
// BasicBlock Implementation
// ----------------------------------------------------------------------------

namespace {

uint64_t PackOperand(const DecodedInstruction& op) {
    const auto common = DecodedOp::PackOperandHeader(op.modrm, op.prefixes.all, op.meta.all);
    if (!HasMem(&op)) {
        const auto ext_kind = GetExtKind(&op);
        const auto branch_width =
            ext_kind == ExtKind::ControlFlow ? op.ext.control.relative_width : RelativeBranchWidth::None;
        const auto value = branch_width == RelativeBranchWidth::None ? GetImm(&op) : GetControlTargetEip(&op);
        if (ext_kind == ExtKind::ControlFlow) {
            // Only the opcode low bits occupy the ModRM byte on these forms.
            assert(!op.meta.flags.has_modrm && op.modrm < 8);
            return common | DecodedOp::kBranchTag | (uint64_t(branch_width) << DecodedOp::kBranchWidthShift) | value;
        }
        return common | (uint64_t(ext_kind) << DecodedOp::kAddressShift) | value;
    }
    assert(op.meta.flags.has_modrm);
    return DecodedOp::PackMemoryPayload(common, GetAddressDescriptor(&op), GetDisplacement(&op), GetImm(&op));
}

}  // namespace

BlockSlotLayout BlockSlotLayout::Inspect(uint32_t start_eip, std::span<const DecodedInstruction> ops) {
    assert(!ops.empty() && ops.size() <= 65);
    BlockSlotLayout layout{static_cast<uint8_t>(ops.size()), 0, 0};
    auto expected = start_eip;
    for (const auto& op : ops.first(ops.size() - 1)) {
        if (op.next_eip - op.len != expected) ++layout.anchor_count;
        expected = op.next_eip;
        if (HasExtraOperandSlot(op.meta.all, op.prefixes.all, GetImm(&op))) ++layout.data_slot_count;
    }
    return layout;
}

void BasicBlock::WriteOps(BlockSlotLayout layout, std::span<const DecodedInstruction> ops, BasicBlock* invalid,
                          bool preserve_handlers, std::span<const ScalarBindingIndex> binding_indices) {
    slot_count = static_cast<uint8_t>(layout.PhysicalSlotCount());
    sentinel_slot_index = slot_count - 1;
    set_inst_count(layout.logical_slot_count - 1);
    link_only_return_prefixes = 0;
    anchor_count = layout.anchor_count;
    sentinel_eip = ops.back().next_eip;

    const auto anchor_offset = AnchorOffset();
    auto* anchors =
        anchor_count ? reinterpret_cast<uint64_t*>(reinterpret_cast<std::byte*>(this) + anchor_offset) : nullptr;
    auto* adjustments = anchors ? reinterpret_cast<uint32_t*>(anchors + 1) : nullptr;
    uint64_t data_mask = 0;
    if (anchors) *anchors = 0;
    uint16_t offset = 0;
    uint32_t expected = start_eip();
    auto* ends = const_cast<uint16_t*>(PCEnds());
    uint32_t physical_index = 0;
    for (unsigned i = 0; i < inst_count(); ++i) {
        const auto& op = ops[i];
        const uint64_t bit = uint64_t{1} << i;
        if (op.next_eip - op.len != expected) {
            *anchors |= bit;
            *adjustments++ = op.next_eip - op.len - (start_eip() + offset);
        }
        offset += op.len;
        ends[i] = offset;
        expected = op.next_eip;
        auto& slot = FirstOp()[physical_index++];
        if (fiberish::IsLinkOnlyReturn(op.meta.all)) {
            assert(i + 1 == inst_count() && !op.prefixes.flags.lock);
            link_only_return_prefixes = op.prefixes.all | prefix::LOCK;
            slot.StoreBlockLink(invalid);
        } else {
            slot.payload = PackOperand(op);
        }
        HandlerFunc target = op.handler;
        if (!preserve_handlers) {
            const auto index = binding_indices.empty() ? ScalarBindingIndex::Unknown : binding_indices[i];
            const auto binding = BindScalarHandler(op, index);
            if (binding.handler) target = binding.handler;
        }
        slot.SetDispatchHandler(target);
        const bool branch_link = HasBranchLinkSlot(op.meta.all);
        const bool memory_immediate = compact_imm::NeedsSeparateSlot(op);
        if (branch_link || memory_immediate) {
            data_mask |= bit;
            auto& data = FirstOp()[physical_index++];
            data.SetDispatchHandler(nullptr);
            if (branch_link) {
                data.StoreBlockLink(invalid);
            } else {
                data.payload = GetImm(&op);
            }
        }
    }
    assert(physical_index == sentinel_slot_index);
    if (HasDataSlots())
        *reinterpret_cast<uint64_t*>(reinterpret_cast<std::byte*>(this) + DataSlotMaskOffset()) = data_mask;
    Sentinel()->SetDispatchHandler(ops.back().handler);
    entry = FirstOp()->handler;
    SetFallthroughLink(invalid);
}

DecodedInstruction BasicBlock::ReadOp(size_t slot) const {
    const auto& op = *LogicalOp(slot);
    DecodedInstruction result{};
    result.handler = op.OriginalHandler();
    result.next_eip = GetNextEip(slot);
    if (slot == inst_count()) {
        SetExtKind(&result, ExtKind::Link);
        return result;
    }
    result.len = GetOpLength(slot);
    if (IsLinkOnlyReturn(&op)) {
        result.modrm = 0xC3 & 7;
        result.prefixes.all = link_only_return_prefixes & ~prefix::LOCK;
        result.meta.flags.is_control_flow = 1;
        SetExtKind(&result, ExtKind::ControlFlow);
        return result;
    }
    // Runtime handlers get a bound constant. Only the decoded snapshot
    // needs the original encoding for concatenation, re-decode and export.
    result.modrm = uint8_t(op.payload >> DecodedOp::kModRMShift);
    if (op.HasBranchOperands()) result.modrm &= 7;
    result.prefixes = op.GetPrefixes();
    result.meta = op.GetMeta();
    if (GetExtKind(&result) == ExtKind::ControlFlow) {
        const auto width = op.BranchWidth();
        const auto target = width == RelativeBranchWidth::None ? 0 : op.Primary();
        uint32_t displacement = op.Primary() - result.next_eip;
        switch (width) {
            case RelativeBranchWidth::Byte:
                displacement = uint8_t(displacement);
                break;
            case RelativeBranchWidth::Word:
                displacement = uint16_t(displacement);
                break;
            case RelativeBranchWidth::Dword:
                break;
            case RelativeBranchWidth::None:
                displacement = op.Primary();
                break;
        }
        result.ext.control = {displacement, target, width, {}};
    } else if (op.HasMemory()) {
        result.ext.data = {GetImm(&op), op.AddressDescriptor(), op.Primary(), 0};
    } else {
        result.ext.data.imm = op.Primary();
    }
    return result;
}

void BasicBlock::Revalidate() { set_valid(true); }

}  // namespace fiberish
