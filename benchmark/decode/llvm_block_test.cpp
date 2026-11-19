#include "bindings.h"
#include "decoder.h"

#include <array>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <thread>
#include <vector>

#include <unistd.h>

namespace {

constexpr uint32_t code_address = 0x10000;
constexpr uint32_t data_address = 0x20000;
constexpr int ss_segment = 2;
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
    State peer{X86_Clone(state.get(), 1), X86_Destroy};
    state.reset();
    if (X86_GetLLVMJitLiveBlockCount() != unsigned(enabled && !linked))
        return false;

    X86_ResetAllCodeCache(peer.get());
    if (X86_GetLLVMJitLiveBlockCount() != 0)
        return false;
    const auto compilation_count = linked ? X86_GetLinkedBlockHitCount : X86_GetLLVMJitBlockCount;
    const auto compiled = compilation_count();
    X86_SetEIP(peer.get(), code_address);
    return X86_Run(peer.get(), end, 64) && X86_RegRead(peer.get(), 0) == 14 &&
           compilation_count() == compiled + unsigned(enabled);
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
    X86_MemMap(state.get(), data_address, 4096, 3);
    X86_MemMap(state.get(), ss_base, 4096, 3);
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
                X86_MemMap(state.get(), data_address, 4096, 3);
                X86_MemMap(state.get(), ss_base, 4096, 3);
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
        X86_MemMap(state.get(), stack_page, 4096, 3);
        X86_MemWrite(state.get(), untouched, reinterpret_cast<const uint8_t*>(&input), sizeof(input));
        if (!source_unmapped)
            X86_ReprotectMappedRange(state.get(), stack_page, 4096, 1);
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

bool VerifyX87Helpers(bool enabled) {
    constexpr uint8_t code[] = {
        0xd9, 0xe8, // FLD1
        0xd9, 0xe8, // FLD1
        0xde, 0xc1, // FADDP ST(1), ST(0)
        0xd9, 0x19, // FSTP dword [ECX]
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
    constexpr uint8_t code[] = {0x66, 0x50, 0x66, 0x5a}; // PUSH AX; POP DX
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
    constexpr uint8_t code[] = {0x66, 0x9c, 0x66, 0x58}; // PUSHF word; POP AX
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
        0xe8, 2,  0, 0, 0, // CALL callee
        0xeb, 6,           // JMP end
        0xb8, 42, 0, 0, 0, // MOV EAX, 42
        0xc3,              // RET
    };
    auto state = LoadCode(code, sizeof(code));
    X86_MemMap(state.get(), data_address, 4096, 3);
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

bool VerifyPackedArena() {
    const uint8_t add[] = {0xb8, 2, 0, 0, 0, 0x05, 1, 0, 0, 0};
    const uint8_t sub[] = {0xb8, 7, 0, 0, 0, 0x2d, 1, 0, 0, 0};
    const auto end = code_address + sizeof(add);
    auto a = LoadCode(add, sizeof(add));
    auto b = LoadCode(sub, sizeof(sub));
    auto peer = LoadCode(add, sizeof(add));
    const auto run = [&](EmuState* state, uint32_t expected) {
        X86_SetEIP(state, code_address);
        return X86_Run(state, end, 16) && X86_RegRead(state, 0) == expected;
    };
    if (!run(a.get(), 3) || !run(b.get(), 6) || !run(peer.get(), 3))
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

    X86_ResetAllCodeCache(a.get());
    if (!run(peer.get(), 3) || !run(b.get(), 6) || X86_GetLLVMJitLiveBlockCount() != 2)
        return false;
    X86_ResetAllCodeCache(peer.get());
    if (!run(b.get(), 6) || X86_GetLLVMJitLiveBlockCount() != 1)
        return false;
    X86_ResetAllCodeCache(b.get());
    if (X86_GetLLVMJitLiveBlockCount() != 0 || !run(a.get(), 3))
        return false;
    const auto* reused = FindCompiledBlock(a.get());
    return reused && reinterpret_cast<uintptr_t>(reused->entry) == first_address && X86_GetLLVMJitBlockCount() == 3;
}

bool VerifyPackedBudget() {
    uint8_t code[] = {0xb8, 1, 0, 0, 0, 0x05, 1, 0, 0, 0};
    const auto end = code_address + sizeof(code);
    auto state = LoadCode(code, sizeof(code));
    const auto run = [&](uint32_t expected) {
        X86_SetEIP(state.get(), code_address);
        return X86_Run(state.get(), end, 16) && X86_RegRead(state.get(), 0) == expected;
    };
    if (!run(2) || !HasCompiledEntry(state.get()))
        return false;
    for (uint8_t immediate = 2; immediate <= 64; ++immediate) {
        code[1] = immediate;
        X86_MemWrite(state.get(), code_address, code, sizeof(code));
        X86_ResetCodeCacheByRange(state.get(), code_address, sizeof(code));
        if (!run(immediate + 1))
            return false;
        if (HasCompiledEntry(state.get()))
            continue;
        if (!run(immediate + 1) || !HasCompiledEntry(state.get()) || X86_GetLLVMJitLiveBlockCount() != 1)
            return false;
        char stats[512];
        X86_DumpStats(state.get(), stats, sizeof(stats));
        return std::strstr(stats, "\"code_cache_flush_count\":1") != nullptr;
    }
    return false;
}

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
    if (argc == 2 && std::strcmp(argv[1], "budget") == 0) {
        if (VerifyPackedBudget())
            return 0;
        std::fprintf(stderr, "LLVM packed allocator did not enforce the byte budget\n");
        return 1;
    }
    if (argc == 2 && std::strcmp(argv[1], "async") == 0) {
        if (VerifyAsyncCompilations())
            return 0;
        std::fprintf(stderr, "Async LLVM installation, invalidation or code reuse mismatch\n");
        return 1;
    }
    if (argc == 2 && std::strcmp(argv[1], "hot") == 0) {
        if (VerifyHotLoop() && VerifyUncompiledSuffix())
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
    if (!VerifyExtendedInteger(enabled) || !VerifyVectorPrefixRouting(enabled) || !VerifyX87Helpers(enabled) ||
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
