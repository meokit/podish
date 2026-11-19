#include "bindings.h"
#include "decoder.h"

#include <array>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>

namespace {

constexpr uint32_t code_address = 0x10000;
constexpr uint32_t data_address = 0x20000;
using State = std::unique_ptr<EmuState, decltype(&X86_Destroy)>;

State LoadCode(const uint8_t* code, unsigned size) {
    State state{X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS), X86_Destroy};
    X86_MemMap(state.get(), code_address, 4096, 7);
    X86_MemWrite(state.get(), code_address, code, size);
    X86_SetEIP(state.get(), code_address);
    return state;
}

BasicBlock* FindCompiledBlock(EmuState* state) {
    std::vector<BasicBlock*> blocks(X86_GetBlockCount(state));
    X86_GetBlockList(state, blocks.data(), blocks.size());
    for (auto* block : blocks) {
        if (block->is_valid() && block->start_eip() == code_address && block->entry != block->FirstOp()->handler)
            return block;
    }
    return nullptr;
}

bool HasCompiledEntry(EmuState* state) {
    return FindCompiledBlock(state) != nullptr;
}

bool VerifyLongBlock(bool enabled) {
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
        HasCompiledEntry(state.get()) != enabled || X86_GetLLVMJitLiveBlockCount() != unsigned(enabled))
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
    State peer{X86_Clone(state.get(), 1), X86_Destroy};
    state.reset();
    if (X86_GetLLVMJitLiveBlockCount() != unsigned(enabled))
        return false;

    X86_ResetAllCodeCache(peer.get());
    if (X86_GetLLVMJitLiveBlockCount() != 0)
        return false;
    const auto compiled = X86_GetLLVMJitBlockCount();
    X86_SetEIP(peer.get(), code_address);
    return X86_Run(peer.get(), end, 64) && X86_RegRead(peer.get(), 0) == 14 &&
           X86_GetLLVMJitBlockCount() == compiled + unsigned(enabled);
}

struct FaultData {
    bool write;
    unsigned count = 0;
};

int MapOnFault(EmuState* state, uint32_t address, int is_write, void* userdata) {
    auto& fault = *static_cast<FaultData*>(userdata);
    if (address != data_address || bool(is_write) != fault.write || fault.count++ != 0)
        return 0;
    X86_MemMap(state, address, 4096, 3);
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

bool VerifyGenericConcat(bool enabled) {
    // MOVSX EDX, CX; NOP; JMP past UD2; ADD EDX, -1; LEA EAX, [EDX+8]; MOVZX ESI, AL.
    constexpr uint8_t code[] = {0x0f, 0xbf, 0xd1, 0x90, 0xeb, 2,    0x0f, 0x0b, 0x83,
                                0xc2, 0xff, 0x8d, 0x42, 8,    0x0f, 0xb6, 0xf0};
    auto state = LoadCode(code, sizeof(code));
    X86_ReprotectMappedRange(state.get(), code_address, 4096, 4);
    X86_RegWrite(state.get(), 1, 0x80ff);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end ||
        X86_RegRead(state.get(), 0) != 0xffff8106u || X86_RegRead(state.get(), 2) != 0xffff80feu ||
        X86_RegRead(state.get(), 6) != 6)
        return false;
    auto* block = FindCompiledBlock(state.get());
    if (!enabled)
        return block == nullptr;
    if (!block || block->inst_count() != 4 || block->FirstOp()->len != 4)
        return false;
    const int32_t opcodes[] = {0x1bf, 0x83, 0x8d, 0x1b6};
    for (unsigned i = 0; i < std::size(opcodes); ++i) {
        if (X86_GetOpIdForHandler(reinterpret_cast<void*>(block->FirstOp()[i].handler)) != opcodes[i])
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
    // ADD dword [ECX], -1; CMP EAX, 0. The unmapped read restarts the ADD.
    constexpr uint8_t code[] = {0x83, 0x01, 0xff, 0x81, 0xf8, 0, 0, 0, 0};
    FaultData fault{false};
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 1, data_address);
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end || fault.count != 1)
        return false;
    uint32_t stored = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&stored), sizeof(stored));
    return stored == 6 && HasCompiledEntry(state.get()) == enabled;
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
    X86_MemMap(state.get(), data_address - 4096, 4096, 3);
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
        0x69, 0xc0, 0xfe, 0xff, 0xff, 0xff,  // IMUL EAX, EAX, -2
        0x0f, 0xa4, 0xc8, 4,               // SHLD EAX, ECX, 4
        0x0f, 0x9c, 0xc2,                  // SETL DL
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
        0x66, 0x0f, 0xef, 0xc0,  // PXOR XMM0, XMM0
        0x66, 0x0f, 0x6e, 0xc8,  // MOVD XMM1, EAX
        0x66, 0x0f, 0xfe, 0xc1,  // PADDD XMM0, XMM1
        0x0f, 0xef, 0xc0,        // PXOR MM0, MM0 (must leave XMM0 alone)
        0x66, 0x0f, 0x7e, 0xc2,  // MOVD EDX, XMM0
    };
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x12345678);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 2) == 0x12345678 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyX87Helpers(bool enabled) {
    constexpr uint8_t code[] = {
        0xd9, 0xe8,  // FLD1
        0xd9, 0xe8,  // FLD1
        0xde, 0xc1,  // FADDP ST(1), ST(0)
        0xd9, 0x19,  // FSTP dword [ECX]
    };
    auto state = LoadCode(code, sizeof(code));
    FaultData fault{true};
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    X86_RegWrite(state.get(), 1, data_address);
    const auto end = code_address + sizeof(code);
    if (!X86_Run(state.get(), end, 16) || X86_GetEIP(state.get()) != end || fault.count != 1)
        return false;
    uint32_t value = 0;
    X86_MemRead(state.get(), data_address, reinterpret_cast<uint8_t*>(&value), sizeof(value));
    return value == 0x40000000 && HasCompiledEntry(state.get()) == enabled;
}

bool VerifyStackWidths(bool enabled) {
    constexpr uint8_t code[] = {0x66, 0x50, 0x66, 0x5a};  // PUSH AX; POP DX
    auto state = LoadCode(code, sizeof(code));
    X86_MemMap(state.get(), data_address, 4096, 3);
    X86_RegWrite(state.get(), 0, 0x89abcdef);
    X86_RegWrite(state.get(), 2, 0x12340000);
    X86_RegWrite(state.get(), 4, data_address + 32);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 2) == 0x1234cdef && X86_RegRead(state.get(), 4) == data_address + 32 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyStackReplay(bool enabled) {
    constexpr uint8_t code[] = {0x66, 0x9c, 0x66, 0x58};  // PUSHF word; POP AX
    auto state = LoadCode(code, sizeof(code));
    X86_RegWrite(state.get(), 0, 0x12340000);
    X86_RegWrite(state.get(), 4, data_address + 2);
    X86_SetEFLAGS(state.get(), 0x203);
    FaultData fault{true};
    X86_SetFaultCallback(state.get(), MapOnFault, &fault);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end && fault.count == 1 &&
           X86_RegRead(state.get(), 0) == 0x12340203 && X86_RegRead(state.get(), 4) == data_address + 2 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyCallReturn(bool enabled) {
    constexpr uint8_t code[] = {
        0xe8, 2, 0, 0, 0,   // CALL callee
        0xeb, 6,            // JMP end
        0xb8, 42, 0, 0, 0,  // MOV EAX, 42
        0xc3,               // RET
    };
    auto state = LoadCode(code, sizeof(code));
    X86_MemMap(state.get(), data_address, 4096, 3);
    X86_RegWrite(state.get(), 4, data_address + 32);
    const auto end = code_address + sizeof(code);
    return X86_Run(state.get(), end, 16) && X86_GetEIP(state.get()) == end &&
           X86_RegRead(state.get(), 0) == 42 && X86_RegRead(state.get(), 4) == data_address + 32 &&
           HasCompiledEntry(state.get()) == enabled;
}

bool VerifyTsc(bool enabled) {
    constexpr uint8_t code[] = {0x0f, 0x31, 0x89, 0xc3, 0x0f, 0x31};  // RDTSC; MOV EBX, EAX; RDTSC
    for (int mode = 0; mode < 2; ++mode) {
        auto state = LoadCode(code, sizeof(code));
        X86_SetTscMode(state.get(), mode);
        X86_SetTscOffset(state.get(), 0x200000010ull);
        X86_SetTscFrequency(state.get(), 0);  // Real-time mode yields the offset regardless of elapsed time.
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
    constexpr uint8_t code[] = {0x8b, 0x74, 0x24, 0x14, 0x89, 0x16, 0xf0, 0x83,
                                0x0e, 0,    0x5b, 0x5e, 0x5f, 0x5d, 0xc3};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    constexpr uint32_t destination = data_address + 0x100;
    const uint32_t stack[] = {1, 2, 3, 4, end, destination};
    X86_MemMap(state.get(), data_address, 4096, 3);
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
    const auto suffix = first->FirstOp()[2].handler;
    for (int i = 0; i < 3; ++i) {
        if (!run())
            return false;
    }
    auto* hot = FindCompiledBlock(state.get());
    return hot == first && hot->FirstOp()[2].handler == suffix;
}

bool VerifyReuseAndBudget(bool tight_budget) {
    uint8_t code[] = {0xb8, 2, 0, 0, 0, 0x05, 1, 0, 0, 0};
    auto state = LoadCode(code, sizeof(code));
    const auto end = code_address + sizeof(code);
    const auto run = [&](uint32_t expected) {
        X86_SetEIP(state.get(), code_address);
        return X86_Run(state.get(), end, 64) && X86_RegRead(state.get(), 0) == expected;
    };
    const auto write = [&](uint8_t opcode) {
        code[5] = opcode;
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
    write(0x2d);
    if (!run(1))
        return false;
    if (tight_budget) {
        if (HasCompiledEntry(state.get()) || X86_GetLLVMJitLiveBlockCount() != 1)
            return false;
        if (!run(1) || !HasCompiledEntry(state.get()) || X86_GetLLVMJitLiveBlockCount() != 1)
            return false;
        char stats[512];
        X86_DumpStats(state.get(), stats, sizeof(stats));
        return std::strstr(stats, "\"code_cache_flush_count\":1") != nullptr;
    }
    if (!HasCompiledEntry(state.get()) || X86_GetLLVMJitLiveBlockCount() != 2)
        return false;
    write(0x05);
    if (!run(3))
        return false;
    const auto* restored = FindCompiledBlock(state.get());
    return restored && restored->entry == original && X86_GetLLVMJitBlockCount() == 2;
}

} // namespace

int main(int argc, char** argv) {
    if (argc == 2 && std::strcmp(argv[1], "hot") == 0) {
        if (VerifyHotLoop() && VerifyUncompiledSuffix())
            return 0;
        std::fprintf(stderr, "LLVM hot backedge admission or native suffix mismatch\n");
        return 1;
    }
    if (argc == 2 && (std::strcmp(argv[1], "reuse") == 0 || std::strcmp(argv[1], "budget") == 0)) {
        if (VerifyReuseAndBudget(std::strcmp(argv[1], "budget") == 0))
            return 0;
        std::fprintf(stderr, "LLVM executable cache reuse or budget mismatch\n");
        return 1;
    }
    const bool enabled = argc == 2;
    if (!VerifyLongBlock(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM block execution or cache ownership mismatch\n");
        return 1;
    }
    if (!VerifyMemoryExit(enabled, false) || !VerifyMemoryExit(enabled, true) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM memory exit or scalar restart mismatch\n");
        return 1;
    }
    if (!VerifyDirectBranch(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM direct branch or exit mismatch\n");
        return 1;
    }
    if (!VerifyGenericConcat(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM generic re-decode or concatenated block mismatch\n");
        return 1;
    }
    if (!VerifyGroup1Registers(enabled) || !VerifyGroup1Memory(enabled) || X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Group1 flags, operands or memory restart mismatch\n");
        return 1;
    }
    if (!VerifyShiftBitRegisters(enabled) || !VerifyTzcntPrefix(enabled) || !VerifyShiftBitMemory(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Shift/bit flags, operands or memory restart mismatch\n");
        return 1;
    }
    if (!VerifyExtendedInteger(enabled) || !VerifyVectorPrefixRouting(enabled) || !VerifyX87Helpers(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "Generated integer, vector prefix or x87 template mismatch\n");
        return 1;
    }
    if (!VerifyStackWidths(enabled) || !VerifyStackReplay(enabled) || !VerifyCallReturn(enabled) || !VerifyTsc(enabled) ||
        X86_GetLLVMJitLiveBlockCount() != 0) {
        std::fprintf(stderr, "LLVM generic stack width, call or return mismatch\n");
        return 1;
    }
}
