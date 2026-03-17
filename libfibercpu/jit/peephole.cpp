#include "peephole.h"

namespace fiberish::jit {

namespace {

constexpr uint32_t kNop = 0xD503201F;
constexpr uint32_t kBranchMask = 0xFC000000;
constexpr uint32_t kBranchOpcode = 0x14000000;
constexpr uint32_t kPrfmMask = 0xFFC00000;
constexpr uint32_t kPrfmOpcode = 0xF9800000;
constexpr uint32_t kMovWideMask = 0x7F800000;
constexpr uint32_t kMovZ64Opcode = 0xD2800000;
constexpr uint32_t kMovK64Opcode = 0xF2800000;
constexpr uint32_t kUbfmMask = 0x7F800000;
constexpr uint32_t kUbfmOpcode = 0x53000000;

int32_t DecodeBranchDelta(uint32_t inst) {
    int32_t imm26 = static_cast<int32_t>(inst & 0x03FFFFFFu);
    if ((imm26 & 0x02000000) != 0) {
        imm26 |= ~0x03FFFFFF;
    }
    return imm26 << 2;
}

void TryElideBranchToNext(uint32_t* inst_ptr, PeepholeStats* stats) {
    const uint32_t inst = *inst_ptr;
    if ((inst & kBranchMask) != kBranchOpcode) return;

    const auto* pc = reinterpret_cast<const uint8_t*>(inst_ptr);
    const auto* target = pc + DecodeBranchDelta(inst);
    if (target != pc + sizeof(uint32_t)) return;

    *inst_ptr = kNop;
    stats->branch_to_next_nops++;
}

void TryElidePrfm(uint32_t* inst_ptr, PeepholeStats* stats) {
    const uint32_t inst = *inst_ptr;
    if ((inst & kPrfmMask) != kPrfmOpcode) return;

    *inst_ptr = kNop;
    stats->prfm_nops++;
}

uint32_t GetRd(uint32_t inst) { return inst & 0x1F; }
uint32_t GetRn(uint32_t inst) { return (inst >> 5) & 0x1F; }
uint32_t GetRm(uint32_t inst) { return (inst >> 16) & 0x1F; }
uint32_t GetRa(uint32_t inst) { return (inst >> 10) & 0x1F; }

bool IsMovZ64(uint32_t inst) { return (inst & kMovWideMask) == kMovZ64Opcode; }
bool IsMovK64(uint32_t inst) { return (inst & kMovWideMask) == kMovK64Opcode; }
bool IsUbfm(uint32_t inst) { return (inst & kUbfmMask) == kUbfmOpcode; }

uint64_t DecodeMovWideImm64(std::span<uint32_t, 4> insts, uint32_t* rd) {
    uint64_t value = 0;
    *rd = GetRd(insts[0]);
    for (size_t i = 0; i < insts.size(); ++i) {
        const uint32_t inst = insts[i];
        const uint32_t this_rd = GetRd(inst);
        if (this_rd != *rd) return UINT64_MAX;
        if (i == 0) {
            if (!IsMovZ64(inst)) return UINT64_MAX;
        } else if (!IsMovK64(inst)) {
            return UINT64_MAX;
        }
        const uint32_t imm16 = (inst >> 5) & 0xFFFF;
        const uint32_t hw = (inst >> 21) & 0x3;
        value &= ~(0xFFFFull << (hw * 16));
        value |= static_cast<uint64_t>(imm16) << (hw * 16);
    }
    return value;
}

uint32_t EncodeMovZ64(uint32_t rd, uint16_t imm16, uint32_t shift) {
    return kMovZ64Opcode | ((shift / 16) << 21) | (static_cast<uint32_t>(imm16) << 5) | rd;
}

uint32_t EncodeMovK64(uint32_t rd, uint16_t imm16, uint32_t shift) {
    return kMovK64Opcode | ((shift / 16) << 21) | (static_cast<uint32_t>(imm16) << 5) | rd;
}

void EmitMovImm64(uint32_t* out, uint32_t rd, uint64_t value) {
    out[0] = EncodeMovZ64(rd, static_cast<uint16_t>(value & 0xFFFF), 0);
    out[1] = EncodeMovK64(rd, static_cast<uint16_t>((value >> 16) & 0xFFFF), 16);
    out[2] = EncodeMovK64(rd, static_cast<uint16_t>((value >> 32) & 0xFFFF), 32);
    out[3] = EncodeMovK64(rd, static_cast<uint16_t>((value >> 48) & 0xFFFF), 48);
}

bool MentionsRegister(uint32_t inst, uint32_t reg) {
    return GetRd(inst) == reg || GetRn(inst) == reg || GetRm(inst) == reg || GetRa(inst) == reg;
}

bool SourceRegisterIsDeadAfter(std::span<uint32_t> rest, uint32_t reg) {
    for (uint32_t inst : rest) {
        if (MentionsRegister(inst, reg)) return false;
    }
    return true;
}

bool TryFoldMovWideIntoUbfm(uint32_t* window_start, std::span<uint32_t> rest_in_op, PeepholeStats* stats) {
    std::span<uint32_t, 4> chain(window_start, 4);
    uint32_t src_reg = 0;
    const uint64_t value = DecodeMovWideImm64(chain, &src_reg);
    if (value == UINT64_MAX) return false;

    uint32_t& use_inst = window_start[4];
    if (!IsUbfm(use_inst) || GetRn(use_inst) != src_reg) return false;

    const uint32_t dst_reg = GetRd(use_inst);
    const uint32_t sf = (use_inst >> 31) & 0x1;
    const uint32_t n = (use_inst >> 22) & 0x1;
    const uint32_t immr = (use_inst >> 16) & 0x3F;
    const uint32_t imms = (use_inst >> 10) & 0x3F;
    if (sf != 1 || n != 1) return false;
    if (imms < immr) return false;
    if (!SourceRegisterIsDeadAfter(rest_in_op.subspan(5), src_reg)) return false;

    const uint32_t width = imms - immr + 1;
    const uint64_t mask = (width >= 64) ? ~0ull : ((1ull << width) - 1ull);
    const uint64_t folded = (value >> immr) & mask;

    EmitMovImm64(window_start, dst_reg, folded);
    use_inst = kNop;
    stats->movwide_ubfm_constant_folds++;
    return true;
}

}  // namespace

PeepholeStats OptimizeBlockInPlace(uint8_t* block_start, uint8_t* block_end, std::span<const JitOpRange> ops) {
    (void)block_start;
    PeepholeStats stats;
    for (const JitOpRange& op : ops) {
        auto* op_words = reinterpret_cast<uint32_t*>(op.start);
        const size_t op_word_count = static_cast<size_t>(op.end - op.start) / sizeof(uint32_t);
        for (size_t word_index = 0; word_index < op_word_count; ++word_index) {
            uint8_t* ptr = op.start + word_index * sizeof(uint32_t);
            if (ptr + sizeof(uint32_t) > block_end) break;
            auto* inst_ptr = reinterpret_cast<uint32_t*>(ptr);
            TryElidePrfm(inst_ptr, &stats);
            TryElideBranchToNext(inst_ptr, &stats);
            if (word_index + 5 <= op_word_count) {
                std::span<uint32_t> remaining(op_words + word_index, op_word_count - word_index);
                if (TryFoldMovWideIntoUbfm(inst_ptr, remaining, &stats)) {
                    word_index += 4;
                }
            }
        }
    }
    return stats;
}

}  // namespace fiberish::jit
