#include "bindings.h"
#include "exec_utils.h"
#include "state.h"
#include <array>
#include <cstdio>
#include <cstring>
#include <memory>
#include <optional>
#include <span>

using namespace fiberish;

static bool CheckDfePreservesOverflow() {
    constexpr uint32_t address = 0x1000;
    constexpr uint32_t unmapped = 0x4000;
    constexpr uint8_t add[] = {0x05, 1, 0, 0, 0};        // ADD EAX, 1
    constexpr uint8_t sahf[] = {0x9e, 0x0f, 0x90, 0xc2}; // SAHF; SETO DL
    constexpr uint8_t xor_ebx[] = {0x31, 0xdb};

    const auto run = [&](std::span<const uint8_t> tail, const char* name, std::optional<uint32_t> fault_stack_pointer) {
        const std::unique_ptr<EmuState, decltype(&X86_Destroy)> state(X86_Create(), X86_Destroy);
        auto* cpu = state.get();
        cpu->status = EmuStatus::Running;
        auto* page = cpu->mmu.allocate_page(address, static_cast<uint8_t>(mem::Property::RX));
        if (!page)
            return false;

        std::memcpy(page, add, sizeof(add));
        std::memcpy(page + sizeof(add), tail.data(), tail.size());
        constexpr uint32_t fault_eip = address + sizeof(add);
        uint32_t end = fault_eip + static_cast<uint32_t>(tail.size());
        if (fault_stack_pointer) {
            // XOR would overwrite OF if execution reached it.
            std::memcpy(page + end - address, xor_ebx, sizeof(xor_ebx));
            end += sizeof(xor_ebx);
            X86_RegWrite(cpu, ESP, *fault_stack_pointer);
        }
        X86_RegWrite(cpu, EAX, 0x7fff'ffff);
        X86_SetEIP(cpu, address);
        X86_SetEFLAGS(cpu, 2);
        const auto* block = DecodeBlock(cpu, address, end, 0);
        const bool keeps_flags = block && !block->FirstOp()->meta.flags.no_flags;
        const bool ran = X86_Run(cpu, end, 16);
        const bool observed = fault_stack_pointer ? X86_GetEIP(cpu) == fault_eip && X86_GetFaultVector(cpu) == 14 &&
                                                        X86_GetFaultAddress(cpu) == unmapped
                                                  : X86_GetEIP(cpu) == end && (X86_RegRead(cpu, EDX) & 0xff) == 1;
        const bool overflow = X86_GetEFLAGS(cpu) & OF_MASK;
        const bool ok = keeps_flags && ran && observed && overflow;
        if (!ok)
            std::fprintf(stderr, "%s DFE check: live=%d ran=%d observed=%d OF=%d\n", name, keeps_flags, ran, observed,
                         overflow);
        return ok;
    };

    constexpr uint8_t push[] = {0x50};
    constexpr uint8_t pop[] = {0x58};
    constexpr uint8_t push_imm32[] = {0x68, 1, 0, 0, 0};
    constexpr uint8_t push_imm8[] = {0x6a, 1};
    constexpr uint8_t moffs_load[] = {0xa1, 0, 0x40, 0, 0};
    constexpr uint8_t moffs_store[] = {0xa3, 0, 0x40, 0, 0};
    constexpr uint8_t segment_load[] = {0x8e, 0x1d, 0, 0x40, 0, 0};  // MOV DS, [0x4000]
    constexpr uint8_t segment_store[] = {0x8c, 0x1d, 0, 0x40, 0, 0}; // MOV [0x4000], DS
    struct FaultCase {
        std::span<const uint8_t> code;
        const char* name;
        uint32_t stack_pointer;
    };
    const std::array cases{
        FaultCase{push, "PUSH", unmapped + 4},
        FaultCase{pop, "POP", unmapped},
        FaultCase{push_imm32, "PUSH imm32", unmapped + 4},
        FaultCase{push_imm8, "PUSH imm8", unmapped + 4},
        FaultCase{moffs_load, "MOV from moffs", unmapped},
        FaultCase{moffs_store, "MOV to moffs", unmapped},
        FaultCase{segment_load, "MOV to DS", unmapped},
        FaultCase{segment_store, "MOV from DS", unmapped},
    };
    bool ok = run(sahf, "SAHF", std::nullopt);
    for (const auto& [code, name, stack_pointer] : cases)
        ok = run(code, name, stack_pointer) && ok;
    return ok;
}

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
    return CheckDfePreservesOverflow() && ok ? 0 : 1;
}
