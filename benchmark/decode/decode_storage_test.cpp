#include "bindings.h"
#include "state.h"
#include <cstdio>
#include <cstring>
#include <span>

using namespace fiberish;

int main() {
    constexpr uint32_t address = 0x1000;
    auto* state = X86_Create();
    state->status = EmuStatus::Running;
    auto* page = state->mmu.allocate_page(address, static_cast<uint8_t>(mem::Property::RX));
    if (!page)
        return 1;
    std::memset(page, 0x40, 4096); // inc eax; no control flow or absorbed NOPs
    auto check = [&](uint32_t limit, uint64_t max, uint32_t count, uint32_t end) {
        state->mmu.reset_code_cache();
        auto* block = DecodeBlock(state, address, limit, max);
        if (!block || block->inst_count() != count || block->slot_count != count + 1 || block->end_eip != end) {
            std::fprintf(stderr, "Unexpected block for limit=%x max=%llu\n", limit, (unsigned long long)max);
            return false;
        }
        return true;
    };
    bool ok = check(0, 0, 64, address + 64) && check(0, 1, 1, address + 1) && check(address + 17, 0, 17, address + 17);
    // Invalid 15-byte prefix sequence at the last available decoded-op slot.
    std::memset(page + 63, 0x66, 15);
    ok = check(0, 0, 64, address + 64) && ok;
    // A one-byte instruction at the end of a page must not fetch the next page.
    page[4095] = std::byte{0x40};
    uint8_t buffer[15]{};
    DecodedInstTmp decoded{};
    uint16_t handler = 0, raw = 0;
    auto result = FetchAndDecodeInstruction(state, address + 4095, buffer, &decoded, &handler, &raw);
    ok = ok && result == InstructionDecodeResult::Success && raw == 0x40 && decoded.head.GetLength() == 1;

    // An instruction split across pages uses the buffered retry path, including
    // the original opcode before specialized MOV handler remapping.
    auto* next = state->mmu.allocate_page(address + 4096, static_cast<uint8_t>(mem::Property::RX));
    if (!next) {
        X86_Destroy(state);
        return 1;
    }
    page[4095] = std::byte{0x66};
    next[0] = std::byte{0x8b};
    next[1] = std::byte{0xc0};
    result = FetchAndDecodeInstruction(state, address + 4095, buffer, &decoded, &handler, &raw);
    ok = ok && result == InstructionDecodeResult::Success && raw == 0x8b && decoded.head.GetLength() == 3;
    // Unprefixed MOV uses a specialized handler index; raw opcode is unchanged.
    page[0] = std::byte{0x8b};
    page[1] = std::byte{0xc0};
    result = FetchAndDecodeInstruction(state, address, buffer, &decoded, &handler, &raw);
    ok = ok && result == InstructionDecodeResult::Success && raw == 0x8b && handler != raw;
    // Check every immediate width and every truncated prefix of each encoding.
    auto check_immediate = [&](std::span<const uint8_t> code, size_t imm_size) {
        for (size_t length = 0; length < code.size(); ++length) {
            DecodedInstTmp inst{};
            uint16_t index = 0;
            if (DecodeInstructionBounded(code.data(), length, &inst, &index) != InstructionDecodeResult::NeedMoreBytes)
                return false;
        }
        DecodedInstTmp inst{};
        uint16_t index = 0;
        if (DecodeInstructionBounded(code.data(), code.size(), &inst, &index) != InstructionDecodeResult::Success)
            return false;
        uint32_t expected_imm = 0;
        std::memcpy(&expected_imm, code.data() + code.size() - imm_size, imm_size);
        return inst.head.GetLength() == code.size() && GetImm(&inst.head) == expected_imm;
    };
    const uint8_t imm8[] = {0xb0, 0xab};
    const uint8_t imm16[] = {0x66, 0xb8, 0x34, 0x12};
    const uint8_t imm24[] = {0xc8, 0x34, 0x12, 0x56};
    const uint8_t imm32[] = {0xb8, 0x78, 0x56, 0x34, 0x12};
    ok = check_immediate(imm8, 1) && check_immediate(imm16, 2) && check_immediate(imm24, 3) &&
         check_immediate(imm32, 4) && ok;
    if (!ok)
        std::fprintf(stderr, "Decode storage or fetch boundary check failed\n");
    X86_Destroy(state);
    return ok ? 0 : 1;
}
