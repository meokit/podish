#include "exec_utils.h"
#include "instruction_variants.h"
#include "state.h"
#include "test_page_backing.h"
#include <cstdio>
#include <cstring>
#include <memory>
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
    alignas(BasicBlock) std::byte storage[BasicBlock::CalculateSize({count, count, count})];
    auto* block = new (storage) BasicBlock;
    block->set_start_eip(start);
    block->WriteOps(BlockSlotLayout::Inspect(start, ops), ops, state->mmu.invalid_code_block());
    for (size_t i = 0; i < std::size(cases); ++i) {
        auto* slot = block->LogicalOp(i);
        const auto got = block->ReadOp(i);
        const auto& expected = ops[i];
        const auto binding = BindScalarHandler(expected);
        const auto handler = binding.handler ? binding.handler : expected.handler;
        if (block->LogicalIndex(slot) != i || block->NextOp(slot) != block->LogicalOp(i + 1) ||
            slot->HasMemoryImmediate() != cases[i].separate_immediate || got.next_eip != expected.next_eip ||
            block->GetNextEip(slot) != expected.next_eip ||
            block->GetStartEip(slot) != expected.next_eip - expected.len || got.len != expected.len ||
            got.handler != handler || got.meta.all != expected.meta.all || got.modrm != expected.modrm ||
            got.prefixes.all != expected.prefixes.all || GetImm(&got) != GetImm(&expected) ||
            (GetExtKind(&expected) == ExtKind::Data && GetImm(slot) != GetImm(&expected))) {
            std::fprintf(stderr, "Compact slot round trip failed at case %zu\n", i);
            return false;
        }
        if (HasMem(&expected) && (GetAddressDescriptor(&got) != GetAddressDescriptor(&expected) ||
                                  GetDisplacement(&got) != GetDisplacement(&expected)))
            return false;
        if (cases[i].separate_immediate && ((slot + 1)->handler != nullptr || GetImm(slot) != GetImm(&expected)))
            return false;
        if (GetExtKind(&expected) == ExtKind::ControlFlow) {
            auto* link = slot + 1;
            if (block->NextOp(slot) != slot + 2 || link->LoadBlockLink() != state->mmu.invalid_code_block() ||
                GetControlTargetEip(&got) != GetControlTargetEip(&expected) ||
                DecodedOperandShape::Read(&got).immediate != 0 ||
                DecodedOperandShape::Read(&got, /*include_relative_offset=*/true).immediate != GetImm(&got))
                return false;
            link->StoreBlockLink(block);
        }
        slot->ClearOperandLowered();
        if (block->ReadOp(i).meta.flags.operand_lowered)
            return false;
    }
    return block->GetNextEip(block->Sentinel()) == guest_pc && block->GetStartEip(block->Sentinel()) == guest_pc &&
           block->GetOpLength(count - 1) == 0 && block->FallthroughLink() == state->mmu.invalid_code_block();
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
            block->GetNextEip(slot) != address + (i + 1) * sizeof(code) ||
            block->GetStartEip(slot) != address + i * sizeof(code) || block->GetOpLength(i) != sizeof(code))
            return false;
    }
    // The branch's second slot stays adjacent even in a full 129-slot block.
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
    return (slot + 1)->LoadBlockLink() == state->mmu.invalid_code_block() && with_branch->NextOp(slot) == slot + 2 &&
           with_branch->LogicalOp(1) == slot + 2 && GetControlTargetEip(slot) == GetControlTargetEip(&branch.head);
}

static bool CheckReturnSlots(bool narrow, bool immediate) {
    constexpr uint32_t code_address = 0x1000;
    constexpr uint32_t target = 0x1200;
    constexpr uint32_t stack_address = 0x2000;
    constexpr uint16_t adjustment = 0x1234;
    TestPageBacking pages;
    std::unique_ptr<EmuState, decltype(&X86_Destroy)> state(X86_Create(), X86_Destroy);
    const auto cpu = state.get();
    cpu->status = EmuStatus::Running;
    auto* page = pages.MapPage(cpu, code_address, static_cast<uint8_t>(mem::Property::RX));
    if (!page)
        return false;
    pages.MapPage(cpu, stack_address, 3);
    const std::array<uint8_t, 4> code{0x66, uint8_t(immediate ? 0xc2 : 0xc3), 0x34, 0x12};
    const uint8_t* bytes = code.data() + !narrow;
    const auto size = uint32_t(1 + narrow + (immediate ? 2 : 0));
    std::memcpy(page, bytes, size);
    const uint32_t stack_value = narrow ? 0xabcd0000 | target : target;
    X86_MemWrite(cpu, stack_address, reinterpret_cast<const uint8_t*>(&stack_value), sizeof(stack_value));

    auto* block = DecodeBlock(cpu, code_address, 0, 0);
    if (!block || block->inst_count() != 1 || block->slot_count != (immediate ? 3 : 2)) {
        std::fprintf(stderr, "RET decode failed (block=%p, count=%u, slots=%u)\n", (void*)block,
                     block ? block->inst_count() : 0, block ? block->slot_count : 0);
        return false;
    }
    auto* op = block->FirstOp();
    auto* link = op + immediate;
    if (link->LoadBlockLink() != cpu->mmu.invalid_code_block())
        return false;
    link->StoreBlockLink(block);
    const auto restored = block->ReadOp(0);
    if (restored.modrm != (immediate ? 2 : 3) || restored.prefixes.flags.opsize != narrow ||
        GetExtKind(&restored) != ExtKind::ControlFlow || GetImm(&restored) != (immediate ? adjustment : 0) ||
        DecodedOperandShape::Read(&restored).immediate != (immediate ? adjustment : 0) ||
        block->NextOp(op) != block->Sentinel()) {
        std::fprintf(stderr, "RET restore failed (opcode bits=%u, prefixes=%u, metadata=%u, imm=%x)\n", restored.modrm,
                     restored.prefixes.all, restored.meta.all, GetImm(&restored));
        return false;
    }

    X86_SetEIP(cpu, code_address);
    X86_RegWrite(cpu, ESP, stack_address);
    X86_Run(cpu, target, 8);
    const auto esp = stack_address + (narrow ? 2 : 4) + (immediate ? adjustment : 0);
    if (X86_GetEIP(cpu) == target && X86_RegRead(cpu, ESP) == esp)
        return true;
    std::fprintf(stderr, "RET execution failed (eip=%x, esp=%x, expected esp=%x)\n", X86_GetEIP(cpu),
                 X86_RegRead(cpu, ESP), esp);
    return false;
}

static bool CheckTestBranches() {
    constexpr uint32_t mask = CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK;
    TestPageBacking pages;
    std::unique_ptr<EmuState, decltype(&X86_Destroy)> state(X86_Create(), X86_Destroy);
    auto* cpu = state.get();
    for (bool branch_on_zero : {true, false}) {
        const uint32_t address = branch_on_zero ? 0x1000 : 0x2000;
        const uint8_t code[] = {
            0x85, 0xc0, // test eax, eax
            0x0f, uint8_t(branch_on_zero ? 0x84 : 0x85),
            5,    0,
            0,    0, // je/jne end
            0xba, 1,
            0,    0,
            0, // mov edx, 1
        };
        pages.MapPage(cpu, address, 7);
        X86_MemWrite(cpu, address, code, sizeof(code));
        for (const uint32_t eax : {0u, 1u, 0x80000000u}) {
            cpu->status = EmuStatus::Running;
            SetArchitecturalEflags(cpu, mask);
            X86_SetEIP(cpu, address);
            X86_RegWrite(cpu, EAX, eax);
            X86_RegWrite(cpu, EDX, 0);
            X86_Run(cpu, address + sizeof(code), 8);
            const uint32_t flags = eax == 0 ? ZF_MASK | PF_MASK : (eax == 1 ? 0 : SF_MASK | PF_MASK);
            const bool taken = (eax == 0) == branch_on_zero;
            if (X86_GetEIP(cpu) != address + sizeof(code) || X86_RegRead(cpu, EDX) != unsigned(!taken) ||
                (GetArchitecturalEflags(cpu) & mask) != flags) {
                std::fprintf(stderr, "TEST/Jcc mismatch (je=%d, eax=%x, eip=%x, edx=%x, flags=%x, expected=%x)\n",
                             branch_on_zero, eax, X86_GetEIP(cpu), X86_RegRead(cpu, EDX),
                             GetArchitecturalEflags(cpu) & mask, flags);
                return false;
            }
        }
    }
    return true;
}

static bool CheckWordCompareFlags() {
    struct Case {
        uint16_t dest;
        uint16_t src;
        uint32_t expected;
    };
    constexpr Case cases[] = {
        {0, 0, ZF_MASK | PF_MASK},
        {0, 1, SF_MASK | CF_MASK | AF_MASK | PF_MASK},
        {0x8000, 1, OF_MASK | AF_MASK | PF_MASK},
        {0x7fff, 0xffff, SF_MASK | CF_MASK | OF_MASK | PF_MASK},
        {1, 0, 0},
    };
    constexpr uint32_t mask = CF_MASK | PF_MASK | AF_MASK | ZF_MASK | SF_MASK | OF_MASK;
    for (auto [dest, src, expected] : cases) {
        uint64_t flags = InitFlagsCache(mask);
        AluCmp<uint16_t>(nullptr, flags, dest, src);
        const uint32_t actual = (GetFlags32(flags) & (mask & ~PF_MASK)) | (EvaluatePF(flags) ? PF_MASK : 0);
        if (actual != expected) {
            std::fprintf(stderr, "Word CMP flags mismatch (dest=%x, src=%x, flags=%x, expected=%x)\n", dest, src,
                         actual, expected);
            return false;
        }
    }
    return true;
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

static bool CheckDeadFlagsAcrossReplay() {
    constexpr uint32_t address = 0x1000;
    TestPageBacking pages;
    auto* state = X86_Create();
    state->status = EmuStatus::Running;
    auto* page = pages.MapPage(state, address, static_cast<uint8_t>(mem::Property::RX));
    if (!page) {
        X86_Destroy(state);
        return false;
    }

    constexpr uint8_t dead[] = {0xc1, 0xce, 1, 0x31, 0xdb};              // ROR ESI,1; XOR EBX,EBX.
    constexpr uint8_t fault[] = {0xc1, 0xce, 1, 0x8b, 0x03, 0x31, 0xdb}; // MOV EAX,[EBX].
    constexpr uint8_t moffs[] = {0xc1, 0xce, 1, 0xa1, 0x00, 0x40, 0x00, 0x00, 0x31, 0xdb};
    constexpr uint8_t stack[] = {0xc1, 0xce, 1, 0x50, 0x31, 0xdb};
    constexpr uint8_t invalid[] = {0xc1, 0xce, 1, 0xfe, 0xf0, 0x31, 0xdb};
    constexpr uint8_t zero[] = {0xc1, 0xce, 1, 0xc1, 0xce, 32, 0x0f, 0x92, 0xc2};
    const auto decode = [&](std::span<const uint8_t> code) {
        state->status = EmuStatus::Running;
        state->mmu.reset_code_cache();
        std::memcpy(page, code.data(), code.size());
        X86_SetEIP(state, address);
        X86_SetEFLAGS(state, 2);
        X86_RegWrite(state, ESI, 1);
        X86_RegWrite(state, EDX, 0);
        X86_RegWrite(state, EBX, 0x4000);
        X86_RegWrite(state, ESP, 0x4004);
        return DecodeBlock(state, address, address + code.size(), code.size());
    };
    const auto* block = decode(dead);
    bool ok = block && block->ReadOp(0).meta.flags.no_flags;
    block = decode(zero);
    ok = ok && block && !block->ReadOp(0).meta.flags.no_flags;
    X86_Run(state, address + sizeof(zero), 16);
    ok = ok && (X86_RegRead(state, EDX) & 0xff) == 1 && X86_RegRead(state, ESI) == 0x80000000;

    struct FaultCase {
        std::span<const uint8_t> bytes;
        int vector;
    };
    const FaultCase faults[] = {{fault, 14}, {moffs, 14}, {stack, 14}, {invalid, 6}};
    for (auto [code, vector] : faults) {
        block = decode(code);
        ok = ok && block && !block->ReadOp(0).meta.flags.no_flags;
        X86_Run(state, address + code.size(), 16);
        ok =
            ok && X86_GetFaultVector(state) == vector && X86_GetEIP(state) == address + 3 && (X86_GetEFLAGS(state) & 1);
    }
    X86_Destroy(state);
    return ok;
}

static bool CheckBoundScalarWidths() {
    constexpr uint32_t code_address = 0x1000;
    constexpr uint32_t data_address = 0x2000;
    constexpr uint32_t initial = 0xa5a5a5a5;
    constexpr uint8_t lea32[] = {0x8d, 0x43, 0x01};
    constexpr uint8_t lea16[] = {0x66, 0x8d, 0x43, 0x01};
    constexpr uint8_t mov_reg32[] = {0xc7, 0xc0, 0x78, 0x56, 0x34, 0x12};
    constexpr uint8_t mov_reg16[] = {0x66, 0xc7, 0xc0, 0x78, 0x56};
    constexpr uint8_t mov_mem32[] = {0xc7, 0x01, 0x78, 0x56, 0x34, 0x12};
    constexpr uint8_t mov_mem16[] = {0x66, 0xc7, 0x01, 0x78, 0x56};
    constexpr uint8_t mov_small32[] = {0xc7, 0x01, 0x04, 0x00, 0x00, 0x00};
    constexpr uint8_t mov_small16[] = {0x66, 0xc7, 0x01, 0x04, 0x00};
    struct Case {
        std::span<const uint8_t> bytes;
        uint32_t eax;
        uint32_t memory;
    };
    const Case cases[] = {
        {lea32, 0x12345678, initial},     {lea16, 0xa5a55678, initial},       {mov_reg32, 0x12345678, initial},
        {mov_reg16, 0xa5a55678, initial}, {mov_mem32, initial, 0x12345678},   {mov_mem16, initial, 0xa5a55678},
        {mov_small32, initial, 4},        {mov_small16, initial, 0xa5a50004},
    };
    TestPageBacking pages;
    std::unique_ptr<EmuState, decltype(&X86_Destroy)> state(X86_Create(), X86_Destroy);
    pages.MapPage(state.get(), code_address, 7);
    pages.MapPage(state.get(), data_address, 3);
    HandlerFunc handler32 = nullptr;
    for (unsigned i = 0; i < std::size(cases); ++i) {
        const auto& test = cases[i];
        const auto address = code_address + 16 * i;
        const auto end = address + uint32_t(test.bytes.size());
        X86_MemWrite(state.get(), address, test.bytes.data(), uint32_t(test.bytes.size()));
        X86_MemWrite(state.get(), data_address, reinterpret_cast<const uint8_t*>(&initial), sizeof(initial));
        X86_RegWrite(state.get(), EAX, initial);
        X86_RegWrite(state.get(), EBX, 0x12345677);
        X86_RegWrite(state.get(), ECX, data_address);
        X86_SetEIP(state.get(), address);
        if (!X86_Run(state.get(), end, 8) || X86_GetEIP(state.get()) != end) {
            std::fprintf(stderr, "Width case %u stopped at %#x, expected %#x\n", i, X86_GetEIP(state.get()), end);
            return false;
        }
        const auto* block = state->mmu.lookup_cached_block(address);
        if (!block || block->inst_count() != 1) {
            std::fprintf(stderr, "Width case %u failed to decode\n", i);
            return false;
        }
        const auto handler = block->FirstOp()->handler;
        if (CanonicalBoundHandler(handler) != g_Handlers[test.bytes[i % 2]]) {
            std::fprintf(stderr, "Width case %u changed the canonical handler\n", i);
            return false;
        }
        if (i % 2 == 0)
            handler32 = handler;
        else if (handler == handler32) {
            std::fprintf(stderr, "Width case %u used the same handler for both widths\n", i);
            return false;
        }
        uint32_t value = 0;
        X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&value), sizeof(value));
        if (X86_RegRead(state.get(), EAX) != test.eax || value != test.memory) {
            std::fprintf(stderr, "Width case %u produced EAX=%#x memory=%#x, expected %#x %#x\n", i,
                         X86_RegRead(state.get(), EAX), value, test.eax, test.memory);
            return false;
        }
    }
    return true;
}

static bool CheckMemoryFaultEip(bool write, bool single_step, bool concat = false) {
    constexpr uint32_t code_address = 0x1000;
    constexpr uint32_t data_address = 0x2000;
    // INC EDX, optionally followed by a JMP over UD2. The concatenated block
    // removes the JMP and has a PC discontinuity before the replaying access.
    constexpr uint8_t prefix[] = {0x42, 0xeb, 0x02, 0x0f, 0x0b};
    constexpr uint8_t load[] = {0x8b, 0x01, 0x42};
    constexpr uint8_t store[] = {0xc7, 0x01, 0x78, 0x56, 0x34, 0x12, 0x42};
    const auto code = write ? std::span<const uint8_t>(store) : std::span<const uint8_t>(load);
    const uint32_t prefix_size = concat ? sizeof(prefix) : 1;
    const auto fault_eip = code_address + prefix_size;
    TestPageBacking pages;
    std::unique_ptr<EmuState, decltype(&X86_Destroy)> state(X86_Create(), X86_Destroy);
    pages.MapPage(state.get(), code_address, 7);
    X86_MemWrite(state.get(), code_address, prefix, prefix_size);
    X86_MemWrite(state.get(), fault_eip, code.data(), static_cast<uint32_t>(code.size()));
    X86_MemProtect(state.get(), code_address, 4096, 5);
    X86_RegWrite(state.get(), ECX, data_address);
    struct Fault {
        uint32_t eip;
        bool write;
        unsigned count = 0;
        bool matched = false;
    } fault{fault_eip, write};
    const auto callback = [](EmuState* cpu, uint32_t address, int is_write, void* data) {
        auto& result = *static_cast<Fault*>(data);
        ++result.count;
        result.matched = address == data_address && bool(is_write) == result.write && X86_GetEIP(cpu) == result.eip &&
                         X86_RegRead(cpu, EDX) == 1;
        return 0;
    };
    X86_SetFaultCallback(state.get(), callback, &fault);
    X86_SetEIP(state.get(), single_step ? fault_eip : code_address);
    if (single_step) {
        X86_RegWrite(state.get(), EDX, 1);
        X86_Step(state.get());
    } else {
        X86_Run(state.get(), fault_eip + static_cast<uint32_t>(code.size()), 16);
    }
    return (!concat || state->block_stats.block_concat_success_direct_jmp == 1) && fault.count == 1 && fault.matched &&
           state->status == EmuStatus::Fault && X86_GetEIP(state.get()) == fault_eip &&
           X86_RegRead(state.get(), EDX) == 1 && state->mem_op_type == PendingMemOp::None;
}

static bool CheckPageLocalBlockCache() {
    constexpr uintptr_t host_page = 0x11000;
    constexpr uintptr_t alias_backing = 0x22000;
    constexpr uintptr_t second_host_page = 0x33000;
    constexpr uint32_t guest_page = 0x3ff000;
    // The first three offsets collide at the final bucket of a four-slot table.
    constexpr mem::BlockCacheKey keys[] = {
        {host_page, guest_page + 3},
        {host_page, guest_page + 7},
        {host_page, guest_page + 11},
        {alias_backing, guest_page + 11},
        {host_page, guest_page + mem::PAGE_SIZE + 11},
    };
    constexpr size_t spanning_block = 2;
    std::array<BasicBlock, std::size(keys)> blocks{};
    BasicBlock replacement{};
    mem::CodeCacheState cache;
    const auto publish = [&](size_t index, BasicBlock& block) {
        mem::HostPageSet host_pages;
        host_pages.push_unique(keys[index].host_page_base);
        if (index == spanning_block)
            host_pages.push_unique(second_host_page);
        block.set_start_eip(keys[index].guest_eip);
        block.set_valid(true);
        return cache.CacheDecodedBlock(keys[index], host_pages, &block) == &block;
    };
    for (size_t i = 0; i < blocks.size(); ++i) {
        if (!publish(i, blocks[i]))
            return false;
    }

    cache.EraseBlock(keys[0]);
    cache.EraseBlock(keys[1]);
    const auto& readonly_cache = std::as_const(cache);
    for (size_t i = 0; i < blocks.size(); ++i) {
        const auto* expected = i < spanning_block ? nullptr : &blocks[i];
        if (readonly_cache.LookupBlock(keys[i]) != expected)
            return false;
    }

    cache.InvalidateHostPage(second_host_page);
    if (blocks[2].is_valid() || !blocks[3].is_valid() || !blocks[4].is_valid() ||
        readonly_cache.LookupBlock(keys[spanning_block]) != &blocks[spanning_block] ||
        !publish(spanning_block, replacement) || readonly_cache.LookupBlock(keys[spanning_block]) != &replacement)
        return false;

    cache.ResetReachability();
    for (const auto& key : keys) {
        if (readonly_cache.LookupBlock(key))
            return false;
    }
    return true;
}

int main() {
    constexpr uint32_t address = 0x1000;
    TestPageBacking pages;
    auto* state = X86_Create();
    state->status = EmuStatus::Running;
    auto* page = pages.MapPage(state, address, static_cast<uint8_t>(mem::Property::RX));
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
    if (!CheckBoundScalarWidths()) {
        std::fprintf(stderr, "LEA or immediate MOV width binding failed\n");
        ok = false;
    }
    if (!CheckPageLocalBlockCache()) {
        std::fprintf(stderr, "Page-local block cache check failed\n");
        ok = false;
    }
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

    // An instruction split across pages preserves the canonical opcode.
    auto* next = pages.MapPage(state, address + 4096, static_cast<uint8_t>(mem::Property::RX));
    if (!next) {
        X86_Destroy(state);
        return 1;
    }
    page[4095] = std::byte{0x66};
    next[0] = std::byte{0x8b};
    next[1] = std::byte{0xc0};
    result = FetchAndDecodeInstruction(state, address + 4095, buffer, &decoded, &handler, &raw);
    ok = ok && result == InstructionDecodeResult::Success && raw == 0x8b && decoded.head.GetLength() == 3;
    // MOV keeps the canonical handler index until the slot is bound.
    page[0] = std::byte{0x8b};
    page[1] = std::byte{0xc0};
    result = FetchAndDecodeInstruction(state, address, buffer, &decoded, &handler, &raw);
    ok = ok && result == InstructionDecodeResult::Success && raw == 0x8b && handler == raw;
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
    for (bool narrow : {false, true}) {
        for (bool immediate : {false, true}) {
            if (!CheckReturnSlots(narrow, immediate)) {
                std::fprintf(stderr, "RET slot check failed (operand16=%d, immediate=%d)\n", narrow, immediate);
                ok = false;
            }
        }
    }
    ok = CheckPendingMemoryReplay(state) && CheckDeadFlagsAcrossReplay() && CheckWordCompareFlags() &&
         CheckTestBranches() && ok;
    ok = CheckMemoryFaultEip(false, false) && CheckMemoryFaultEip(true, false) && CheckMemoryFaultEip(false, true) &&
         CheckMemoryFaultEip(true, true) && CheckMemoryFaultEip(false, false, true) &&
         CheckMemoryFaultEip(true, false, true) && ok;
    if (!ok)
        std::fprintf(stderr, "Decode storage, fetch boundary or pending replay check failed\n");
    X86_Destroy(state);
    return ok ? 0 : 1;
}
