#undef NDEBUG
#include <array>
#include <bit>
#include <cassert>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <string_view>
#include <thread>

#include "aarch64/dispatch.h"
#include "bindings.h"
#include "state.h"

namespace {
using namespace fiberish::aarch64;
using namespace fiberish::aarch64::detail;
using ArmBlock = fiberish::aarch64::detail::BasicBlock;

constexpr uint64_t Pc = 0x35'0040'0000;
constexpr uint64_t Alias = 0x72'0040'0000;
constexpr uint64_t Data = (1ull << 39) - PageBytes;

template <size_t N>
void Put(uint8_t* here, const std::array<uint32_t, N>& words) {
    std::memcpy(here, words.data(), sizeof(words));
}

void Run(EmuState* state, uint64_t pc, uint64_t budget) {
    AArch64_RegWrite(state, 32, pc);
    assert(AArch64_Run(state, 0, budget, 0));
}

ArmBlock* Lookup(EmuState* state, uint64_t pc, const uint8_t* code) {
    return state->mmu.lookup_arm_block(pc, code);
}

void WaitForJit(EmuState* state, uint64_t pc, unsigned budget, const uint8_t* code, unsigned minimum_size) {
    const auto timeout = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    do {
        AArch64_RegWrite(state, 0, 64);
        Run(state, pc, budget);
        const auto* block = Lookup(state, pc, code);
        if (block && block->header.entry != block->FirstOp()->handler) {
            if (minimum_size == 1)
                return;
            const auto& regions = state->mmu.code_cache().arm_blocks->regions;
            const auto* installed = regions.Find(pc & ~PageMask);
            if (installed && block->RegionContext() == installed->members && installed->count >= minimum_size &&
                installed->guest_page == (pc & ~PageMask))
                return;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    } while (std::chrono::steady_clock::now() < timeout);
    const auto* block = Lookup(state, pc, code);
    std::fprintf(stderr, "JIT pc=0x%llx, compiled=%d, expected at least %u members\n",
                 static_cast<unsigned long long>(pc), block && block->header.entry != block->FirstOp()->handler,
                 minimum_size);
    assert(false && "LLVM did not install the expected A64 entry");
}

void CheckLoop(EmuState* state, bool region) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{};
    const std::array loop{0x91000421u,  // add x1, x1, #1
                          0x10000005u,  // adr x5, .
                          0xd503201fu,  // nop, folded in the decoder
                          0x14000001u,  // b .+4
                          0xf1000400u,  // subs x0, x0, #1
                          0x54ffff61u}; // b.ne .-20
    Put(code.data(), loop);
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, Alias, code.data(), 5));
    WaitForJit(state, Pc, 32, code.data(), region ? 2 : 1);
    const auto* first = Lookup(state, Pc, code.data());
    const auto* second = Lookup(state, Pc + 16, code.data());
    assert(first && second);
    const auto clear_links = [](const ArmBlock& block) {
        auto& sentinel = GetBlockSentinel(*block.Sentinel());
        sentinel.links[0] = sentinel.links[1] = &ArmBlock::Invalid;
    };
    if (region) {
        assert(first->header.entry != second->header.entry);
        assert(first->RegionContext() == second->RegionContext());
        clear_links(*first);
        clear_links(*second);
    }
    AArch64_RegWrite(state, 0, 3);
    AArch64_RegWrite(state, 1, 0);
    Run(state, Pc, 18);
    assert(AArch64_RegRead(state, 0) == 0 && AArch64_RegRead(state, 1) == 3);
    assert(AArch64_RegRead(state, 32) == Pc + 24);
    assert(state->arm->nzcv == 0x60000000);
    if (region) {
        // Every edge in the loop ran in JIT code. The interpreter does not
        // discover or install a link in either member's sentinel.
        for (auto* member : {first, second})
            for (auto* target : GetBlockSentinel(*member->Sentinel()).links)
                assert(!target->IsValid());
    }

    AArch64_RegWrite(state, 0, 2);
    Run(state, Pc, 5);
    assert(AArch64_RegRead(state, 0) == 1 && AArch64_RegRead(state, 32) == Pc + 20);
    assert(state->arm->nzcv == 0x20000000);

    WaitForJit(state, Alias, 32, code.data(), region ? 2 : 1);
    const auto* alias = Lookup(state, Alias, code.data());
    assert(alias->header.entry == first->header.entry);
    if (region)
        assert(alias->RegionContext() && alias->RegionContext() != first->RegionContext());
    assert(AArch64_RegRead(state, 5) == Alias + 4);
    // The host changed an instruction through an alias and then flushed it,
    // as the managed MMU does for out-of-band writes.
    const auto* context = static_cast<ArmBlock* const*>(first->RegionContext());
    const auto revision = first->invalidation_revision;
    const auto stamp = state->mmu.arm_page_region_stamp(Pc);
    using RegionEntry = fiberish::mem::CodeCacheState::ArmBlocks::RegionEntry;
    std::array<RegionEntry, 2> old_entries{};
    if (region) {
        old_entries[0] = {Pc, context[0], revision, first->header.entry};
        old_entries[1] = {Pc + 16, context[1], second->invalidation_revision, second->header.entry};
    }
    Put(code.data(), std::array{0x91000821u}); // add x1, x1, #2
    AArch64_ResetCodeCacheByRange(state, Alias, 4);
    assert(!first->IsValid() && first->invalidation_revision != revision);
    if (region) {
        for (const auto* member : {first, second, alias})
            assert(member->header.entry == member->FirstOp()->handler);
    }
    assert(!first->RegionContext() && !second->RegionContext() && !alias->RegionContext());
    if (region)
        assert(stamp && !state->mmu.publish_arm_page_region(*stamp, old_entries));
    AArch64_RegWrite(state, 0, 3);
    AArch64_RegWrite(state, 1, 0);
    Run(state, Pc, 18);
    assert(AArch64_RegRead(state, 1) == 6);
    AArch64_MemUnmap(state, Alias, PageBytes);
    AArch64_MemUnmap(state, Pc, PageBytes);
}

void CheckRegionReplay(EmuState* state) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{}, data{};
    Put(code.data(), std::array{0xb1000421u,   // adds x1, x1, #1
                                0x14000001u,   // b .+4
                                0x10000005u,   // adr x5, . (wide slot)
                                0xd503201fu,   // nop (no slot)
                                0xf9400043u,   // ldr x3, [x2]
                                0x17fffffbu}); // b .-20
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, Data, data.data(), 3));
    AArch64_RegWrite(state, 2, Data);
    WaitForJit(state, Pc, 32, code.data(), 2);

    AArch64_MemUnmap(state, Data, PageBytes);
    AArch64_RegWrite(state, 1, UINT64_MAX);
    Run(state, Pc, 6);
    assert(AArch64_RegRead(state, 1) == 0 && AArch64_RegRead(state, 32) == Pc + 16);
    assert(AArch64_RegRead(state, 5) == Pc + 8);
    assert(X86_GetFaultVector(state) == 14 && AArch64_GetFaultAddress(state) == Data);
    assert(state->arm->nzcv == 0x60000000);

    struct Observer {
        uint8_t* code;
        uint8_t* data;
        bool called = false;
    } observer{code.data(), data.data()};
    AArch64_SetExtendedFaultCallback(
        state,
        +[](EmuState* cpu, uint64_t address, int access, void* opaque) -> int {
            auto& observer = *static_cast<Observer*>(opaque);
            assert(address == Data && !(access & 3) && !observer.called);
            assert(AArch64_RegRead(cpu, 32) == Pc + 16 && cpu->arm->nzcv == 0x60000000);
            observer.called = true;
            Put(observer.code + 20, std::array{0x910004e7u, 0x14000000u}); // add x7, x7, #1; b .
            AArch64_ResetCodeCacheByRange(cpu, Pc, PageBytes);
            return AArch64_MemMapHostPage(cpu, Data, observer.data, 3);
        },
        &observer);
    AArch64_RegWrite(state, 1, UINT64_MAX);
    AArch64_RegWrite(state, 7, 0);
    Run(state, Pc, 6);
    assert(observer.called && AArch64_RegRead(state, 7) == 1 && AArch64_RegRead(state, 32) == Pc + 24);
    AArch64_SetExtendedFaultCallback(state, nullptr, nullptr);
    AArch64_MemUnmap(state, Data, PageBytes);
    AArch64_MemUnmap(state, Pc, PageBytes);
}

void CheckRegionExternalLink(EmuState* state, bool indirect) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{}, next{};
    constexpr auto external = Pc + PageBytes;
    constexpr auto start = external - 32;
    constexpr auto second_pc = start + 12;
    Put(code.data() + PageBytes - 32, std::array{
                                          0x10000005u,                          // adr x5, .
                                          0xd503201fu,                          // nop (no slot)
                                          0x14000001u,                          // b .+4
                                          0x91000421u,                          // add x1, x1, #1
                                          0x10000006u,                          // adr x6, .
                                          0xd503201fu,                          // nop (no slot)
                                          0xf1000400u,                          // subs x0, x0, #1
                                          indirect ? 0xd61f00e0u : 0x54ffff21u, // br x7 / b.ne start
                                      });
    Put(next.data(), std::array{0x91002021u,   // add x1, x1, #8
                                0x14000000u}); // b .
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, external, next.data(), 5));
    AArch64_RegWrite(state, 7, start);
    WaitForJit(state, start, 16, code.data(), 2);
    Run(state, external, 2);

    auto* first = Lookup(state, start, code.data());
    auto* second = Lookup(state, second_pc, code.data());
    auto* target = Lookup(state, external, next.data());
    assert(first && second && target && first->header.entry != second->header.entry);
    assert(first->RegionContext() && first->RegionContext() == second->RegionContext());
    auto& first_exit = GetBlockSentinel(*first->Sentinel());
    auto& second_exit = GetBlockSentinel(*second->Sentinel());
    first_exit.links[1] = second_exit.links[1] = &ArmBlock::Invalid;
    AArch64_RegWrite(state, 7, external);
    for (unsigned run = 0; run < 2; ++run) {
        AArch64_RegWrite(state, 0, 1);
        AArch64_RegWrite(state, 1, 0);
        Run(state, start, 10);
        assert(AArch64_RegRead(state, 1) == 9 && AArch64_RegRead(state, 32) == external + 4);
        assert(first_exit.links[1] == &ArmBlock::Invalid && second_exit.links[1] == target);
    }

    AArch64_MemUnmap(state, Pc, 2 * PageBytes);
}

void CheckMemory(EmuState* state) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{}, data{};
    alignas(PageBytes) std::array<uint8_t, PageBytes> next{};
    const auto address = Pc + PageBytes - 16;
    const std::array loop{0x91000421u,  // add x1, x1, #1
                          0xf9400043u,  // ldr x3, [x2]
                          0xf9000083u,  // str x3, [x4]
                          0x14000001u}; // b .+4, cross the page boundary
    Put(code.data() + PageBytes - sizeof(loop), loop);
    Put(next.data(), std::array{0x14000000u}); // b .
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, Pc + PageBytes, next.data(), 5));
    assert(AArch64_MemMapHostPage(state, Data, data.data(), 3));
    constexpr uint64_t magic = 0xc0ffeecafe123456;
    std::memcpy(data.data(), &magic, sizeof(magic));
    AArch64_RegWrite(state, 2, Data);
    AArch64_RegWrite(state, 4, Data + 8);
    WaitForJit(state, address, 12, code.data(), 1);
    AArch64_RegWrite(state, 1, 0);
    Run(state, address, 4);
    uint64_t stored = 0;
    std::memcpy(&stored, data.data() + 8, sizeof(stored));
    assert(stored == magic);
    assert(AArch64_RegRead(state, 1) == 1 && AArch64_RegRead(state, 32) == Pc + PageBytes);

    AArch64_RegWrite(state, 2, Data - PageBytes);
    Run(state, address, 4);
    assert(X86_GetFaultVector(state) == 14 && AArch64_GetFaultAddress(state) == Data - PageBytes);
    assert(AArch64_RegRead(state, 1) == 2 && AArch64_RegRead(state, 32) == address + 4);

    AArch64_RegWrite(state, 2, Data);
    AArch64_MemProtect(state, Data, PageBytes, 1);
    Run(state, address, 4);
    assert(X86_GetFaultVector(state) == 14 && AArch64_GetFaultAddress(state) == Data + 8);
    assert(AArch64_RegRead(state, 1) == 3 && AArch64_RegRead(state, 32) == address + 8);

    AArch64_MemUnmap(state, Data, PageBytes);
    AArch64_MemUnmap(state, Pc, 2 * PageBytes);
}

void CheckRegisterAndRefetch(EmuState* state, bool region) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{};
    Put(code.data(), std::array{0xd51b4208u,   // msr nzcv, x8
                                0x9a8900a5u,   // csel x5, x5, x9, eq
                                0x14000001u,   // b .+4
                                0x4f00e460u,   // movi v0.16b, #3
                                0x4e208401u,   // add v1.16b, v0.16b, v0.16b
                                0x1e642862u,   // fadd d2, d3, d4
                                0x1e632040u,   // fcmp d2, d3
                                0xd5033fdfu,   // isb
                                0xd53b4206u,   // mrs x6, nzcv
                                0x14000000u}); // b .
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    AArch64_RegWrite(state, 8, 0x40000000);
    AArch64_RegWrite(state, 70, std::bit_cast<uint64_t>(1.0));
    AArch64_RegWrite(state, 72, std::bit_cast<uint64_t>(2.0));
    WaitForJit(state, Pc, 8, code.data(), region ? 2 : 1);
    WaitForJit(state, Pc + 12, 5, code.data(), region ? 2 : 1);
    const auto* block = Lookup(state, Pc + 12, code.data());
    assert(block->inst_count() == 5);
    if (region) {
        const auto* first = Lookup(state, Pc, code.data());
        assert(first->header.entry != block->header.entry);
        assert(first->RegionContext() && first->RegionContext() == block->RegionContext());
    }

    AArch64_RegWrite(state, 5, 42);
    AArch64_RegWrite(state, 9, 77);
    AArch64_RegWrite(state, 6, UINT64_MAX);
    Run(state, Pc, 8);
    assert(AArch64_RegRead(state, 32) == Pc + 32);
    assert(AArch64_RegRead(state, 5) == 42 && AArch64_RegRead(state, 6) == UINT64_MAX);
    assert(AArch64_RegRead(state, 66) == 0x0606060606060606 && AArch64_RegRead(state, 67) == 0x0606060606060606);
    assert(AArch64_RegRead(state, 68) == std::bit_cast<uint64_t>(3.0));
    assert(state->arm->nzcv == 0x20000000);
    Run(state, Pc + 32, 1);
    assert(AArch64_RegRead(state, 6) == 0x20000000);
    AArch64_MemUnmap(state, Pc, PageBytes);
}

void CheckAtomicAndSystemReplay(EmuState* state) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{}, data{};
    Put(code.data(), std::array{0xc85f7c6au,   // ldxr x10, [x3]
                                0xc80b7c62u,   // stxr w11, x2, [x3]
                                0xf8e2006au,   // ldaddal x2, x10, [x3]
                                0xd50b7423u,   // dc zva, x3
                                0x14000000u}); // b .
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, Data, data.data(), 3));
    AArch64_RegWrite(state, 2, 17);
    AArch64_RegWrite(state, 3, Data);
    for (unsigned i = 0; i < 4; ++i)
        WaitForJit(state, Pc + i * 4, 1, code.data(), 1);

    constexpr uint64_t initial = 5;
    AArch64_MemWrite(state, Data, reinterpret_cast<const uint8_t*>(&initial), sizeof(initial));
    Run(state, Pc, 2);
    assert(AArch64_RegRead(state, 10) == initial && AArch64_RegRead(state, 11) == 0);
    assert(AArch64_RegRead(state, 32) == Pc + 8);
    Run(state, Pc + 8, 1);
    uint64_t stored = 0;
    std::memcpy(&stored, data.data(), sizeof(stored));
    assert(AArch64_RegRead(state, 10) == 17 && stored == 34);
    Run(state, Pc + 12, 1);
    for (unsigned i = 0; i < 32; ++i)
        assert(!data[i]);

    AArch64_MemProtect(state, Data, PageBytes, 1);
    AArch64_RegWrite(state, 11, 83);
    Run(state, Pc, 2);
    assert(X86_GetFaultVector(state) == 14 && AArch64_GetFaultAddress(state) == Data);
    assert(AArch64_RegRead(state, 32) == Pc + 4 && AArch64_RegRead(state, 11) == 83);
    Run(state, Pc + 12, 1);
    assert(X86_GetFaultVector(state) == 14 && AArch64_GetFaultAddress(state) == Data);
    assert(AArch64_RegRead(state, 32) == Pc + 12);

    AArch64_MemUnmap(state, Data, PageBytes);
    AArch64_MemUnmap(state, Pc, PageBytes);
}

void CheckVectorStructureReplay(EmuState* state) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{}, data{};
    Put(code.data(), std::array{0x4cdfa063u,   // ld1 {v3.16b, v4.16b}, [x3], #32
                                0x4c9fa063u}); // st1 {v3.16b, v4.16b}, [x3], #32
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, Data, data.data(), 3));
    for (unsigned i = 0; i < 32; ++i)
        data[i] = uint8_t(i);
    AArch64_RegWrite(state, 3, Data);
    WaitForJit(state, Pc, 1, code.data(), 1);
    WaitForJit(state, Pc + 4, 1, code.data(), 1);

    AArch64_RegWrite(state, 3, Data);
    Run(state, Pc, 1);
    assert(AArch64_RegRead(state, 3) == Data + 32);
    assert(AArch64_RegRead(state, 70) == 0x0706050403020100);
    assert(AArch64_RegRead(state, 73) == 0x1f1e1d1c1b1a1918);
    Run(state, Pc + 4, 1);
    assert(AArch64_RegRead(state, 3) == Data + 64);
    assert(std::memcmp(data.data(), data.data() + 32, 32) == 0);

    AArch64_RegWrite(state, 3, Data + PageBytes - 16);
    Run(state, Pc, 1);
    assert(X86_GetFaultVector(state) == 14 && AArch64_RegRead(state, 32) == Pc);
    assert(AArch64_RegRead(state, 3) == Data + PageBytes - 16);
    assert(AArch64_RegRead(state, 70) == 0x0706050403020100);
    assert(AArch64_RegRead(state, 73) == 0x1f1e1d1c1b1a1918);

    AArch64_MemUnmap(state, Data, PageBytes);
    AArch64_MemUnmap(state, Pc, PageBytes);
}
void CheckStandaloneReuse(EmuState* state) {
    alignas(PageBytes) std::array<uint8_t, PageBytes> code{};
    constexpr uint64_t Other = 128;
    const std::array words{0x1000000eu,  // adr x14, .
                           0x14000002u}; // b .+8
    Put(code.data(), words);
    Put(code.data() + Other, words);
    assert(AArch64_MemMapHostPage(state, Pc, code.data(), 5));
    WaitForJit(state, Pc, 2, code.data(), 1);
    auto* first = Lookup(state, Pc, code.data());
    const auto before = X86_GetLLVMJitBlockCount();
    WaitForJit(state, Pc + Other, 2, code.data(), 1);
    assert(X86_GetLLVMJitBlockCount() == before);
    assert(Lookup(state, Pc + Other, code.data())->header.entry == first->header.entry);

    Run(state, Pc + Other, 2);
    assert(AArch64_RegRead(state, 14) == Pc + Other);
    assert(AArch64_RegRead(state, 32) == Pc + Other + 12);
    Run(state, Pc, 2);
    assert(AArch64_RegRead(state, 14) == Pc);
    assert(AArch64_RegRead(state, 32) == Pc + 12);
    AArch64_MemUnmap(state, Pc, PageBytes);
}
} // namespace

int main(int argc, char**) {
    const bool region = argc > 1;
    using namespace std::string_view_literals;
    const auto options = region ? "JitMode=region\0JitHotThreshold=1\0"sv : "JitMode=exact\0JitHotThreshold=1\0"sv;
    char error[256]{};
    auto* run = X86_CreateRun(options.data(), options.size(), error, sizeof(error));
    if (!run)
        std::fprintf(stderr, "%s\n", error);
    assert(run);
    auto* state = X86_CreateForRun(run);
    assert(state);
    AArch64_Reset(state);
    const auto before = X86_GetLLVMJitBlockCount();
    CheckLoop(state, region);
    CheckMemory(state);
    CheckRegisterAndRefetch(state, region);
    CheckAtomicAndSystemReplay(state);
    CheckVectorStructureReplay(state);
    if (region) {
        CheckRegionReplay(state);
        CheckRegionExternalLink(state, false);
        CheckRegionExternalLink(state, true);
    } else {
        CheckStandaloneReuse(state);
    }
    assert(X86_GetLLVMJitBlockCount() > before && X86_GetLLVMJitLiveBlockCount() > 0);
    X86_Destroy(state);
    X86_DestroyRun(run);
    assert(X86_GetLLVMJitLiveBlockCount() == 0);
}
