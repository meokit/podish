#include "decoder.h"
#include "test_page_backing.h"
#if FIBERCPU_LLVM_JIT
#include "jit/executable_arena.h"
#include "jit/llvm_block_jit.h"
#include "mem/page_region.h"
#include <mach/mach.h>
#include <mach/mach_vm.h>
#endif

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <memory>
#include <thread>
#include <vector>

#include <unistd.h>

namespace {

constexpr uint32_t code_address = 0x10000;
constexpr uint32_t data_address = 0x20000;
constexpr int ss_segment = 2;
constexpr size_t kStatsBufferSize = 2048; // Include the dispatch counters when ENABLE_TLB_STATS is set.
class State {
public:
    explicit State(EmuState* cpu)
        : State(cpu, std::make_shared<TestPageBacking>()) {}

    EmuState* get() const {
        return cpu_.get();
    }
    void reset() {
        cpu_.reset();
    }

    State Clone(bool share_mmu) const {
        return State(X86_Clone(get(), share_mmu), pages_);
    }

    std::byte* MapPage(uint32_t address, uint8_t permissions) {
        return pages_->MapPage(get(), address, permissions);
    }

private:
    State(EmuState* cpu, std::shared_ptr<TestPageBacking> pages)
        : pages_(std::move(pages))
        , cpu_(cpu, X86_Destroy) {}

    std::shared_ptr<TestPageBacking> pages_;
    std::unique_ptr<EmuState, decltype(&X86_Destroy)> cpu_;
};

State LoadCode(const uint8_t* code, unsigned size, const X86_RunContext* run = nullptr) {
    State state{run ? X86_CreateForRun(run) : X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS)};
    state.MapPage(code_address, 7);
    X86_MemWrite(state.get(), code_address, code, size);
    X86_SetEIP(state.get(), code_address);
    return state;
}

BasicBlock* FindCompiledBlock(EmuState* state, uint32_t address = code_address) {
    std::vector<BasicBlock*> blocks(X86_GetBlockCount(state));
    X86_GetBlockList(state, blocks.data(), blocks.size());
    for (auto* block : blocks) {
        if (block->is_valid() && block->start_eip() == address && block->entry != block->FirstOp()->handler)
            return block;
    }
    return nullptr;
}

bool HasCompiledEntry(EmuState* state) {
    return FindCompiledBlock(state) != nullptr;
}

bool VerifyLongBlock(bool enabled, bool linked) {
    // MOV EAX, 2; then twelve ADD EAX, 1 operations.
    std::array<uint8_t, 65> code{};
    code[0] = 0xb8;
    code[1] = 2;
    for (unsigned offset = 5; offset < code.size(); offset += 5) {
        code[offset] = 0x05;
        code[offset + 1] = 1;
    }
    auto state = LoadCode(code.data(), code.size());
    const auto end = code_address + code.size();
    if (!X86_Run(state.get(), end, 64) || X86_RegRead(state.get(), 0) != 14 ||
        HasCompiledEntry(state.get()) != enabled || X86_GetLLVMJitLiveBlockCount() != unsigned(enabled && !linked))
        return false;

    if (auto* block = FindCompiledBlock(state.get())) {
        // A complete block must reach the inlined OpExitBlock even without a
        // callable sentinel handler in the decoded stream.
        const auto handler = block->Sentinel()->handler;
        block->Sentinel()->handler = nullptr;
        X86_SetEIP(state.get(), code_address);
        const bool ok = X86_Run(state.get(), end, 64) && X86_GetEIP(state.get()) == end;
        block->Sentinel()->handler = handler;
        if (!ok)
            return false;
    }

    // A different state can still chain into the shared MMU's code cache.
    auto peer = state.Clone(true);
    state.reset();
    if (X86_GetLLVMJitLiveBlockCount() != unsigned(enabled && !linked))
        return false;

    X86_ResetAllCodeCache(peer.get());
    if (X86_GetLLVMJitLiveBlockCount() != unsigned(enabled && !linked))
        return false;
    const auto compilation_count = linked ? X86_GetLinkedBlockHitCount : X86_GetLLVMJitBlockCount;
    const auto compiled = compilation_count();
    X86_SetEIP(peer.get(), code_address);
    return X86_Run(peer.get(), end, 64) && X86_RegRead(peer.get(), 0) == 14 &&
           compilation_count() == compiled + unsigned(enabled && linked);
}

struct FaultData {
    bool write;
    unsigned count = 0;
    std::array<std::byte, 4096> page{};
};

int MapOnFault(EmuState* state, uint32_t address, int is_write, void* userdata) {
    auto& fault = *static_cast<FaultData*>(userdata);
    if (address != data_address || bool(is_write) != fault.write || fault.count++ != 0)
        return 0;
    if (!X86_MemMapHostPage(state, address, fault.page.data(), 3))
        return 0;
    if (!is_write) {
        constexpr uint8_t value[] = {7, 0, 0, 0};
        X86_MemWrite(state, address, value, sizeof(value));
    }
    return 1;
}

bool VerifyMemoryExit(bool enabled, bool write) {
    // With the data page initially absent, the first MOV must take the slow
    // path. A load restarts that slot. A store resumes at the following ADD.
    const uint8_t code[] = {uint8_t(write ? 0x89 : 0x8b), 0x01, 0x05, 3, 0, 0, 0};
    FaultData fault{write};
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 7);
    X86_RegWrite(state.get(), 1, data_address);
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    const bool ran = X86_Run(state.get(), code_address + sizeof(code), 16);
    uint32_t stored = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
    const auto* compiled = FindCompiledBlock(state.get());
    if (compiled && X86_GetOpIdForHandler(reinterpret_cast<void*>(compiled->FirstOp()->handler)) != code[0])
        return false;
    return ran && fault.count == 1 && stored == 7 && X86_RegRead(state.get(), 0) == 10 &&
           X86_GetEIP(state.get()) == code_address + sizeof(code) && (compiled != nullptr) == enabled;
}

bool VerifyMovAddressOperands(bool enabled) {
    constexpr uint8_t code[] = {
        0x64, 0x67, 0x66, 0x8b, 0x40, 0x14, // MOV AX, FS:[BX+SI+0x14]
        0x64, 0x67, 0x66, 0x89, 0x40, 0x1c, // MOV FS:[BX+SI+0x1c], AX
        0x8b, 0x54, 0x8d, 0x20,             // MOV EDX, [EBP+ECX*4+0x20]
    };
    constexpr uint32_t ss_base = data_address + 4096;
    constexpr int fs_segment = 4;
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    state.MapPage(ss_base, 3);
    X86_SegBaseWrite(state.get(), fs_segment, data_address);
    X86_SegBaseWrite(state.get(), ss_segment, ss_base);
    X86_RegWrite(state.get(), 1, 3);
    X86_RegWrite(state.get(), 3, 0x7600fffd);
    X86_RegWrite(state.get(), 5, 0x40);
    X86_RegWrite(state.get(), 6, 0xe0000006);

    const uint8_t dword[] = {0x78, 0x56, 0x34, 0x12};
    X86_MemWrite(state.get(), ss_base + 0x6c, dword, sizeof(dword));
    const auto end = code_address + sizeof(code);
    for (const uint16_t word : {uint16_t{0xcdef}, uint16_t{0xabcd}}) {
        const uint8_t input[] = {uint8_t(word), uint8_t(word >> 8)};
        const uint8_t dest[] = {0x44, 0x33, 0x22, 0x11};
        X86_MemWrite(state.get(), data_address + 0x17, input, sizeof(input));
        X86_MemWrite(state.get(), data_address + 0x1f, dest, sizeof(dest));
        X86_SetEIP(state.get(), code_address);
        X86_RegWrite(state.get(), 0, 0x12340000);
        X86_RegWrite(state.get(), 2, 0);

        if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end)
            return false;
        uint32_t stored = 0;
        X86_MemRead(state.get(), data_address + 0x1f, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
        if (X86_RegRead(state.get(), 0) != (0x12340000 | word) || X86_RegRead(state.get(), 2) != 0x12345678 ||
            stored != (0x11220000 | word) || HasCompiledEntry(state.get()) != enabled)
            return false;
    }
    return true;
}

bool VerifyStringAndLoopPrefixes(bool enabled) {
    constexpr uint8_t code[] = {
        0xf3, 0x66, 0x67, 0xab, // REP STOSW using CX and DI
        0x67, 0xe3, 2,          // JCXZ skips UD2
        0x0f, 0x0b,
    };
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    X86_SegBaseWrite(state.get(), 0, data_address);
    X86_RegWrite(state.get(), 0, 0xabcd1234);
    X86_RegWrite(state.get(), 1, 0xcafe0003);
    X86_RegWrite(state.get(), 7, 0xccdd0104);
    X86_SetEFLAGS(state.get(), X86_GetEFLAGS(state.get()) | 0x400);

    const auto end = code_address + sizeof(code);
    std::array<uint16_t, 3> words{};
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end)
        return false;
    X86_MemRead(state.get(), data_address + 0x100, reinterpret_cast<uint8_t*>(words.data()), sizeof(words));
    return words == std::array<uint16_t, 3>{0x1234, 0x1234, 0x1234} && X86_RegRead(state.get(), 1) == 0xcafe0000 &&
           X86_RegRead(state.get(), 7) == 0xccdd00fe && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyDirectBranch(bool enabled) {
    // MOV EAX, 2; CMP EAX, 2; JE over UD2.
    constexpr uint8_t code[] = {0xb8, 2, 0, 0, 0, 0x3d, 2, 0, 0, 0, 0x74, 2, 0x0f, 0x0b};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end)
        return false;
    auto* block = FindCompiledBlock(state.get());
    return enabled ? block && block->inst_count() == 4 : block == nullptr;
}

bool VerifyRelocatedWordBranch(bool enabled) {
    // JMP rel16 with a negative displacement. The target wraps to 16 bits
    // even when the same machine code is used at a different guest address.
    constexpr uint8_t code[] = {0x66, 0xe9, 0xfb, 0xff};
    auto state = LoadCode(code, sizeof(code));
    constexpr uint32_t relocated = 0x13200;
    state.MapPage(relocated & ~0xfffu, 7);
    X86_MemWrite(state.get(), relocated, code, sizeof(code));
    fiberish::HandlerFunc entry = nullptr;
    for (auto start : {code_address, relocated}) {
        const auto target = (start - 1) & 0xffffu;
        X86_SetEIP(state.get(), start);
        if (!X86_Run(state.get(), start + sizeof(code), 1) || X86_GetEIP(state.get()) != target) {
            std::fprintf(stderr, "rel16 from %#x expected %#x got %#x fault %u\n", start, target,
                         X86_GetEIP(state.get()), X86_GetFaultVector(state.get()));
            return false;
        }
        const auto* block = FindCompiledBlock(state.get(), start);
        if (bool(block) != enabled || (block && entry && entry != block->entry))
            return false;
        if (block)
            entry = block->entry;
    }
    return true;
}

bool VerifyFlagConsumers(bool enabled) {
    constexpr uint8_t je[] = {0x85, 0xc0, 0x74, 5, 0xba, 5, 0, 0, 0}; // TEST EAX,EAX; JE end; MOV EDX,5
    constexpr uint8_t cmov[] = {0x85, 0xc0, 0x0f, 0x44, 0xd3};        // TEST EAX,EAX; CMOVZ EDX,EBX
    constexpr uint8_t stc[] = {0x85, 0xc0, 0xf9, 0x0f, 0x44, 0xd3};   // TEST EAX,EAX; STC; CMOVZ EDX,EBX
    constexpr uint8_t inc[] = {0x85, 0xc0, 0x41, 0x0f, 0x44, 0xd3};   // TEST EAX,EAX; INC ECX; CMOVZ EDX,EBX
    constexpr uint8_t cmp[] = {0x83, 0xf8, 0, 0x0f, 0x44, 0xd3};      // CMP EAX,0; CMOVZ EDX,EBX
    constexpr uint32_t status_flags = 0x8d5;
    constexpr uint32_t direction_flag = 0x400;
    constexpr uint32_t initial_flags = status_flags | direction_flag | 2;
    struct FlagCase {
        const char* name;
        std::span<const uint8_t> code;
        std::array<uint32_t, 2> edx;
        std::array<uint32_t, 2> flags;
    };
    const FlagCase cases[] = {
        {"TEST; JE", je, {5, 7}, {0x84, 0x44}},          {"TEST; CMOVZ", cmov, {7, 5}, {0x84, 0x44}},
        {"TEST; STC; CMOVZ", stc, {7, 5}, {0x85, 0x45}}, {"TEST; INC; CMOVZ", inc, {5, 7}, {0x54, 0}},
        {"CMP; CMOVZ", cmp, {7, 5}, {0x84, 0x44}},
    };
    for (const auto& [name, code, edx, flags] : cases) {
        for (const bool zero : {false, true}) {
            auto state = LoadCode(code.data(), code.size());
            const auto end = code_address + code.size();
            X86_RegWrite(state.get(), 0, zero ? 0 : 0x80000003);
            X86_RegWrite(state.get(), 1, zero ? 0 : UINT32_MAX);
            X86_RegWrite(state.get(), 2, 7);
            X86_RegWrite(state.get(), 3, 5);
            X86_SetEFLAGS(state.get(), initial_flags);
            // INC overwrites ZF while STC preserves it. Both results still
            // have to publish the full status flags for the next block.
            if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end ||
                X86_RegRead(state.get(), 2) != edx[zero] ||
                (X86_GetEFLAGS(state.get()) & (status_flags | direction_flag)) != (flags[zero] | direction_flag) ||
                HasCompiledEntry(state.get()) != enabled) {
                std::fprintf(stderr, "%s (zero=%d): eip=%x edx=%x flags=%x compiled=%d\n", name, zero,
                             X86_GetEIP(state.get()), X86_RegRead(state.get(), 2), X86_GetEFLAGS(state.get()),
                             HasCompiledEntry(state.get()));
                return false;
            }
        }
    }

    // A faulting TEST leaves the input flags intact. A memory-source CMOV
    // must still fault on the source read when its condition is false.
    constexpr uint8_t faulting[][5] = {{0x85, 0x01, 0x0f, 0x44, 0xd3}, {0x85, 0xc0, 0x0f, 0x44, 0x11}};
    for (unsigned i = 0; i < std::size(faulting); ++i) {
        auto state = LoadCode(faulting[i], sizeof(faulting[i]));
        X86_RegWrite(state.get(), 0, 0x80000003);
        X86_RegWrite(state.get(), 1, data_address);
        X86_RegWrite(state.get(), 2, 7);
        X86_SetEFLAGS(state.get(), initial_flags);
        if (!X86_Run(state.get(), code_address + sizeof(faulting[i]), 16) || X86_GetFaultVector(state.get()) != 14 ||
            X86_GetFaultAddress(state.get()) != data_address || X86_GetEIP(state.get()) != code_address + i * 2 ||
            X86_RegRead(state.get(), 2) != 7 ||
            (X86_GetEFLAGS(state.get()) & (status_flags | direction_flag)) !=
                ((i ? 0x84 : status_flags) | direction_flag) ||
            HasCompiledEntry(state.get()) != enabled)
            return false;
    }
    return true;
}

bool VerifyGenericAcrossJump(bool enabled) {
    // MOVSX EDX, CX; NOP; JMP past UD2; ADD EDX, -1; LEA EAX, [EDX+8]; MOVZX ESI, AL.
    constexpr uint8_t code[] = {0x0f, 0xbf, 0xd1, 0x90, 0xeb, 2,    0x0f, 0x0b, 0x83,
                                0xc2, 0xff, 0x8d, 0x42, 8,    0x0f, 0xb6, 0xf0};
    auto state = LoadCode(code, sizeof(code));
    X86_MemProtect(state.get(), code_address, 4096, 4);
    X86_RegWrite(state.get(), 1, 0x80ff);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end ||
        X86_RegRead(state.get(), 0) != 0xffff8106u || X86_RegRead(state.get(), 2) != 0xffff80feu ||
        X86_RegRead(state.get(), 6) != 6)
        return false;
    auto* block = FindCompiledBlock(state.get());
    const auto* target = FindCompiledBlock(state.get(), code_address + 8);
    if (!enabled)
        return !block && !target;
    if (!block || block->inst_count() != 2 || block->GetOpLength(0) != 4 ||
        X86_GetOpIdForHandler(reinterpret_cast<void*>(block->FirstOp()->handler)) != 0x1bf || !target ||
        target->inst_count() != 3)
        return false;
    const int32_t opcodes[] = {0x83, 0x8d, 0x1b6};
    for (unsigned i = 0; i < std::size(opcodes); ++i) {
        if (X86_GetOpIdForHandler(reinterpret_cast<void*>(target->LogicalOp(i)->handler)) != opcodes[i])
            return false;
    }
    return true;
}

bool VerifyGroup1Registers(bool enabled) {
    constexpr uint8_t code[] = {
        0x80, 0xc4, 1,                      // ADD AH, 1
        0x82, 0xf4, 0x7f,                   // XOR AH, 0x7f (0x82 alias)
        0x81, 0xf8, 0xff, 0x80, 0x34, 0x12, // CMP EAX, 0x123480ff (CF = 0)
        0x83, 0xd0, 0xff,                   // ADC EAX, -1 (CF = 1)
        0x83, 0xd8, 0xff,                   // SBB EAX, -1 (input CF is live, output flags are dead)
        0x89, 0xc2,                         // MOV EDX, EAX
        0x66, 0x83, 0xe8, 0xff,             // SUB AX, -1
        0x81, 0xf8, 0xff, 0x80, 0x34, 0x12, // CMP EAX, 0x123480ff
    };
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x1234feff);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 32) || X86_GetEIP(state.get()) != end || X86_RegRead(state.get(), 0) != 0x123480ff ||
        X86_RegRead(state.get(), 2) != 0x123480fe)
        return false;
    const auto* block = FindCompiledBlock(state.get());
    return enabled ? block && block->inst_count() == 8 : block == nullptr;
}

bool VerifyGroup1Memory(bool enabled) {
    // Restart the ADD on an unmapped read, then execute the following register
    // ADD. Exercise both the compact imm8 and the adjacent-slot fallback.
    constexpr uint32_t displacement = 0x1240;
    for (const auto imm : {uint8_t{0xff}, uint8_t{0xab}}) {
        const uint8_t code[] = {0x83, 0x81, 0x40, 0x12, 0, 0, imm, 0x83, 0xc0, 3};
        FaultData fault{false};
        auto state = LoadCode(code, sizeof(code));
        X86_RegWrite(state.get(), 1, data_address - displacement);
        X86_SetFaultCallback(state.get(), MapOnFault, &fault);
        const auto end = code_address + sizeof(code);
        if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end || fault.count != 1 ||
            X86_RegRead(state.get(), 0) != 3)
            return false;
        uint32_t stored = 0;
        X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
        const auto* compiled = FindCompiledBlock(state.get());
        const unsigned slots = imm == 0xff ? 3 : 4;
        if (stored != uint32_t(7 + int8_t(imm)) ||
            (enabled ? !compiled || compiled->inst_count() != 2 || compiled->slot_count != slots : compiled != nullptr))
            return false;
    }
    return true;
}

bool VerifyGroup5ArithmeticAndPush(bool enabled) {
    constexpr uint8_t code[] = {
        0xff, 0xc0,             // INC EAX
        0xff, 0xc8,             // DEC EAX
        0xff, 0x01,             // INC dword [ECX]
        0x66, 0xff, 0x49, 4,    // DEC word [ECX+4]
        0x66, 0xff, 0x34, 0x24, // PUSH word [ESP]
        0xff, 0xf4,             // PUSH ESP
    };
    constexpr uint32_t ss_base = data_address + 4096;
    constexpr uint32_t initial_esp = 32;
    constexpr uint32_t stack_top = ss_base + initial_esp;
    constexpr uint32_t input[] = {0x7fffffff, 0x12340000};
    constexpr uint32_t stack_operand = 0x1234cdef;
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    state.MapPage(ss_base, 3);
    X86_SegBaseWrite(state.get(), ss_segment, ss_base);
    X86_MemWrite(state.get(), data_address, reinterpret_cast<const uint8_t*>(input), sizeof(input));
    X86_MemWrite(state.get(), stack_top, reinterpret_cast<const uint8_t*>(&stack_operand), sizeof(stack_operand));
    X86_RegWrite(state.get(), 0, 0x7fffffff);
    X86_RegWrite(state.get(), 1, data_address);
    X86_RegWrite(state.get(), 4, initial_esp);
    X86_SetEFLAGS(state.get(), X86_GetEFLAGS(state.get()) | 1);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end) {
        std::fprintf(stderr, "FF arithmetic/push: eip=%x esp=%x ss=%x fault=%d@%x\n", X86_GetEIP(state.get()),
                     X86_RegRead(state.get(), 4), X86_SegBaseRead(state.get(), ss_segment),
                     X86_GetFaultVector(state.get()), X86_GetFaultAddress(state.get()));
        return false;
    }
    uint32_t output[2]{};
    uint32_t pushed_esp = 0;
    uint16_t pushed_word = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(output), sizeof(output));
    X86_MemRead(state.get(), stack_top - 6, reinterpret_cast<uint8_t*>(&pushed_esp), sizeof(pushed_esp));
    X86_MemRead(state.get(), stack_top - 2, reinterpret_cast<uint8_t*>(&pushed_word), sizeof(pushed_word));
    const bool ok = X86_RegRead(state.get(), 0) == 0x7fffffff && X86_RegRead(state.get(), 4) == initial_esp - 6 &&
                    output[0] == 0x80000000 && output[1] == 0x1234ffff && pushed_esp == initial_esp - 2 &&
                    pushed_word == 0xcdef && (X86_GetEFLAGS(state.get()) & 0x8d5) == 0x95 &&
                    HasCompiledEntry(state.get()) == enabled;
    if (!ok)
        std::fprintf(stderr,
                     "FF arithmetic/push: eax=%x esp=%x mem=%x,%x stack=%x,%x flags=%x compiled=%d expected=%d\n",
                     X86_RegRead(state.get(), 0), X86_RegRead(state.get(), 4), output[0], output[1], pushed_esp,
                     pushed_word, X86_GetEFLAGS(state.get()), HasCompiledEntry(state.get()), enabled);
    return ok;
}

bool VerifyGroup5IndirectBranches(bool enabled) {
    for (bool call : {false, true}) {
        for (bool memory : {false, true}) {
            for (bool word : {false, true}) {
                // FF /2 CALL or FF /4 JMP to EAX or [ECX]. A word target
                // and return address must each be truncated to 16 bits.
                const std::array<uint8_t, 3> code{0x66, 0xff, uint8_t((call ? 0x10 : 0x20) | (memory ? 0x01 : 0xc0))};
                const unsigned length = word ? 3 : 2;
                auto state = LoadCode(code.data() + (word ? 0 : 1), length);
                constexpr uint32_t ss_base = data_address + 4096;
                constexpr uint32_t initial_esp = 32;
                constexpr uint32_t stack_top = ss_base + initial_esp;
                state.MapPage(data_address, 3);
                state.MapPage(ss_base, 3);
                X86_SegBaseWrite(state.get(), ss_segment, ss_base);
                constexpr uint32_t untouched = 0x89abcdef;
                const unsigned stack_size = word ? 2 : 4;
                BasicBlock* entry = nullptr;
                for (uint32_t target : {0xabcd2345u, 0xbcde3456u, UINT32_MAX}) {
                    X86_SetEIP(state.get(), code_address);
                    X86_RegWrite(state.get(), 0, target);
                    X86_RegWrite(state.get(), 1, data_address);
                    X86_RegWrite(state.get(), 4, initial_esp);
                    X86_MemWrite(state.get(), data_address, reinterpret_cast<const uint8_t*>(&target), sizeof(target));
                    X86_MemWrite(state.get(), stack_top - 4, reinterpret_cast<const uint8_t*>(&untouched),
                                 sizeof(untouched));
                    const auto end = word ? uint16_t(target) : target;
                    if (!X86_Run(state.get(), 0, 1) || X86_GetEIP(state.get()) != end ||
                        X86_RegRead(state.get(), 4) != initial_esp - (call ? stack_size : 0)) {
                        std::fprintf(stderr, "FF indirect (call=%d memory=%d word=%d target=%x): eip=%x esp=%x\n", call,
                                     memory, word, target, X86_GetEIP(state.get()), X86_RegRead(state.get(), 4));
                        return false;
                    }
                    uint32_t stack_word = 0;
                    X86_MemRead(state.get(), stack_top - 4, reinterpret_cast<uint8_t*>(&stack_word),
                                sizeof(stack_word));
                    const uint32_t expected = !call  ? untouched
                                              : word ? (length << 16) | (untouched & 0xffff)
                                                     : code_address + length;
                    auto* block = FindCompiledBlock(state.get());
                    if (stack_word != expected || (block != nullptr) != enabled || (entry && block != entry)) {
                        std::fprintf(stderr,
                                     "FF indirect (call=%d memory=%d word=%d target=%x): stack=%x expected=%x "
                                     "compiled=%d expected=%d\n",
                                     call, memory, word, target, stack_word, expected, block != nullptr, enabled);
                        return false;
                    }
                    entry = block;
                }
            }
        }
    }
    return true;
}

bool VerifyGroup5Faults(bool enabled) {
    // A faulting Group 5 instruction preserves the previous INC's flags and
    // stops before the following flag producer, even for a register PUSH.
    constexpr uint8_t code[][4] = {
        {0x40, 0xff, 0x01, 0x42}, // INC dword [ECX] faults on its write
        {0x40, 0xff, 0x11, 0x42}, // CALL dword [ECX] faults on the stack write
        {0x40, 0xff, 0xf2, 0x42}, // PUSH EDX faults on the stack write
        {0x40, 0xff, 0x31, 0x42}, // PUSH dword [ECX] faults on the source read
    };
    constexpr uint32_t input = 0x7fffffff;
    constexpr uint32_t status_flags = 0x8d5;
    constexpr uint32_t flags_after_first_inc = 0x55;
    for (size_t i = 0; i < std::size(code); ++i) {
        const bool faults_on_stack_write = i == 1 || i == 2;
        const bool source_unmapped = i == 3;
        // End the actual indirect CALL block at the CALL.
        const unsigned size = i == 1 ? 3 : sizeof(code[i]);
        const uint32_t stack_page = data_address + (source_unmapped ? 4096 : 0);
        const uint32_t untouched = source_unmapped ? stack_page + 12 : data_address;
        auto state = LoadCode(code[i], size);
        state.MapPage(stack_page, 3);
        X86_MemWrite(state.get(), untouched, reinterpret_cast<const uint8_t*>(&input), sizeof(input));
        if (!source_unmapped)
            X86_MemProtect(state.get(), stack_page, 4096, 1);
        X86_RegWrite(state.get(), 0, 0xffffffff);
        X86_RegWrite(state.get(), 1, data_address);
        X86_RegWrite(state.get(), 4, stack_page + 16);
        X86_SetEFLAGS(state.get(), 0x883);
        if (!X86_Run(state.get(), code_address + size, 4) || X86_GetFaultVector(state.get()) != 14 ||
            X86_GetFaultAddress(state.get()) != data_address + (faults_on_stack_write ? 12 : 0) ||
            X86_GetEIP(state.get()) != code_address + 1 || X86_RegRead(state.get(), 0) != 0 ||
            X86_RegRead(state.get(), 2) != 0 || X86_RegRead(state.get(), 4) != stack_page + 16 ||
            (X86_GetEFLAGS(state.get()) & status_flags) != flags_after_first_inc ||
            HasCompiledEntry(state.get()) != enabled) {
            std::fprintf(stderr,
                         "FF fault (case=%zu): eip=%x eax=%x esp=%x flags=%x fault=%d@%x compiled=%d expected=%d\n", i,
                         X86_GetEIP(state.get()), X86_RegRead(state.get(), 0), X86_RegRead(state.get(), 4),
                         X86_GetEFLAGS(state.get()), X86_GetFaultVector(state.get()), X86_GetFaultAddress(state.get()),
                         HasCompiledEntry(state.get()), enabled);
            return false;
        }
        uint32_t value = 0;
        X86_MemRead(state.get(), untouched, reinterpret_cast<uint8_t*>(&value), sizeof(value));
        if (value != input) {
            std::fprintf(stderr, "FF fault (case=%zu): memory changed to %x\n", i, value);
            return false;
        }
    }
    return true;
}

bool VerifyShiftBitRegisters(bool enabled) {
    constexpr uint8_t code[] = {
        0xc0, 0xc0, 8,        // ROL AL, 8 (sets CF from the low bit)
        0xd0, 0xd4,           // RCL AH, 1 (sets CF)
        0xd2, 0xd8,           // RCR AL, CL (consumes CF)
        0x66, 0xd1, 0xf0,     // SAL AX, 1 (/6)
        0xc1, 0xe8, 1,        // SHR EAX, 1
        0x0f, 0xba, 0xe8, 31, // BTS EAX, 31
        0x0f, 0xa3, 0xc8,     // BT EAX, ECX
        0x0f, 0xbb, 0xc8,     // BTC EAX, ECX
        0x0f, 0xbd, 0xd0,     // BSR EDX, EAX
        0x0f, 0xbc, 0xd8,     // BSF EBX, EAX
        0x0f, 0xc8,           // BSWAP EAX
    };
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x12348002);
    X86_RegWrite(state.get(), 1, 1);
    X86_RegWrite(state.get(), 3, 0xff);
    X86_SetEFLAGS(state.get(), X86_GetEFLAGS(state.get()) | 1); // ROL AL, 8 must clear the incoming CF.
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 32) || X86_GetEIP(state.get()) != end || X86_RegRead(state.get(), 0) != 0x83001a89 ||
        X86_RegRead(state.get(), 2) != 31 || X86_RegRead(state.get(), 3) != 0 ||
        (X86_GetEFLAGS(state.get()) & 0x41) != 0)
        return false;
    const auto* block = FindCompiledBlock(state.get());
    return enabled ? block && block->inst_count() == 11 : block == nullptr;
}

bool VerifyTzcntPrefix(bool enabled) {
    // MOV EAX, 0; 16-bit TZCNT DX, AX. The F3 and 66 selectors compose.
    constexpr uint8_t code[] = {0xb8, 0, 0, 0, 0, 0x66, 0xf3, 0x0f, 0xbc, 0xd0};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end && X86_RegRead(state.get(), 2) == 16 &&
           (X86_GetEFLAGS(state.get()) & 0x41) == 1 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyShiftBitMemory(bool enabled) {
    constexpr uint8_t code[] = {
        0xc1, 0x21, 32,         // SHL dword [ECX], 32 reads before checking the count.
        0x0f, 0xab, 0x11,       // BTS [ECX], EDX (index -1 addresses the previous byte).
        0x0f, 0xba, 0x29, 0xff, // BTS dword [ECX], 255 (wraps to bit 31).
    };
    FaultData fault{false};
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address - 4096, 3);
    X86_RegWrite(state.get(), 1, data_address);
    X86_RegWrite(state.get(), 2, 0xffffffff);
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 32) || X86_GetEIP(state.get()) != end || fault.count != 1)
        return false;
    uint32_t word = 0;
    uint8_t previous = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&word), sizeof(word));
    X86_MemRead(state.get(), data_address - 1, &previous, sizeof(previous));
    return word == 0x80000007 && previous == 0x80 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyExtendedInteger(bool enabled) {
    constexpr uint8_t code[] = {
        0x69, 0xc0, 0xfe, 0xff, 0xff, 0xff, // IMUL EAX, EAX, -2
        0x0f, 0xa4, 0xc8, 4,                // SHLD EAX, ECX, 4
        0x0f, 0x9c, 0xc2,                   // SETL DL
    };
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 3);
    X86_RegWrite(state.get(), 1, 0xf0000000);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 0) == 0xffffffaf && X86_RegRead(state.get(), 2) == 1 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyVectorPrefixRouting(bool enabled) {
    constexpr uint8_t code[] = {
        0x66, 0x0f, 0xef, 0xc0, // PXOR XMM0, XMM0
        0x66, 0x0f, 0x6e, 0xc8, // MOVD XMM1, EAX
        0x66, 0x0f, 0xfe, 0xc1, // PADDD XMM0, XMM1
        0x0f, 0xef, 0xc0,       // PXOR MM0, MM0 (must leave XMM0 alone)
        0x66, 0x0f, 0x7e, 0xc2, // MOVD EDX, XMM0
    };
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x12345678);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 2) == 0x12345678 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifySseArithmeticPrefixes(bool enabled) {
    const auto run = [enabled]<class T, size_t N>(uint8_t prefix, const std::array<T, N>& lhs,
                                                  const std::array<T, N>& rhs, bool scalar) {
        const uint8_t code[] = {prefix, 0x0f, 0x58, 0xc1}; // ADDPS, ADDPD, ADDSS or ADDSD
        const unsigned offset = prefix == 0;
        auto state = LoadCode(code + offset, sizeof(code) - offset);
        X86_WriteXMM(state.get(), 0, reinterpret_cast<const uint8_t*>(lhs.data()));
        X86_WriteXMM(state.get(), 1, reinterpret_cast<const uint8_t*>(rhs.data()));
        auto expected = lhs;
        for (unsigned i = 0; i < (scalar ? 1 : N); ++i)
            expected[i] += rhs[i];

        const auto end = code_address + sizeof(code) - offset;
        std::array<T, N> result{};
        if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end)
            return false;
        X86_ReadXMM(state.get(), 0, reinterpret_cast<uint8_t*>(result.data()));
        return result == expected && HasCompiledEntry(state.get()) == enabled;
    };
    constexpr std::array<float, 4> f1{1, 2, 3, 4}, f2{10, 20, 30, 40};
    constexpr std::array<double, 2> d1{1, 2}, d2{10, 20};
    return run(0, f1, f2, false) && run(0x66, d1, d2, false) && run(0xf3, f1, f2, true) && run(0xf2, d1, d2, true);
}

bool VerifyX87Helpers(bool enabled) {
    constexpr uint8_t code[] = {
        0xd9, 0xe8, // FLD1
        0xd9, 0xe8, // FLD1
        0xde, 0xc1, // FADDP ST(1), ST(0)
        0xd9, 0x19, // FSTP dword [ECX]
    };
    FaultData fault{true};
    auto state = LoadCode(code, sizeof(code));
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    X86_RegWrite(state.get(), 1, data_address);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end || fault.count != 1)
        return false;
    uint32_t value = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&value), sizeof(value));
    return value == 0x40000000 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyTwoSlotX87Prefixes(bool enabled) {
    constexpr uint8_t code[] = {
        0x81, 0x42, 0x34, 0x78, 0x56, 0x34, 0x12, // ADD dword [EDX+0x34], 0x12345678 uses two slots
        0x66, 0xd9, 0x72, 0x50,                   // FNSTENV m14byte [EDX+0x50]
    };
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    X86_RegWrite(state.get(), 2, data_address);
    std::array<uint8_t, 28> environment;
    environment.fill(0xa5);
    X86_MemWrite(state.get(), data_address + 0x50, environment.data(), environment.size());

    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end || HasCompiledEntry(state.get()) != enabled)
        return false;
    uint32_t value = 0;
    X86_MemRead(state.get(), data_address + 0x34, reinterpret_cast<uint8_t*>(&value), sizeof(value));
    X86_MemRead(state.get(), data_address + 0x50, environment.data(), environment.size());
    return value == 0x12345678 && environment[0] == 0x7f && environment[1] == 0x03 &&
           std::ranges::all_of(std::span(environment).subspan(14), [](auto byte) { return byte == 0xa5; });
}

bool VerifyStackWidths(bool enabled) {
    constexpr uint8_t code[] = {0x66, 0x50, 0x66, 0x5a}; // PUSH AX; POP DX
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    X86_RegWrite(state.get(), 0, 0x89abcdef);
    X86_RegWrite(state.get(), 2, 0x12340000);
    X86_RegWrite(state.get(), 4, data_address + 32);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 2) == 0x1234cdef && X86_RegRead(state.get(), 4) == data_address + 32 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyStackReplay(bool enabled) {
    constexpr uint8_t code[] = {0x66, 0x9c, 0x66, 0x58}; // PUSHF word; POP AX
    FaultData fault{true};
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x12340000);
    X86_RegWrite(state.get(), 4, data_address + 2);
    X86_SetEFLAGS(state.get(), 0x203);
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end && fault.count == 1 &&
           X86_RegRead(state.get(), 0) == 0x12340203 && X86_RegRead(state.get(), 4) == data_address + 2 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyCallReturn(bool enabled) {
    constexpr uint8_t code[] = {
        0xe8, 2,  0, 0, 0, // CALL callee
        0xeb, 6,           // JMP end
        0xb8, 42, 0, 0, 0, // MOV EAX, 42
        0xc3,              // RET
    };
    auto state = LoadCode(code, sizeof(code));
    state.MapPage(data_address, 3);
    X86_RegWrite(state.get(), 4, data_address + 32);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end && X86_RegRead(state.get(), 0) == 42 &&
           X86_RegRead(state.get(), 4) == data_address + 32 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyTsc(bool enabled) {
    constexpr uint8_t code[] = {0x0f, 0x31, 0x89, 0xc3, 0x0f, 0x31}; // RDTSC; MOV EBX, EAX; RDTSC
    for (int mode = 0; mode < 2; ++mode) {
        auto state = LoadCode(code, sizeof(code));
        X86_SetTscMode(state.get(), mode);
        X86_SetTscOffset(state.get(), 0x200000010ull);
        X86_SetTscFrequency(state.get(), 0); // Real-time mode yields the offset regardless of elapsed time.
        const auto end = code_address + sizeof(code);
        if (!X86_Run(state.get(), end, 16) || X86_RegRead(state.get(), 3) != 16 ||
            X86_RegRead(state.get(), 0) != uint32_t(mode ? 16 : 1016) || X86_RegRead(state.get(), 2) != 2 ||
            HasCompiledEntry(state.get()) != enabled)
            return false;
    }
    return true;
}

bool VerifyHotLoop() {
    // SUB EAX, 1; JNE to SUB. With threshold four, the second run
    // becomes hot on a threaded backedge.
    constexpr uint8_t code[] = {0x2d, 1, 0, 0, 0, 0x75, 0xf9};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    X86_RegWrite(state.get(), 0, 2);
    if (!X86_Run(state.get(), end, 64) || X86_RegRead(state.get(), 0) != 0 || X86_GetLLVMJitBlockCount() != 0)
        return false;
    X86_RegWrite(state.get(), 0, 16);
    X86_SetEIP(state.get(), code_address);
    return X86_Run(state.get(), end, 64) && X86_RegRead(state.get(), 0) == 0 && HasCompiledEntry(state.get()) &&
           X86_GetLLVMJitBlockCount() == 1;
}

bool VerifyUncompiledSuffix() {
    // MOV ESI, [ESP+0x14]; MOV [ESI], EDX; LOCK OR dword [ESI], 0; POP EBX/ESI/EDI/EBP; RET.
    constexpr uint8_t code[] = {0x8b, 0x74, 0x24, 0x14, 0x89, 0x16, 0xf0, 0x83, 0x0e, 0, 0x5b, 0x5e, 0x5f, 0x5d, 0xc3};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    constexpr uint32_t destination = data_address + 0x100;
    const uint32_t stack[] = {1, 2, 3, 4, end, destination};
    state.MapPage(data_address, 3);
    X86_MemWrite(state.get(), data_address, reinterpret_cast<const uint8_t*>(stack), sizeof(stack));
    const auto run = [&] {
        X86_SetEIP(state.get(), code_address);
        X86_RegWrite(state.get(), 2, 7);
        X86_RegWrite(state.get(), 4, data_address);
        if (!X86_Run(state.get(), end, 64) || X86_GetEIP(state.get()) != end ||
            X86_RegRead(state.get(), 4) != data_address + 20)
            return false;
        uint32_t stored = 0;
        X86_MemRead(state.get(), destination, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
        return stored == 7;
    };
    if (!run() || HasCompiledEntry(state.get()))
        return false;
    BasicBlock* first = nullptr;
    if (X86_GetBlockList(state.get(), &first, 1) != 1)
        return false;
    const auto suffix = first->LogicalOp(2)->handler;
    for (int i = 0; i < 3; ++i) {
        if (!run())
            return false;
    }
    auto* hot = FindCompiledBlock(state.get());
    return hot == first && hot->LogicalOp(2)->handler == suffix;
}

bool VerifyBranchBeforeUncompiledSuffix() {
    constexpr uint32_t target = code_address + 0x40;
    constexpr uint32_t end = code_address + 0x80;
    constexpr uint8_t prefix[] = {0x83, 0xfa, 1, 0x74, 59}; // CMP EDX,1; JE target
    constexpr uint8_t locked[] = {0xf0, 0x83, 0x0e, 0};     // LOCK OR dword [ESI],0
    constexpr uint8_t target_code[] = {0x43, 0xeb, 61};     // INC EBX; JMP end
    std::array states{State{nullptr}, State{nullptr}};
    for (size_t i = 0; i < states.size(); ++i) {
        std::array<uint8_t, 4096> bytes{};
        auto* after_lock = std::ranges::copy(locked, std::ranges::copy(prefix, bytes.begin()).out).out;
        if (i)
            *after_lock++ = 0x45; // INC EBP gives the same LLVM prefix a different decoded block budget.
        *after_lock++ = 0xeb;
        *after_lock = static_cast<uint8_t>(-(after_lock - bytes.begin() + 1));
        std::ranges::copy(target_code, bytes.begin() + (target - code_address));
        auto& state = states[i];
        state = LoadCode(bytes.data(), bytes.size());
        auto* cpu = state.get();
        X86_RegWrite(cpu, 2, 1);
        for (int warm = 0; warm < 5; ++warm) {
            X86_SetEIP(cpu, code_address);
            if (!X86_Run(cpu, 0, 2) || X86_GetEIP(cpu) != target) {
                std::fprintf(stderr, "Branch before native suffix %zu, warm %d: EIP=%x\n", i, warm, X86_GetEIP(cpu));
                return false;
            }
        }
        const auto* block = FindCompiledBlock(cpu);
        if (!block || block->start_eip() != code_address || block->inst_count() != 4 + i) {
            std::fprintf(stderr, "Branch before native suffix %zu: compiled=%d, count=%u\n", i, block != nullptr,
                         block ? block->inst_count() : 0);
            return false;
        }
        X86_SetEIP(cpu, code_address);
        if (!X86_Run(cpu, 0, 3) || X86_GetEIP(cpu) != end || X86_RegRead(cpu, 3) != 1) {
            std::fprintf(stderr, "Branch before native suffix %zu: EIP=%x, EBX=%u\n", i, X86_GetEIP(cpu),
                         X86_RegRead(cpu, 3));
            return false;
        }
    }
    return true;
}

bool VerifyCodeReuse() {
    const auto* mode = std::getenv("FIBERCPU_LLVM_JIT");
    const bool exact = !mode || std::strcmp(mode, "generic") != 0;
    uint8_t code[] = {0xb8, 2, 0, 0, 0, 0x05, 1, 0, 0, 0};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    const auto run = [&](uint32_t expected) {
        X86_SetEIP(state.get(), code_address);
        return X86_Run(state.get(), end, 64) && X86_RegRead(state.get(), 0) == expected;
    };
    const auto write = [&](uint8_t opcode, uint8_t immediate = 1) {
        code[5] = opcode;
        code[6] = immediate;
        X86_MemWrite(state.get(), code_address, code, sizeof(code));
        X86_ResetCodeCacheByRange(state.get(), code_address, sizeof(code));
    };

    if (!run(3))
        return false;
    const auto* first = FindCompiledBlock(state.get());
    if (!first)
        return false;
    const auto original = first->entry;
    write(0x05);
    if (!run(3) || FindCompiledBlock(state.get()) != first || X86_GetLLVMJitBlockCount() != 1)
        return false;
    // The same operation with a different immediate reuses the generic
    // entry and gets a different exact entry.
    write(0x05, 4);
    if (!run(6))
        return false;
    const auto* changed = FindCompiledBlock(state.get());
    if (!changed || (changed->entry != original) != exact || X86_GetLLVMJitBlockCount() != 1 + unsigned(exact))
        return false;
    write(0x2d);
    if (!run(1))
        return false;
    if (!HasCompiledEntry(state.get()) || X86_GetLLVMJitLiveBlockCount() != 2 + unsigned(exact))
        return false;
    write(0x05);
    if (!run(3))
        return false;
    const auto* restored = FindCompiledBlock(state.get());
    return restored && restored->entry == original && X86_GetLLVMJitBlockCount() == 2 + unsigned(exact);
}

uint64_t ReadJitCacheBytes(EmuState* state) {
    char stats[kStatsBufferSize];
    X86_DumpStats(state, stats, sizeof(stats));
    constexpr char key[] = "\"llvm_code_cache_bytes\":";
    const char* field = std::strstr(stats, key);
    return field ? std::strtoull(field + sizeof(key) - 1, nullptr, 10) : std::numeric_limits<uint64_t>::max();
}

bool VerifyPackedArena() {
    const uint8_t add[] = {0xb8, 2, 0, 0, 0, 0x05, 1, 0, 0, 0};
    const uint8_t sub[] = {0xb8, 7, 0, 0, 0, 0x2d, 1, 0, 0, 0};
    const auto end = code_address + sizeof(add);
    auto a = LoadCode(add, sizeof(add));
    auto b = LoadCode(sub, sizeof(sub));
    auto peer = LoadCode(add, sizeof(add));
    constexpr size_t arena_bytes = 32 * 1024 * 1024;
    if (!X86_ConfigureCodeCache(a.get(), 0, arena_bytes) || !X86_ConfigureCodeCache(b.get(), 0, arena_bytes) ||
        X86_ConfigureCodeCache(peer.get(), 0, arena_bytes * 2))
        return false;

    char stats[kStatsBufferSize];
    X86_DumpStats(a.get(), stats, sizeof(stats));
    if (!std::strstr(stats, "\"jit_arena_capacity_bytes\":33554432"))
        return false;

    const auto run = [&](EmuState* state, uint32_t expected) {
        X86_SetEIP(state, code_address);
        return X86_Run(state, end, 16) && X86_RegRead(state, 0) == expected;
    };
    if (!run(a.get(), 3))
        return false;
    const auto first_charge = ReadJitCacheBytes(a.get());
    if (!first_charge || ReadJitCacheBytes(b.get()) != first_charge || !run(b.get(), 6) || !run(peer.get(), 3))
        return false;
    const auto* first = FindCompiledBlock(a.get());
    const auto* second = FindCompiledBlock(b.get());
    const auto* shared = FindCompiledBlock(peer.get());
    if (!first || !second || !shared || first->entry != shared->entry || first->entry == second->entry)
        return false;
    const uintptr_t first_address = reinterpret_cast<uintptr_t>(first->entry);
    const uintptr_t second_address = reinterpret_cast<uintptr_t>(second->entry);
    const size_t page_size = getpagesize();
    if (first_address / page_size != second_address / page_size || X86_GetLLVMJitBlockCount() != 2)
        return false;

    const auto original_charge = ReadJitCacheBytes(a.get());
    if (original_charge != first_charge || ReadJitCacheBytes(b.get()) != original_charge ||
        ReadJitCacheBytes(peer.get()) != original_charge)
        return false;

    // Installing the same entry at another guest address adds no JIT allocation.
    constexpr uint32_t alias = code_address + 4096;
    a.MapPage(alias, 7);
    X86_MemWrite(a.get(), alias, add, sizeof(add));
    X86_SetEIP(a.get(), alias);
    if (!X86_Run(a.get(), alias + sizeof(add), 16) || X86_RegRead(a.get(), 0) != 3)
        return false;
    const auto* alias_block = FindCompiledBlock(a.get(), alias);
    if (!alias_block || alias_block->entry != first->entry || ReadJitCacheBytes(a.get()) != original_charge ||
        X86_GetLLVMJitBlockCount() != 2)
        return false;

    X86_ResetAllCodeCache(a.get());
    if (ReadJitCacheBytes(a.get()) != original_charge || !run(peer.get(), 3) || !run(b.get(), 6) ||
        X86_GetLLVMJitLiveBlockCount() != 2)
        return false;
    X86_ResetAllCodeCache(peer.get());
    X86_ResetAllCodeCache(b.get());
    if (X86_GetLLVMJitLiveBlockCount() != 2 || !run(a.get(), 3))
        return false;
    const auto* reused = FindCompiledBlock(a.get());
    return reused && reinterpret_cast<uintptr_t>(reused->entry) == first_address &&
           ReadJitCacheBytes(a.get()) == original_charge && X86_GetLLVMJitBlockCount() == 2;
}

#if FIBERCPU_LLVM_JIT
bool HasPageProtection(mach_vm_address_t address, vm_prot_t protection) {
    mach_vm_address_t base = address;
    mach_vm_size_t size = 0;
    vm_region_basic_info_data_64_t info{};
    mach_msg_type_number_t count = VM_REGION_BASIC_INFO_COUNT_64;
    mach_port_t object = MACH_PORT_NULL;
    const auto result = mach_vm_region(mach_task_self(), &base, &size, VM_REGION_BASIC_INFO_64,
                                       reinterpret_cast<vm_region_info_t>(&info), &count, &object);
    if (object != MACH_PORT_NULL)
        mach_port_deallocate(mach_task_self(), object);
    return result == KERN_SUCCESS && base <= address && address - base < size && info.protection == protection;
}

bool VerifyFlushWithActivePeer(const auto& trigger_flush) {
    constexpr uint8_t interrupt[] = {0xcd, 0x80};
    auto peer = LoadCode(interrupt, sizeof(interrupt));
    struct FlushSignal {
        std::atomic<bool> entered{false};
        bool saw_live_generation = false;
    } signal;
    X86_SetInterruptHook(
        peer.get(), 0x80,
        +[](EmuState*, uint32_t, void* userdata) -> int {
            auto& signal = *static_cast<FlushSignal*>(userdata);
            signal.entered.store(true, std::memory_order_release);
            const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
            while (!fiberish::LLVMJitCacheFlushPending() && std::chrono::steady_clock::now() < deadline)
                std::this_thread::yield();
            signal.saw_live_generation = fiberish::LLVMJitCacheFlushPending() && X86_GetLLVMJitLiveBlockCount() != 0;
            return 1;
        },
        &signal);

    std::thread running_peer([&] { X86_Run(peer.get(), code_address + sizeof(interrupt), 1); });
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!signal.entered.load(std::memory_order_acquire) && std::chrono::steady_clock::now() < deadline)
        std::this_thread::yield();
    const bool flushed = signal.entered.load(std::memory_order_acquire) && trigger_flush();
    running_peer.join();
    return flushed && signal.saw_live_generation;
}

bool VerifyPackedBudget() {
    uint8_t code[] = {
        0xb8, 1,    0,    0,    0, 0x05, 1, 0, 0, 0, // MOV EAX, 1; ADD EAX, 1
        0xd9, 0xe8, 0xd9, 0xe8,                      // FLD1 uses a resident LLVM constant.
        0xde, 0xc1, 0xd9, 0x19,                      // FADDP; FSTP dword [ECX]
    };
    constexpr size_t slab_bytes = 256 * 1024;
    const size_t page_size = getpagesize();
    const size_t arena_bytes = slab_bytes + 3 * page_size;
    const auto arena_field = "\"jit_arena_capacity_bytes\":" + std::to_string(arena_bytes);
    const auto end = code_address + sizeof(code);
    auto state = LoadCode(code, sizeof(code));
    if (!X86_ConfigureCodeCache(state.get(), 1024 * 1024, arena_bytes))
        return false;
    auto arena = fiberish::ExecutableArena::Shared();
    auto data_slot = arena->ReserveData(page_size);
    if (!data_slot)
        return false;
    const auto constant_page = reinterpret_cast<mach_vm_address_t>(data_slot->Data());
    data_slot.reset();
    if (!HasPageProtection(constant_page, VM_PROT_NONE))
        return false;
    state.MapPage(data_address, 3);
    X86_RegWrite(state.get(), 1, data_address);
    auto forked = state.Clone(false);
    char stats[kStatsBufferSize];
    X86_DumpStats(forked.get(), stats, sizeof(stats));
    if (!std::strstr(stats, "\"code_cache_limit_bytes\":1048576") || !std::strstr(stats, arena_field.c_str()))
        return false;
    const auto run = [&](uint32_t expected) {
        X86_SetEIP(state.get(), code_address);
        if (!X86_Run(state.get(), end, 16) || X86_RegRead(state.get(), 0) != expected)
            return false;
        float stored = 0;
        X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
        return stored == 2.0f;
    };
    if (!run(2))
        return false;
    const auto* first = FindCompiledBlock(state.get());
    if (!first || !HasPageProtection(constant_page, VM_PROT_READ) ||
        ReadJitCacheBytes(state.get()) != slab_bytes + page_size || !X86_Run(forked.get(), end, 16) ||
        !HasCompiledEntry(forked.get()))
        return false;
    const auto code_page = reinterpret_cast<mach_vm_address_t>(first->entry) & ~mach_vm_address_t{0xfff};
    const auto distance = code_page < constant_page ? constant_page - code_page : code_page - constant_page;
    if (distance >= (uint64_t{1} << 32))
        return false;
    uint8_t immediate = 2;
    const auto trigger_flush = [&] {
        // The caller replaces the parent's backing before writing, as COW
        // would. The forked MMU keeps its original code page.
        if (!state.MapPage(code_address, 7))
            return false;
        for (; immediate <= 64; ++immediate) {
            code[1] = immediate;
            X86_MemWrite(state.get(), code_address, code, sizeof(code));
            X86_ResetCodeCacheByRange(state.get(), code_address, sizeof(code));
            if (!run(immediate + 1))
                return false;
            if (!HasCompiledEntry(state.get()))
                return X86_GetLLVMJitLiveBlockCount() == 0 && ReadJitCacheBytes(state.get()) == 0 &&
                       ReadJitCacheBytes(forked.get()) == 0 && HasPageProtection(constant_page, VM_PROT_NONE);
        }
        return false;
    };
    if (!VerifyFlushWithActivePeer(trigger_flush) || !run(immediate + 1) || !HasCompiledEntry(state.get()) ||
        X86_GetLLVMJitLiveBlockCount() != 1 || !HasPageProtection(constant_page, VM_PROT_READ))
        return false;
    X86_DumpStats(state.get(), stats, sizeof(stats));
    if (!std::strstr(stats, "\"code_cache_flush_count\":1"))
        return false;
    // This peer was idle throughout the flush. It must drop its old
    // decoded entry before dispatching from that guest address again.
    if (X86_GetBlockCount(forked.get()) != 0 || ReadJitCacheBytes(forked.get()) != ReadJitCacheBytes(state.get()))
        return false;
    X86_SetEIP(forked.get(), code_address);
    if (!X86_Run(forked.get(), end, 16) || X86_RegRead(forked.get(), 0) != 2 || !HasCompiledEntry(forked.get()) ||
        X86_GetLLVMJitLiveBlockCount() != 2)
        return false;
    X86_DumpStats(forked.get(), stats, sizeof(stats));
    return std::strstr(stats, "\"code_cache_flush_count\":1") && std::strstr(stats, arena_field.c_str());
}

bool VerifyCpuRunOptions(const char* path) {
    using Run = std::unique_ptr<X86_RunContext, decltype(&X86_DestroyRun)>;
    char error[512];
    const auto configure = [&](std::initializer_list<std::string> entries) {
        std::string args;
        for (const auto& entry : entries) {
            args += entry;
            args.push_back('\0');
        }
        return Run{X86_CreateRun(args.data(), args.size(), error, sizeof(error)), X86_DestroyRun};
    };

    if (configure({"JitWorkers=5", "LlvmPipeline=unknown-pass"}) || !std::strstr(error, "LlvmPipeline"))
        return false;
    if (configure({"JitArenaSize=3g"}) || !std::strstr(error, "JitArenaSize exceeds 2 GiB"))
        return false;
    if (configure({"LlvmFoldOperandLoads=invalid"}) || !std::strstr(error, "LlvmFoldOperandLoads"))
        return false;
    const auto online =
        configure({"JitMode=exact", "JitHotThreshold=2", "LlvmPipeline=O1", "JitWorkers=0", "LlvmFoldOperandLoads=on"});
    if (!online || configure({"JitWorkers=2"}) || !std::strstr(error, "JitWorkers"))
        return false;
    if (configure({"LlvmFoldOperandLoads=off"}) || !std::strstr(error, "LlvmFoldOperandLoads"))
        return false;
    const std::string output = std::string("WeightedProfilePath=") + path;
    const auto profile =
        configure({"JitMode=profile", output, "JitHotThreshold=3", "JitHotThreshold=2", "BlockCacheSize=1m"});
    const auto second_profile = configure({"JitMode=profile", "JitHotThreshold=1", output + ".second"});
    if (!profile || !second_profile)
        return false;

    uint8_t code[] = {0xb8, 1, 0, 0, 0, 0x05, 1, 0, 0, 0};
    const auto run = [&](EmuState* state, unsigned count, unsigned eax) {
        for (unsigned i = 0; i < count; ++i) {
            X86_SetEIP(state, code_address);
            if (!X86_Run(state, code_address + sizeof(code), 16) || X86_RegRead(state, 0) != eax)
                return false;
        }
        return true;
    };
    auto jit = LoadCode(code, sizeof(code), online.get());
    if (!run(jit.get(), 1, 2) || HasCompiledEntry(jit.get()) || !run(jit.get(), 1, 2) || !HasCompiledEntry(jit.get()))
        return false;

    auto first = LoadCode(code, sizeof(code), profile.get());
    if (!run(first.get(), 3, 2) || HasCompiledEntry(first.get()))
        return false;
    X86_ResetAllCodeCache(first.get());
    if (!run(first.get(), 2, 2))
        return false;

    // A fork inherits the collector and admission threshold, while a separate
    // CPU run can record the same recipe without adding to this run's weight.
    auto forked = first.Clone(false);
    if (!run(forked.get(), 2, 2))
        return false;
    forked.reset();
    auto other_run = LoadCode(code, sizeof(code), second_profile.get());
    if (!run(other_run.get(), 9, 2) || !X86_FinishRun(second_profile.get(), error, sizeof(error)))
        return false;
    code[1] = 2;
    auto live = LoadCode(code, sizeof(code), profile.get());
    if (!run(live.get(), 2, 3) || !X86_FinishRun(profile.get(), error, sizeof(error)))
        return false;

    const auto check_weights = [](const std::string& file, std::initializer_list<uint64_t> expected) {
        std::ifstream stream(file, std::ios::binary);
        const std::string bytes((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
        if (bytes.size() < 40 || !bytes.starts_with("FIBJPR09"))
            return false;
        size_t offset = 40; // Eight-byte magic, then the 32-byte fingerprint.
        const auto read_le = [&](unsigned count) {
            uint64_t value = 0;
            for (unsigned i = 0; i < count; ++i)
                value |= uint64_t(static_cast<unsigned char>(bytes[offset++])) << (i * 8);
            return value;
        };
        for (uint64_t weight : expected) {
            if (bytes.size() - offset < 12 || read_le(8) != weight)
                return false;
            const size_t length = read_le(4);
            if (!length || length > bytes.size() - offset)
                return false;
            offset += length;
        }
        return offset == bytes.size();
    };
    if (!check_weights(path, {7, 2}) || !check_weights(std::string(path) + ".second", {9}))
        return false;

    const auto page_profile =
        configure({"JitMode=profile", "JitHotThreshold=2", "WeightedProfileSamePage=on", output + ".page"});
    if (!page_profile)
        return false;
    auto page_state = LoadCode(code, sizeof(code), page_profile.get());
    constexpr uint32_t neighbor = code_address + 32;
    constexpr uint32_t late_neighbor = code_address + 64;
    constexpr uint32_t other_page = code_address + 4096;
    page_state.MapPage(other_page, 7);
    const auto visit = [&](uint32_t address, uint8_t value) {
        code[1] = value;
        X86_MemWrite(page_state.get(), address, code, sizeof(code));
        X86_SetEIP(page_state.get(), address);
        return X86_Run(page_state.get(), address + sizeof(code), 16) &&
               X86_RegRead(page_state.get(), 0) == unsigned(value) + 1;
    };
    return visit(neighbor, 3) && visit(other_page, 4) && run(page_state.get(), 2, 3) && visit(late_neighbor, 5) &&
           X86_FinishRun(page_profile.get(), error, sizeof(error)) &&
           check_weights(std::string(path) + ".page", {2, 1});
}
#endif

#if FIBERCPU_LLVM_JIT
bool VerifyConditionalFallthrough(bool region) {
    constexpr uint32_t first = code_address + 0x40;
    constexpr uint32_t second = code_address + 0x80;
    constexpr uint8_t entry[] = {
        0x83, 0xfa, 1, 0x74, 59,           // CMP EDX,1; JE first
        0x83, 0xfb, 1, 0x74, 118,          // CMP EBX,1; JE second
        0x83, 0xf8, 0, 0xeb, uint8_t(-15), // CMP EAX,0; JMP entry
    };
    constexpr uint8_t first_code[] = {0x0f, 0x44, 0xf7, 0xeb, uint8_t(-0x45)}; // CMOVZ ESI,EDI; JMP entry
    constexpr uint8_t second_code[] = {0x0f, 0x44, 0xf7, 0xe9, 0x78, 0xff, 0xff, 0xff};
    alignas(4096) std::array<uint8_t, 4096> code{};
    std::ranges::copy(entry, code.begin());
    std::ranges::copy(first_code, code.begin() + (first - code_address));
    std::ranges::copy(second_code, code.begin() + (second - code_address));
    State state{X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS)};
    auto* cpu = state.get();
    if (!X86_MemMapHostPage(cpu, code_address, code.data(), 5))
        return false;
    X86_RegWrite(cpu, 7, 11);
    for (const auto target : {first, second}) {
        X86_SetEIP(cpu, target);
        if (!X86_Run(cpu, 0, 2) || X86_GetEIP(cpu) != code_address)
            return false;
    }
    const auto check = [&](unsigned edx, unsigned ebx, unsigned budget, uint32_t next, unsigned esi, uint32_t flags) {
        X86_SetEIP(cpu, code_address);
        X86_RegWrite(cpu, 0, 2);
        X86_RegWrite(cpu, 2, edx);
        X86_RegWrite(cpu, 3, ebx);
        X86_RegWrite(cpu, 6, 7);
        X86_SetEFLAGS(cpu, 2);
        return X86_Run(cpu, 0, budget) && X86_GetEIP(cpu) == next && X86_RegRead(cpu, 6) == esi &&
               (X86_GetEFLAGS(cpu) & 0x8d5) == flags;
    };
    const auto verify_paths = [&] {
        return check(1, 0, 2, first, 7, 0x44) && check(0, 1, 4, second, 7, 0x44) &&
               check(1, 0, 4, code_address, 11, 0x44) && check(0, 1, 6, code_address, 11, 0x44) &&
               check(0, 0, 6, code_address, 7, 0);
    };
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    do {
        if (!verify_paths())
            return false;
        if (const auto* block = FindCompiledBlock(cpu); block && (!region || block->RegionContext()))
            return block->inst_count() == 6 && verify_paths();
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    } while (std::chrono::steady_clock::now() < deadline);
    return false;
}

bool VerifyPageRegion(bool cross_page = false) {
    constexpr uint32_t first = code_address;
    constexpr uint32_t external = first + 4096;
    constexpr uint32_t local_second = first + 32;
    const uint32_t second = cross_page ? external - 15 : local_second;
    constexpr uint32_t relocated = 0x30000;
    constexpr uint8_t local_a[] = {
        0x8b, 0x0a, 0x8d, 0x40, 1, 0x85, 0xdb, 0xeb, 23,
    }; // MOV ECX,[EDX]; LEA EAX,[EAX+1]; TEST EBX,EBX; JMP B.
    constexpr uint8_t local_b[] = {
        0x0f, 0x44, 0xf7, 0x8d, 0x40, 2, 0x83, 0xfb, 1, 0xeb, uint8_t(-43),
    }; // CMOVZ ESI,EDI; LEA EAX,[EAX+2]; CMP EBX,1; JMP A.
    constexpr uint8_t cross_a[] = {
        0x8b, 0x0a, 0x8d, 0x40, 1, 0x85, 0xdb, 0xe9, 0xe5, 0x0f, 0, 0,
    }; // MOV ECX,[EDX]; LEA EAX,[EAX+1]; TEST EBX,EBX; JMP B.
    constexpr uint8_t cross_b[] = {
        0x0f, 0x44, 0xf7, 0x8d, 0x40, 2, 0x83, 0xfb, 1, 0x0f, 0x85, 0, 0xf0, 0xff, 0xff,
    }; // CMOVZ ESI,EDI; LEA EAX,[EAX+2]; CMP EBX,1; JNE A; otherwise fall into external.
    constexpr uint8_t external_code[] = {0x0f, 0x44, 0xdf, 0xe9, 0xf8, 0xef, 0xff, 0xff};
    // CMOVZ EBX,EDI; JMP A.
    constexpr uint32_t flag_mask = 0x8d5 | 0x400;
    constexpr uint32_t initial_flags = 0x480;
    constexpr uint32_t test_flags = 0x444;
    constexpr uint32_t cmp_flags = 0x495;
    const auto a_code = cross_page ? std::span<const uint8_t>(cross_a) : std::span<const uint8_t>(local_a);
    const auto b_code = cross_page ? std::span<const uint8_t>(cross_b) : std::span<const uint8_t>(local_b);
    alignas(4096) std::array<uint8_t, 4096> bytes{};
    std::copy(std::begin(a_code), std::end(a_code), bytes.begin());
    std::copy(std::begin(b_code), std::end(b_code), bytes.begin() + (second - first));
    State state{X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS)};
    if (!X86_MemMapHostPage(state.get(), first, bytes.data(), 5) ||
        !X86_MemMapHostPage(state.get(), relocated, bytes.data(), 5))
        return false;
    if (cross_page) {
        state.MapPage(external, 7);
        X86_MemWrite(state.get(), external, external_code, sizeof(external_code));
    }
    auto* data_page = state.MapPage(data_address, 3);
    X86_RegWrite(state.get(), 2, data_address);
    X86_RegWrite(state.get(), 7, 11);

    const auto run = [&](uint32_t eip, uint64_t budget, uint32_t eax, uint32_t next, uint32_t esi, uint32_t flags,
                         uint32_t ebx = 0) {
        X86_SetEIP(state.get(), eip);
        X86_RegWrite(state.get(), 0, 0);
        X86_RegWrite(state.get(), 3, ebx);
        X86_RegWrite(state.get(), 6, 7);
        X86_SetEFLAGS(state.get(), initial_flags | 2);
        return X86_Run(state.get(), 0, budget) && X86_GetEIP(state.get()) == next &&
               X86_RegRead(state.get(), 0) == eax && X86_RegRead(state.get(), 6) == esi &&
               (X86_GetEFLAGS(state.get()) & flag_mask) == flags;
    };
    const auto region_context = [](const BasicBlock* block) { return block ? block->RegionContext() : nullptr; };
    const auto warm = [&](uint32_t start) -> BasicBlock* {
        for (int attempt = 0; attempt < 150; ++attempt) {
            if (!run(start, 10000, 3750, start, 11, cmp_flags))
                return nullptr;
            auto* a = FindCompiledBlock(state.get(), start);
            auto* b = FindCompiledBlock(state.get(), start + (second - first));
            if (const auto* table = region_context(a); table && table == region_context(b) && a->entry != b->entry) {
                using Region = fiberish::mem::PageRegion<BasicBlock>;
                return reinterpret_cast<uintptr_t>(table) & Region::MemberMask ? nullptr : a;
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
        return nullptr;
    };
    const auto* a = warm(first);
    if (cross_page) {
        if (!a)
            return false;
        // A cold link and a cached link both pass CMP's zero flag to CMOVZ.
        for (unsigned i = 0; i < 2; ++i) {
            if (!run(second, 6, 2, first, 7, test_flags, 1) || X86_RegRead(state.get(), 3) != 11)
                return false;
        }
        return true;
    }
    const auto* alias = warm(relocated);
    if (!a || !alias || a->entry != alias->entry || region_context(a) == region_context(alias) ||
        !run(first, 4, 1, second, 7, test_flags) || !run(second, 4, 2, first, 7, cmp_flags) ||
        !run(second, 12, 5, first, 11, cmp_flags))
        return false;

    X86_MemUnmap(state.get(), data_address, 4096);
    struct Observer {
        uint8_t* bytes;
        std::byte* data_page;
        const BasicBlock* first;
        const BasicBlock* alias;
        bool called = false;
        bool restored = false;
        uint32_t flags = 0;
    } observer{bytes.data(), data_page, a, alias};
    X86_SetFaultCallback(
        state.get(),
        +[](EmuState* s, uint32_t address, int write, void* userdata) -> int {
            auto& observer = *static_cast<Observer*>(userdata);
            if (address != data_address || write || observer.called || !X86_IsExecuting(s))
                return 0;
            observer.called = true;
            observer.flags = X86_GetEFLAGS(s);
            observer.bytes[(local_second - first) + 5] = 9;
            X86_ResetCodeCacheByRange(s, first, 4096);
            observer.restored = observer.first->entry == observer.first->FirstOp()->handler &&
                                observer.alias->entry == observer.alias->FirstOp()->handler;
            return X86_MemMapHostPage(s, data_address, observer.data_page, 3);
        },
        &observer);
    return run(first, 8, 10, first, 11, cmp_flags) && observer.called && observer.restored &&
           (observer.flags & flag_mask) == initial_flags && !region_context(alias);
}

bool VerifyRegionFlagsAtExits() {
    constexpr uint32_t next = code_address + 0x10;
    // ADD EAX,[ESI]; DEC ECX; JMP next
    constexpr uint8_t first[] = {0x03, 0x06, 0x49, 0xeb, 0x0b};
    // XOR EAX,EDX; LEA ESI,[ESI+4]; JMP code_address
    constexpr uint8_t second[] = {0x31, 0xd0, 0x8d, 0x76, 4, 0xeb, 0xe9};
    alignas(4096) std::array<uint8_t, 4096> code{};
    std::ranges::copy(first, code.begin());
    std::ranges::copy(second, code.begin() + (next - code_address));
    State state{X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS)};
    auto* cpu = state.get();
    auto* data = state.MapPage(data_address, 3);
    if (!X86_MemMapHostPage(cpu, code_address, code.data(), 5) || !data)
        return false;
    data[4092] = std::byte{1};

    constexpr uint32_t sticky = 0x602; // DF, IF and the reserved bit.
    constexpr uint32_t mask = sticky | 0x8d5;
    const auto run = [&](uint32_t source, int64_t budget) {
        X86_SetEIP(cpu, code_address);
        X86_RegWrite(cpu, 0, UINT32_MAX);
        X86_RegWrite(cpu, 1, 0x80000000);
        X86_RegWrite(cpu, 2, 0x80000003);
        X86_RegWrite(cpu, 6, source);
        X86_SetEFLAGS(cpu, sticky | 0x41);
        return X86_Run(cpu, 0, budget);
    };
    const auto flags = [&] { return X86_GetEFLAGS(cpu) & mask; };
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        if (!run(data_address, 12))
            return false;
        const auto* a = FindCompiledBlock(cpu);
        const auto* b = FindCompiledBlock(cpu, next);
        if (a && b && a->RegionContext() && a->RegionContext() == b->RegionContext()) {
            constexpr uint32_t last_word = data_address + 4092;
            // DEC preserves ADD's CF, sets OF and AF, and has even parity.
            if (!run(last_word, 3) || X86_GetEIP(cpu) != next || flags() != (sticky | 0x815))
                return false;
            // XOR replaces the arithmetic flags on the backedge budget exit.
            if (!run(last_word, 6) || X86_GetEIP(cpu) != code_address || flags() != (sticky | 0x84))
                return false;
            // A fault on the following iteration observes the same XOR flags.
            return run(last_word, 9) && X86_GetFaultVector(cpu) == 14 &&
                   X86_GetFaultAddress(cpu) == data_address + 4096 && X86_GetEIP(cpu) == code_address &&
                   flags() == (sticky | 0x84);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    } while (std::chrono::steady_clock::now() < deadline);
    return false;
}

bool VerifyMultiplePageRegions() {
    using Region = fiberish::mem::PageRegion<BasicBlock>;
    constexpr unsigned blocks = 18;
    constexpr unsigned stride = 4;
    constexpr uint32_t alias = 0x30000;
    alignas(4096) std::array<uint8_t, 4096> code{};
    for (unsigned i = 0; i < blocks; ++i) {
        const auto at = stride * i;
        code[at] = 0x40; // INC EAX
        code[at + 1] = 0xeb;
        code[at + 2] = i + 1 == blocks ? uint8_t(1 - stride * blocks) : 1;
    }
    using namespace std::string_view_literals;
    constexpr auto options = "JitMode=region\0JitHotThreshold=8\0"sv;
    char error[256]{};
    const std::unique_ptr<X86_RunContext, decltype(&X86_DestroyRun)> run{
        X86_CreateRun(options.data(), options.size(), error, sizeof(error)), X86_DestroyRun};
    if (!run)
        return false;
    State state{X86_CreateForRun(run.get())};
    auto* cpu = state.get();
    if (!cpu || !X86_MemMapHostPage(cpu, code_address, code.data(), 5) ||
        !X86_MemMapHostPage(cpu, alias, code.data(), 5))
        return false;
    const auto step = [&](uint32_t address, unsigned budget = 2) {
        X86_SetEIP(cpu, address);
        return X86_Run(cpu, 0, budget);
    };
    const auto wait_for_region = [&](uint32_t address) -> BasicBlock* {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
        do {
            if (!step(address))
                return nullptr;
            if (auto* block = FindCompiledBlock(cpu, address); block && block->RegionContext())
                return block;
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        } while (std::chrono::steady_clock::now() < deadline);
        return nullptr;
    };

    std::vector<const BasicBlock*> members;
    for (const auto page : {code_address, alias}) {
        for (unsigned i = 0; i < blocks; ++i)
            if (!step(page + stride * i))
                return false;
        const auto* first = wait_for_region(page);
        if (!first)
            return false;
        const auto* table = static_cast<const Region*>(first->RegionContext());
        const auto entry = first->entry;
        if (table->count != Region::MaxMembers)
            return false;
        unsigned offset = 0;
        while (offset < stride * blocks && FindCompiledBlock(cpu, page + offset))
            offset += stride;
        if (offset == stride * blocks)
            return false;
        const auto* second = wait_for_region(page + offset);
        if (!second || first->RegionContext() != table || first->entry != entry)
            return false;
        const auto* other_table = static_cast<const Region*>(second->RegionContext());
        if (other_table == table || other_table->count != blocks - Region::MaxMembers)
            return false;
        for (unsigned i = 0; i < blocks; ++i) {
            const auto* block = FindCompiledBlock(cpu, page + stride * i);
            if (!block || (block->RegionContext() != table && block->RegionContext() != other_table))
                return false;
            members.push_back(block);
        }
    }
    X86_RegWrite(cpu, 0, 0);
    if (!step(code_address, 2 * blocks) || X86_RegRead(cpu, 0) != blocks || X86_GetEIP(cpu) != code_address)
        return false;
    X86_ResetCodeCacheByRange(cpu, alias, 1);
    return std::ranges::all_of(members, [](const BasicBlock* block) {
        return !block->is_valid() && !block->RegionContext() && block->entry == block->FirstOp()->handler;
    });
}

#endif

bool VerifyAsyncCompilations() {
    // Both blocks share a recipe initially. Replace one before the VM polls
    // for the first recipe and hard-reset the other's decoded code cache.
    constexpr uint8_t code[] = {0xb8, 5, 0, 0, 0, 0x90, 0x05, 3, 0, 0, 0};
    const auto end = code_address + sizeof(code);
    auto changed = LoadCode(code, sizeof(code));
    auto reset = LoadCode(code, sizeof(code));
    auto run = [&](State& state, uint32_t expected) {
        X86_SetEIP(state.get(), code_address);
        return X86_Run(state.get(), end, 32) && X86_GetEIP(state.get()) == end &&
               X86_RegRead(state.get(), 0) == expected;
    };
    if (!run(changed, 8) || !run(reset, 8))
        return false;
    constexpr uint8_t sub = 0x2d;
    X86_MemWrite(changed.get(), code_address + 6, &sub, 1);
    X86_ResetCodeCacheByRange(changed.get(), code_address, sizeof(code));
    X86_ResetAllCodeCache(reset.get());
    auto peer = LoadCode(code, sizeof(code));
    {
        auto destroyed = LoadCode(code, sizeof(code));
        if (!run(destroyed, 8))
            return false;
    }

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        if (!run(changed, 2) || !run(reset, 8) || !run(peer, 8))
            return false;
        const auto* sub_block = FindCompiledBlock(changed.get());
        const auto* add_block = FindCompiledBlock(reset.get());
        const auto* peer_block = FindCompiledBlock(peer.get());
        if (sub_block && add_block && peer_block) {
            return sub_block->entry != add_block->entry && add_block->entry == peer_block->entry &&
                   X86_GetLLVMJitLiveBlockCount() == 2;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    } while (std::chrono::steady_clock::now() < deadline);
    return false;
}

bool VerifyLinkedCache(bool compile_on_miss) {
    const auto hits = X86_GetLinkedBlockHitCount();
    if (hits == 0 || X86_GetLLVMJitBlockCount() != 0)
        return false;
    constexpr uint8_t code[] = {0xb8, 0x4a, 0x17, 0, 0}; // MOV EAX, 0x174a, absent from the profile.
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    auto run = [&] {
        X86_SetEIP(state.get(), code_address);
        return X86_Run(state.get(), end, 16) && X86_RegRead(state.get(), 0) == 0x174a &&
               X86_GetLinkedBlockHitCount() == hits;
    };
    if (!compile_on_miss)
        return run() && !HasCompiledEntry(state.get()) && X86_GetLLVMJitBlockCount() == 0;

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    do {
        if (!run())
            return false;
        if (HasCompiledEntry(state.get()))
            return X86_GetLLVMJitBlockCount() == 1;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    } while (std::chrono::steady_clock::now() < deadline);
    return false;
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "packed") == 0) {
        if (VerifyPackedArena())
            return 0;
        std::fprintf(stderr, "LLVM packed allocation, shared ownership or slab reuse mismatch\n");
        return 1;
    }
#if FIBERCPU_LLVM_JIT
    if (argc == 2 && std::strcmp(argv[1], "region") == 0) {
        if (VerifyPageRegion() && VerifyPageRegion(true) && VerifyConditionalFallthrough(true) &&
            VerifyRegionFlagsAtExits() && VerifyMultiplePageRegions())
            return 0;
        std::fprintf(stderr, "LLVM page region publication, relocation or memory restart mismatch\n");
        return 1;
    }
    if (argc == 3 && std::strcmp(argv[1], "options") == 0) {
        if (VerifyCpuRunOptions(argv[2]))
            return 0;
        std::fprintf(stderr, "CPU run option configuration, weighted counts or isolation mismatch\n");
        return 1;
    }
    if (argc == 2 && std::strcmp(argv[1], "budget") == 0) {
        if (VerifyPackedBudget() && VerifyConditionalFallthrough(false))
            return 0;
        std::fprintf(stderr, "LLVM cache pressure or conditional branch budget mismatch\n");
        return 1;
    }
#endif
    if (argc == 2 && std::strcmp(argv[1], "async") == 0) {
        if (VerifyAsyncCompilations())
            return 0;
        std::fprintf(stderr, "Async LLVM installation, invalidation or code reuse mismatch\n");
        return 1;
    }
    if (argc == 2 && std::strcmp(argv[1], "hot") == 0) {
        if (VerifyHotLoop() && VerifyUncompiledSuffix() && VerifyBranchBeforeUncompiledSuffix())
            return 0;
        std::fprintf(stderr, "LLVM hot backedge admission or native suffix mismatch\n");
        return 1;
    }
    if (argc == 2 && std::strcmp(argv[1], "reuse") == 0) {
        if (VerifyCodeReuse())
            return 0;
        std::fprintf(stderr, "LLVM executable cache reuse mismatch\n");
        return 1;
    }
    const bool enabled = argc == 2;
    const bool linked_hybrid = argc == 2 && std::strcmp(argv[1], "linked-hybrid") == 0;
    const bool linked = linked_hybrid || (argc == 2 && std::strcmp(argv[1], "linked") == 0);
    if (!VerifyLongBlock(enabled, linked) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM block execution or cache ownership mismatch\n");
        return 1;
    }
    if (!VerifyMemoryExit(enabled, false) || !VerifyMemoryExit(enabled, true) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM memory exit or scalar restart mismatch\n");
        return 1;
    }
    if (!VerifyMovAddressOperands(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM MOV address width, segment or operand width mismatch\n");
        return 1;
    }
    if (!VerifyDirectBranch(enabled) || !VerifyRelocatedWordBranch(enabled) || !VerifyStringAndLoopPrefixes(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM direct branch, REP string or counter width mismatch\n");
        return 1;
    }
    if (!VerifyFlagConsumers(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM flag consumers, exit flags or memory replay mismatch\n");
        return 1;
    }
    if (!VerifyGenericAcrossJump(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM re-decode across a direct jump mismatch\n");
        return 1;
    }
    if (!VerifyGroup1Registers(enabled) || !VerifyGroup1Memory(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Group1 flags, operands or memory restart mismatch\n");
        return 1;
    }
    const char* jit_mode = std::getenv("FIBERCPU_LLVM_JIT");
    const bool group5_enabled = enabled && (!jit_mode || std::strcmp(jit_mode, "generic") != 0);
    if (!VerifyGroup5ArithmeticAndPush(group5_enabled) || !VerifyGroup5IndirectBranches(group5_enabled) ||
        !VerifyGroup5Faults(group5_enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Group5 operands, indirect exits or precise faults mismatch\n");
        return 1;
    }
    if (!VerifyShiftBitRegisters(enabled) || !VerifyTzcntPrefix(enabled) || !VerifyShiftBitMemory(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Shift/bit flags, operands or memory restart mismatch\n");
        return 1;
    }
    if (!VerifyExtendedInteger(enabled) || !VerifyVectorPrefixRouting(enabled) ||
        !VerifySseArithmeticPrefixes(enabled) || !VerifyX87Helpers(enabled) || !VerifyTwoSlotX87Prefixes(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Generated integer, vector prefix or x87 template mismatch\n");
        return 1;
    }
    if (!VerifyStackWidths(enabled) || !VerifyStackReplay(enabled) || !VerifyCallReturn(enabled) ||
        !VerifyTsc(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM generic stack width, call or return mismatch\n");
        return 1;
    }
    if (linked && !VerifyLinkedCache(linked_hybrid)) {
        std::fprintf(stderr, "Linked block install or native miss fallback mismatch\n");
        return 1;
    }
}
