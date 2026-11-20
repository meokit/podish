#include "bindings.h"
#include "instruction_variants.h"
#include "state.h"
#include <cstdio>
#include <cstring>
#include <span>

using namespace fiberish;

static bool CheckCompactSlots(EmuState* state) {
    constexpr uint8_t plain[] = {0xb8, 0x78, 0x56, 0x34, 0x12};
    constexpr uint8_t memory[] = {0x66, 0x67, 0x8b, 0x86, 0xff, 0xff};
    constexpr uint8_t unsigned_imm[] = {0x83, 0x84, 0x98, 0x12, 0x34, 0x56, 0x78, 0x80};
    constexpr uint8_t signed_imm[] = {0xc7, 0x85, 0x12, 0x34, 0x56, 0x78, 0x80, 0xff, 0xff, 0xff};
    constexpr uint8_t short_disp[] = {0xc7, 0x44, 0x24, 0x80, 0x78, 0x56, 0x34, 0x12};
    constexpr uint8_t wide[] = {0x64, 0x69, 0x84, 0x88, 0x12, 0x34, 0x56, 0x78, 0x88, 0x77, 0x66, 0x55};
    constexpr uint8_t disp16[] = {0x67, 0x69, 0x86, 0xff, 0x80, 0x88, 0x77, 0x66, 0x55};
    constexpr uint8_t absolute16[] = {0x67, 0xc7, 0x06, 0x01, 0xff, 0x88, 0x77, 0x66, 0x55};
    constexpr uint8_t lock[] = {0xf0, 0x83, 0x85, 0x12, 0x34, 0x56, 0x78, 1};
    constexpr uint8_t segment[] = {0x64, 0xc6, 0x05, 0x12, 0x34, 0x56, 0x78, 1};
    constexpr uint8_t word16[] = {0x66, 0x67, 0xc7, 0x86, 0xff, 0x80, 4, 0};
    constexpr uint8_t near16[] = {0x66, 0xe9, 0xf0, 0xff};
    constexpr uint8_t rel8[] = {0x75, 0xe4};
    constexpr uint8_t zero_imm[] = {0x66, 0x81, 0x85, 0x12, 0x34, 0x56, 0x78, 0, 0};
    struct Case {
        std::span<const uint8_t> bytes;
        bool separate_immediate;
    };
    const Case cases[] = {
        {plain, false}, {memory, false},   {unsigned_imm, false}, {signed_imm, true}, {short_disp, true},
        {wide, true},   {disp16, true},    {absolute16, true},    {lock, true},       {near16, false},
        {rel8, false},  {zero_imm, false}, {segment, true},       {word16, false},
    };
    constexpr uint32_t start = 0x12348000;
    uint32_t guest_pc = start;
    std::array<DecodedInstruction, std::size(cases) + 1> ops{};
    for (size_t i = 0; i < std::size(cases); ++i) {
        if (i == 2)
            guest_pc += 0x1234;
        if (i == 7)
            guest_pc -= 0x4567;
        auto bytes = cases[i].bytes;
        DecodedInstTmp decoded{};
        uint16_t opcode = 0;
        if (DecodeInstructionBounded(bytes.data(), bytes.size(), &decoded, &opcode) != InstructionDecodeResult::Success)
            return false;
        // A length can include the NOPs absorbed by DecodeBlock.
        if (i == 0)
            decoded.head.len += 200;
        FinalizeDecodedInstruction(state, guest_pc, opcode, &decoded.head);
        decoded.head.meta.flags.operand_lowered = i % 2;
        decoded.head.meta.flags.no_flags = i % 3 != 0;
        ops[i] = decoded.head;
        guest_pc = decoded.head.next_eip;
    }
    ops.back().next_eip = guest_pc;
    SetExtKind(&ops.back(), ExtKind::Link);
    constexpr uint8_t count = std::tuple_size_v<decltype(ops)>;
    alignas(BasicBlock) std::byte storage[BasicBlock::CalculateSize({count, count, count, count})];
    auto* block = new (storage) BasicBlock;
    block->set_start_eip(start);
    block->WriteOps(BlockSlotLayout::Inspect(start, ops), ops, state->mmu.invalid_code_block());
    for (size_t i = 0; i < std::size(cases); ++i) {
        auto* slot = block->LogicalOp(i);
        const auto got = block->ReadOp(i);
        const auto& expected = ops[i];
        const auto binding = BindScalarHandler(expected);
        const auto handler = binding.handler ? binding.handler : expected.handler;
        if (block->LogicalIndex(slot) != i || NextOp(slot) != block->LogicalOp(i + 1) ||
            slot->HasMemoryImmediate() != cases[i].separate_immediate || got.next_eip != expected.next_eip ||
            block->GetNextEip(slot) != expected.next_eip || got.len != expected.len || got.handler != handler ||
            got.meta.all != expected.meta.all || got.modrm != expected.modrm ||
            got.prefixes.all != expected.prefixes.all || GetImm(&got) != GetImm(&expected) ||
            (GetExtKind(&expected) == ExtKind::Data && GetImm(slot) != GetImm(&expected))) {
            std::fprintf(stderr, "Compact slot round trip failed at case %zu\n", i);
            return false;
        }
        if (HasMem(&expected) && (GetAddressDescriptor(&got) != GetAddressDescriptor(&expected) ||
                                  GetDisplacement(&got) != GetDisplacement(&expected)))
            return false;
        if (cases[i].separate_immediate &&
            ((slot + 1)->handler != ForwardMemoryImmediateSlot || GetImm(slot) != GetImm(&expected)))
            return false;
        if (GetExtKind(&expected) == ExtKind::ControlFlow) {
            auto** link = block->BranchLink(slot);
            if (!link || *link != state->mmu.invalid_code_block() ||
                GetControlTargetEip(&got) != GetControlTargetEip(&expected))
                return false;
            *link = block;
        }
        slot->ClearOperandLowered();
        if (block->ReadOp(i).meta.flags.operand_lowered)
            return false;
    }
    return block->GetNextEip(block->Sentinel()) == guest_pc && block->GetOpLength(count - 1) == 0 &&
           block->FallthroughLink() == state->mmu.invalid_code_block();
}

static bool CheckMaximalPhysicalSlots(EmuState* state, uint32_t address, std::byte* page) {
    constexpr uint8_t code[] = {0x80, 0x83, 0x78, 0x56, 0x34, 0x12, 0xab};
    constexpr uint32_t count = 64;
    for (unsigned i = 0; i < count; ++i)
        std::memcpy(page + i * sizeof(code), code, sizeof(code));
    state->mmu.reset_code_cache();
    const auto end = address + count * sizeof(code);
    const auto* block = DecodeBlock(state, address, end, count);
    if (!block || block->inst_count() != count || block->slot_count != 2 * count + 1 ||
        block->LogicalIndex(block->Sentinel()) != count || block->LogicalOp(count) != block->Sentinel())
        return false;
    for (unsigned i = 0; i < count; ++i) {
        const auto* slot = block->LogicalOp(i);
        if (slot != block->FirstOp() + 2 * i || block->LogicalIndex(slot) != i || GetImm(slot) != 0xab ||
            block->GetNextEip(slot) != address + (i + 1) * sizeof(code) || block->GetOpLength(i) != sizeof(code))
            return false;
    }
    // The leading branch must reach past 63 double-slot instructions and the
    // sentinel. In eight-byte units that needs the ninth bit of its offset.
    std::array<DecodedInstruction, count + 1> ops;
    for (unsigned i = 0; i <= count; ++i)
        ops[i] = block->ReadOp(i);
    constexpr uint8_t jne[] = {0x75, 0x07};
    DecodedInstTmp branch{};
    uint16_t opcode = 0;
    if (DecodeInstructionBounded(jne, sizeof(jne), &branch, &opcode) != InstructionDecodeResult::Success)
        return false;
    branch.head.SetLength(sizeof(code));
    FinalizeDecodedInstruction(state, address, opcode, &branch.head);
    ops[0] = branch.head;
    const auto layout = BlockSlotLayout::Inspect(address, ops);
    auto* storage = state->mmu.try_allocate_block_bytes(BasicBlock::CalculateSize(layout), address);
    if (!storage)
        return false;
    auto* with_branch = new (storage) BasicBlock;
    with_branch->set_start_eip(address);
    with_branch->WriteOps(layout, ops, state->mmu.invalid_code_block());
    const auto* slot = with_branch->FirstOp();
    return with_branch->BranchLink(with_branch->FirstOp()) == &with_branch->BranchLinks()[0].target &&
           with_branch->LogicalOp(1) == slot + 1 && GetControlTargetEip(slot) == GetControlTargetEip(&branch.head);
}

static bool CheckPendingMemoryReplay(EmuState* state) {
    constexpr uint32_t address = 0x2000;
    constexpr uint32_t value = 0x12345678;
    DecodedOp slots[2]{};

    if (state->request_read_and_check_pending<uint32_t>(address, &slots[0]))
        return false;
    std::memcpy(state->mem_op.read.data.data(), &value, sizeof(value));
    state->mem_op.read.done = true;
    if (state->request_read_and_check_pending<uint32_t>(address, &slots[1]))
        return false;
    std::memcpy(state->mem_op.read.data.data(), &value, sizeof(value));
    state->mem_op.read.done = true;
    const auto read = state->request_read_and_check_pending<uint32_t>(address, &slots[1]);
    if (!read || *read != value || state->mem_op_type != PendingMemOp::None)
        return false;

    if (state->request_write_and_check_pending(address, value, &slots[0]))
        return false;
    state->mem_op.write.done = true;
    if (state->request_write_and_check_pending(address, value, &slots[1]))
        return false;
    state->mem_op.write.done = true;
    return state->request_write_and_check_pending(address, value, &slots[1]).has_value() &&
           state->mem_op_type == PendingMemOp::None;
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
    bool ok = check(0, 0, 64, address + 64) && check(0, 1, 1, address + 1) &&
              check(address + 17, 0, 17, address + 17) && CheckCompactSlots(state);
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
         check_immediate(imm32, 4) && CheckMaximalPhysicalSlots(state, address, page) && ok;
    ok = CheckPendingMemoryReplay(state) && ok;
    if (!ok)
        std::fprintf(stderr, "Decode storage, fetch boundary or pending replay check failed\n");
    X86_Destroy(state);
    return ok ? 0 : 1;
}
