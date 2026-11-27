#include "bindings.h"
#include "block_limits.h"

#undef NDEBUG
#include <array>
#include <cassert>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <iterator>
#include <map>
#include <string>
#include <vector>

namespace {

constexpr uint64_t Top = 1ull << 39;
constexpr uint64_t LowPc = 0x0040'0000;
constexpr uint64_t HighPc = 0x1'0040'0000;
constexpr uint32_t PageSize = 4096;
constexpr uint64_t HighData = Top - PageSize;
constexpr uint64_t Alias = 0x20'0040'0000;

template <class T>
void put(uint8_t* where, T value) {
    std::memcpy(where, &value, sizeof value);
}

struct Fault {
    uint64_t address = 0;
    int flags = 0;
    uint8_t* backing;
};

int resolve(EmuState* state, uint64_t address, int flags, void* context) {
    auto& fault = *static_cast<Fault*>(context);
    fault.address = address;
    fault.flags = flags;
    assert(AArch64_GetFaultAddress(state) == address);
    return AArch64_MemMapHostPage(state, address & ~uint64_t{PageSize - 1}, fault.backing, 3);
}

void observe(EmuState*, uint64_t address, uint32_t size, int write, uint64_t value, void* context) {
    assert(size == 8 && !write && value == 0x123456789abcdef0ULL);
    *static_cast<uint64_t*>(context) = address;
}

void run(EmuState* state, uint64_t pc, uint64_t budget) {
    AArch64_RegWrite(state, 32, pc);
    assert(AArch64_Run(state, 0, budget, 0));
}

void profile_block_boundaries() {
    const auto nonce = std::chrono::steady_clock::now().time_since_epoch().count();
    const auto path = "aarch64-mmu-test-" + std::to_string(nonce) + ".json";
    const auto args = "AArch64ProfilePath=" + path;
    char error[256]{};
    auto* context = X86_CreateRun(args.c_str(), args.size() + 1, error, sizeof(error));
    assert(context);
    auto* state = X86_CreateForRun(context);
    assert(state);
    AArch64_Reset(state);

    constexpr uint32_t A = 0x91000400, B = 0x91000800, C = 0x91000c00, D = 0x91001000, E = 0x91001400;
    constexpr uint32_t BCond = 0x54000020, BSkip = 0x14000002, Nop = 0xd503201f, Load = 0xf9400041;
    alignas(PageSize) std::array<uint8_t, PageSize> first{}, second{};
    put(first.data() + PageSize - 8, A);
    put(first.data() + PageSize - 4, B);
    constexpr auto max = fiberish::BlockMax;
    for (unsigned i = 0; i < max; ++i)
        put(second.data() + i * 4, C);
    put(second.data() + max * 4, BCond); // b.eq .+4, a block boundary even on fallthrough
    put(second.data() + (max + 1) * 4, D);
    put(second.data() + (max + 2) * 4, BSkip); // jump over an unexecuted word
    put(second.data() + (max + 4) * 4, E);
    put(second.data() + (max + 5) * 4, Load); // ldr x1,[x2] faults at zero
    const std::array extra{A, B, C, Nop, A, B, C};
    std::memcpy(first.data(), extra.data(), sizeof(extra));
    const std::array with_hook{A, Load, C};
    std::memcpy(first.data() + 128, with_hook.data(), sizeof(with_hook));
    assert(AArch64_MemMapHostPage(state, HighPc, first.data(), 5));
    assert(AArch64_MemMapHostPage(state, HighPc + PageSize, second.data(), 5));
    for (unsigned i = 0; i < 2; ++i) {
        run(state, HighPc + PageSize - 8, max + 16);
        assert(X86_GetFaultVector(state) == 14 && AArch64_RegRead(state, 32) == HighPc + PageSize + (max + 5) * 4);
    }
    run(state, HighPc, extra.size());
    AArch64_RegWrite(state, 32, HighPc);
    AArch64_Run(state, 0, 1, 1);
    AArch64_Run(state, 0, 1, 1);
    assert(AArch64_RegRead(state, 32) == HighPc + 8);

    // A successful slow load ends the current trace at refetch. The nested
    // run attempted by its callback must not contribute any instructions.
    unsigned callbacks = 0;
    AArch64_SetMemHook(
        state,
        [](EmuState* cpu, uint64_t, uint32_t, int, uint64_t, void* opaque) {
            ++*static_cast<unsigned*>(opaque);
            assert(!AArch64_Run(cpu, 0, 1, 0));
        },
        &callbacks);
    AArch64_RegWrite(state, 2, HighPc);
    run(state, HighPc + 128, with_hook.size());
    assert(callbacks == 1 && AArch64_RegRead(state, 32) == HighPc + 140);
    X86_Destroy(state);
    assert(X86_FinishRun(context, error, sizeof(error)));
    X86_DestroyRun(context);

    std::ifstream stream(path);
    assert(stream);
    const std::string json{std::istreambuf_iterator<char>(stream), {}};
    stream.close();
    assert(!std::remove(path.c_str()));
    assert(json.find(R"("version":2,"architecture":"aarch64")") != std::string::npos);
    const std::map<std::vector<uint32_t>, unsigned> expected = {
        {{A, B}, 2},    {std::vector<uint32_t>(max, C), 2},
        {{BCond}, 2},   {{D, BSkip}, 2},
        {{E}, 2},       {std::vector<uint32_t>(extra.begin(), extra.end()), 1},
        {{A}, 1},       {{B}, 1},
        {{A, Load}, 1}, {{C}, 1},
    };
    std::string blocks = R"("blocks":[)";
    for (const auto& [words, count] : expected) {
        if (blocks.back() == '}')
            blocks += ',';
        blocks += R"({"words":[)";
        for (auto word : words) {
            if (blocks.back() != '[')
                blocks += ',';
            blocks += std::to_string(word);
        }
        blocks += R"(],"count":)" + std::to_string(count) + '}';
    }
    assert(json.find(blocks + "]") != std::string::npos);
}

#if FIBERCPU_GUEST_I386
void switch_address_space() {
    auto* state = X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS);
    assert(state);
    alignas(PageSize) std::array<uint8_t, PageSize> arm_code{}, x86_code{}, replacement{};
    put(arm_code.data(), 0x10000000u); // adr x0, .
    x86_code[0] = 0x90;                // nop
    replacement[0] = 0xb8;             // mov eax, 1234
    put(replacement.data() + 1, 1234u);

    AArch64_Reset(state);
    assert(AArch64_MemMapHostPage(state, HighPc, arm_code.data(), 5));
    run(state, HighPc, 1);
    assert(AArch64_RegRead(state, 0) == HighPc);
    assert(X86_MemMapHostPage(state, LowPc, x86_code.data(), 5));
    X86_SetEIP(state, static_cast<uint32_t>(LowPc));
    X86_Step(state);
    assert(X86_GetEIP(state) == LowPc + 1);

    X86_MemUnmap(state, LowPc, PageSize);
    assert(X86_MemMapHostPage(state, LowPc, replacement.data(), 5));
    auto* child = X86_Clone(state, 0);
    assert(child);
    assert(AArch64_ResolvePtrForRead(child, LowPc) == replacement.data());
    run(child, HighPc, 1);
    assert(AArch64_RegRead(child, 0) == HighPc);

    run(state, HighPc, 1);
    assert(AArch64_RegRead(state, 0) == HighPc);
    assert(AArch64_ResolvePtrForRead(state, LowPc) == replacement.data());
    X86_SetEIP(state, static_cast<uint32_t>(LowPc));
    assert(X86_Run(state, 0, 1));
    assert(X86_GetEIP(state) == LowPc + 5);
    assert(X86_RegRead(state, 0) == 1234);

    assert(X86_ResetMemory(state));
    assert(!AArch64_ResolvePtrForRead(state, HighPc));
    assert(AArch64_ResolvePtrForRead(child, HighPc) == arm_code.data());
    X86_Destroy(child);
    X86_Destroy(state);
}
#endif

} // namespace

int main() {
    profile_block_boundaries();
#if FIBERCPU_GUEST_I386
    switch_address_space();
#endif
    auto* state = X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS);
    assert(state);
    alignas(PageSize) std::array<uint8_t, PageSize> code{}, data{}, replacement{}, low{}, cross{};
    put(code.data(), 0x10000000u);      // adr x0, .
    put(code.data() + 4, 0xf9400041u);  // ldr x1, [x2]
    put(code.data() + 8, 0x14000001u);  // b .+4
    put(code.data() + 12, 0xf9000041u); // str x1, [x2]
    put(code.data() + 16, 0x14000001u);
    put(data.data(), 0x123456789abcdef0ULL);
    put(replacement.data(), 0x0fedcba987654321ULL);

    // Legacy low mapping installed before ISA selection becomes an arm mapping.
    assert(X86_MemMapHostPage(state, LowPc, code.data(), 5));
    assert(AArch64_ResolvePtrForRead(state, LowPc) == code.data());
    AArch64_Reset(state);
    assert(X86_ResolvePtrForRead(state, LowPc) == code.data());
    assert(AArch64_MemMapHostPage(state, HighPc, code.data(), 5));
    assert(AArch64_MemMapHostPage(state, HighData, data.data(), 3));
    assert(!AArch64_MemMapHostPage(state, Top, data.data(), 3));
    assert(!AArch64_MemMapHostPage(state, UINT64_MAX, data.data(), 3));
    assert(!AArch64_MemMapHostPage(state, HighData + 1, data.data(), 3));
    AArch64_RegWrite(state, 2, HighData);
    run(state, LowPc, 3);
    assert(AArch64_RegRead(state, 0) == LowPc && AArch64_RegRead(state, 1) == 0x123456789abcdef0ULL);
    run(state, HighPc, 3);
    assert(AArch64_RegRead(state, 0) == HighPc && AArch64_RegRead(state, 1) == 0x123456789abcdef0ULL);
    assert(AArch64_RegRead(state, 32) == HighPc + 12);

    uint64_t observed = 0;
    AArch64_SetMemHook(state, observe, &observed);
    run(state, HighPc + 4, 1);
    assert(observed == HighData);
    AArch64_SetMemHook(state, nullptr, nullptr);

    // A wide fault bridge preserves the reported address and resolves a page.
    Fault fault{.backing = data.data()};
    AArch64_SetExtendedFaultCallback(state, resolve, &fault);
    const uint64_t newly_mapped = HighData - PageSize;
    AArch64_RegWrite(state, 2, newly_mapped);
    run(state, HighPc + 4, 1);
    assert(fault.address == newly_mapped && (fault.flags & 3) == 0);
    assert(AArch64_RegRead(state, 1) == 0x123456789abcdef0ULL);
    AArch64_SetExtendedFaultCallback(state, nullptr, nullptr);

    // A shared core observes remap and protection changes after its TLB was
    // populated. A forked core preserves its independent mappings.
    auto* sibling = X86_Clone(state, 1);
    auto* child = X86_Clone(state, 0);
    assert(sibling && child);
    AArch64_RegWrite(sibling, 2, HighData);
    AArch64_RegWrite(child, 2, HighData);
    run(sibling, HighPc, 3);
    assert(AArch64_RegRead(sibling, 1) == 0x123456789abcdef0ULL);
    assert(AArch64_MemMapHostPage(state, HighData, replacement.data(), 3));
    run(sibling, HighPc, 3);
    assert(AArch64_RegRead(sibling, 1) == 0x0fedcba987654321ULL);
    run(child, HighPc, 3);
    assert(AArch64_RegRead(child, 1) == 0x123456789abcdef0ULL);

    // Guest writes via a high alias invalidate both executable PCs.
    assert(AArch64_MemMapHostPage(state, Alias, code.data(), 3));
    assert(AArch64_MemHasSlowWrite(state, Alias));
    uint32_t mov7 = 0xd28000e0; // mov x0, #7
    AArch64_RegWrite(state, 2, Alias);
    AArch64_RegWrite(state, 1, mov7 | (uint64_t{0xf9400041} << 32));
    run(state, HighPc + 12, 2);
    AArch64_RegWrite(state, 2, HighData);
    run(state, HighPc, 3);
    assert(AArch64_RegRead(state, 0) == 7);
    run(state, LowPc, 3);
    assert(AArch64_RegRead(state, 0) == 7);
    assert(AArch64_MemIsDirty(state, Alias));
    put(code.data(), 0x10000000u);
    AArch64_ResetCodeCacheByRange(state, HighPc, 4);
    run(state, HighPc, 3);
    assert(AArch64_RegRead(state, 0) == HighPc);
    run(state, LowPc, 3);
    assert(AArch64_RegRead(state, 0) == LowPc);

    AArch64_MemProtect(state, HighData, PageSize, 0);
    assert(!AArch64_ResolvePtrForRead(state, HighData));
    AArch64_RegWrite(sibling, 2, HighData);
    run(sibling, HighPc + 4, 1);
    assert(X86_GetFaultVector(sibling) == 14 && AArch64_GetFaultAddress(sibling) == HighData);

    // A CPU access crossing 4 GiB must use distinct adjacent host pages.
    assert(X86_MemMapHostPage(state, 0xfffff000, low.data(), 3));
    assert(AArch64_MemMapHostPage(state, 0x1'0000'0000, cross.data(), 3));
    put(low.data() + PageSize - 4, 0x89abcdefu);
    put(cross.data(), 0x01234567u);
    AArch64_RegWrite(state, 2, 0xfffffffc);
    run(state, HighPc + 4, 1);
    assert(AArch64_RegRead(state, 1) == 0x0123456789abcdefULL);
    AArch64_MemProtect(state, 0x1'0000'0000, PageSize, 1);
    AArch64_RegWrite(state, 1, 0xffffdeadbeefULL);
    run(state, HighPc + 12, 1);
    assert(X86_GetFaultVector(state) == 14);
    assert(low[PageSize - 4] == 0xef && cross[0] == 0x67);

    std::array<AArch64_PageMapping, 16> mappings{};
    const auto found = AArch64_CollectMappedPages(state, 0, Top, mappings.data(), mappings.size());
    assert(found == 7);
    for (size_t i = 1; i < found; ++i)
        assert(mappings[i - 1].guest_page < mappings[i].guest_page);
    assert(mappings[found - 1].guest_page == HighData);
    AArch64_MemUnmap(state, 0x1'0000'0000, Top);
    assert(AArch64_CollectMappedPages(state, 0, Top, mappings.data(), mappings.size()) == 2);
    assert(X86_ResolvePtrForRead(state, LowPc) == code.data());
    assert(!AArch64_ResolvePtrForRead(state, HighPc));

    X86_Destroy(sibling);
    X86_Destroy(child);
    X86_Destroy(state);
}
