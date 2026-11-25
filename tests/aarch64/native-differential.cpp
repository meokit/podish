// Original differential harness under the repository license in LICENSE.md.

#include "aarch64/aarch64.h"
#include "aarch64/baked.h"
#include "aarch64/lowering.h"

#include <array>
#include <bit>
#include <cerrno>
#include <cinttypes>
#include <csignal>
#include <cstdio>
#include <limits>
#include <random>
#include <sys/wait.h>
#include <unistd.h>

using namespace fiberish::aarch64;

namespace {

struct TestCase {
    uint32_t word;
    const char* mnemonic;
    void (*reference)(State&);
    Handler baked;
    detail::ThreadHandler threaded;
    bool supports_dead_nzcv;
};

// Run one instruction directly on the AArch64 host with the same register
// inputs as the interpreter. Save and restore the host FP environment.
#define A64_CASE(word, mnemonic)                                                                                       \
    void Native_##word(State& state) {                                                                                 \
        asm volatile(                                                                                                  \
            "mrs x14, fpcr\n\tmrs x15, fpsr\n\t"                                                                       \
            "msr fpcr, %x[fpcr]\n\tmsr fpsr, xzr\n\tmsr nzcv, %x[nzcv]\n\t"                                            \
            "ldp x0, x1, [%[x]]\n\tldp x2, x3, [%[x], #16]\n\tldp x4, x5, [%[x], #32]\n\t"                             \
            "ldp q0, q1, [%[v]]\n\tldp q2, q3, [%[v], #32]\n\tldp q4, q5, [%[v], #64]\n\t"                             \
            ".inst 0x" #word "\n\t"                                                                                    \
            "stp x0, x1, [%[x]]\n\tstp x2, x3, [%[x], #16]\n\tstp x4, x5, [%[x], #32]\n\t"                             \
            "stp q0, q1, [%[v]]\n\tstp q2, q3, [%[v], #32]\n\tstp q4, q5, [%[v], #64]\n\t"                             \
            "mrs %x[new_nzcv], nzcv\n\tmrs %x[new_fpsr], fpsr\n\t"                                                     \
            "mov %x[host_sp], sp\n\t"                                                                                  \
            "msr fpcr, x14\n\tmsr fpsr, x15\n\t"                                                                       \
            : [new_nzcv] "=r"(state.nzcv), [new_fpsr] "=r"(state.fpsr), [host_sp] "=r"(state.sp)                       \
            : [x] "r"(state.x.data()), [v] "r"(state.v.data()), [fpcr] "r"(state.fpcr), [nzcv] "r"(state.nzcv)         \
            : "x0", "x1", "x2", "x3", "x4", "x5", "x14", "x15", "v0", "v1", "v2", "v3", "v4", "v5", "cc", "memory");   \
    }
#define A64_FLAG_CASE A64_CASE
#include "native-differential-cases.inc"
#undef A64_FLAG_CASE
#undef A64_CASE

constexpr TestCase Cases[] = {
#define A64_ENTRY(word, mnemonic, dead_nzcv)                                                                           \
    {0x##word##u, mnemonic, Native_##word, BakedHandler<0x##word##u>(), BakedThreadHandler<0x##word##u>(), dead_nzcv},
#define A64_CASE(word, mnemonic) A64_ENTRY(word, mnemonic, false)
#define A64_FLAG_CASE(word, mnemonic) A64_ENTRY(word, mnemonic, true)
#include "native-differential-cases.inc"
#undef A64_FLAG_CASE
#undef A64_CASE
#undef A64_ENTRY
};

constexpr unsigned Seed = 194629;
constexpr unsigned TrialsPerCase = 80;
constexpr uint64_t CodeAddress = 0x1000;
using CodePage = std::array<uint8_t, 4096>;

const TestCase* FindCase(uint32_t word) {
    for (const auto& test : Cases)
        if (test.word == word)
            return &test;
    return nullptr;
}

struct InvalidCase {
    uint32_t word;
    void (*reference)();
};

template <uint32_t Word>
void NativeInvalid() {
    asm volatile(".inst %c0" ::"i"(Word) : "memory");
}

constexpr InvalidCase InvalidCases[] = {
    {0x000000ff, NativeInvalid<0x000000ff>}, // UDF #255
    {0xffffffff, NativeInvalid<0xffffffff>}, // unallocated
    {0x12c00000, NativeInvalid<0x12c00000>}, // MOVN W with invalid halfword
    {0x32400000, NativeInvalid<0x32400000>}, // logical immediate W with N=1
    {0x0a008000, NativeInvalid<0x0a008000>}, // AND W with shift #32
    {0x0bc00000, NativeInvalid<0x0bc00000>}, // ADD (shifted register) with ROR
    {0x13808000, NativeInvalid<0x13808000>}, // EXTR W with bit index #32
    {0x1ea00800, NativeInvalid<0x1ea00800>}, // FMUL with reserved precision
};

bool NativeTraps(const InvalidCase& test) {
    const auto child = fork();
    if (child < 0)
        return false;
    if (!child) {
        std::signal(SIGILL, [](int) { _exit(100); });
        test.reference();
        _exit(0);
    }
    int status;
    pid_t result;
    do {
        result = waitpid(child, &status, 0);
    } while (result < 0 && errno == EINTR);
    return result == child && WIFEXITED(status) && WEXITSTATUS(status) == 100;
}

State MakeState(std::mt19937_64& random, unsigned trial) {
    State state;
    for (auto& x : state.x)
        x = random();
    for (auto& v : state.v)
        v = {random(), random()};
    state.nzcv = uint32_t(random()) & 0xf0000000;
    state.fpcr = uint32_t(random()) & 0x07c00000;
    if (trial % 4 != 0)
        return state;

    constexpr uint64_t Integers[] = {
        0, 1, UINT64_MAX, 0x8000000000000000, 0x7fffffffffffffff, 0x7fffffff, 0x80000000, 0xffffffff,
    };
    for (auto& x : state.x)
        x = Integers[random() % std::size(Integers)];
    constexpr double Floats[] = {
        -0.0,
        0.0,
        2.2,
        -4.3,
        1.5,
        1e-310,
        1e-40,
        1e50,
        std::numeric_limits<double>::infinity(),
        std::numeric_limits<double>::quiet_NaN(),
    };
    for (auto& v : state.v) {
        const auto value = Floats[random() % std::size(Floats)];
        const auto bits = trial % 8 == 0 ? std::bit_cast<uint64_t>(value)
                                         : uint64_t(std::bit_cast<uint32_t>(float(value))) * 0x100000001;
        v = {bits, bits};
    }
    return state;
}

bool LastStateMatches(const TestCase& test, unsigned trial, const State& got, const State& expected) {
    for (unsigned i = 0; i < got.x.size(); ++i) {
        if (got.x[i] == expected.x[i])
            continue;
        std::fprintf(stderr, "%08" PRIx32 " %s trial %u x%u expected %016" PRIx64 " got %016" PRIx64 "\n", test.word,
                     test.mnemonic, trial, i, expected.x[i], got.x[i]);
        return false;
    }
    for (unsigned i = 0; i < got.v.size(); ++i) {
        if (got.v[i].lo == expected.v[i].lo && got.v[i].hi == expected.v[i].hi)
            continue;
        std::fprintf(stderr,
                     "%08" PRIx32 " %s trial %u v%u expected %016" PRIx64 ":%016" PRIx64 " got %016" PRIx64
                     ":%016" PRIx64 "\n",
                     test.word, test.mnemonic, trial, i, expected.v[i].hi, expected.v[i].lo, got.v[i].hi, got.v[i].lo);
        return false;
    }
    if (got.sp != expected.sp) {
        std::fprintf(stderr, "%08" PRIx32 " %s trial %u SP expected %016" PRIx64 " got %016" PRIx64 "\n", test.word,
                     test.mnemonic, trial, expected.sp, got.sp);
        return false;
    }
    if (got.nzcv == expected.nzcv && got.fpsr == expected.fpsr)
        return true;
    std::fprintf(stderr,
                 "%08" PRIx32 " %s trial %u NZCV/FPSR expected %08" PRIx32 "/%08" PRIx32 " got %08" PRIx32 "/%08" PRIx32
                 " FPCR %08" PRIx32 "\n",
                 test.word, test.mnemonic, trial, expected.nzcv, expected.fpsr, got.nzcv, got.fpsr, got.fpcr);
    return false;
}

bool RelocatedBlockOperands() {
    constexpr uint32_t adr = 0x10000060;     // adr x0, .+12
    constexpr uint32_t adrp = 0x90000001;    // adrp x1, current page
    constexpr uint32_t literal = 0x180000c2; // ldr w2, .+24
    constexpr uint32_t call = 0x94000003;    // bl .+12
    struct Mapping {
        CodePage page{};
        uint64_t base = 0;
    } mapping;
    Memory memory{&mapping};
    memory.executable_page = [](void* opaque, uint64_t pc) -> const uint8_t* {
        const auto& m = *static_cast<Mapping*>(opaque);
        return pc == m.base ? m.page.data() : nullptr;
    };
    memory.read = [](void* opaque, uint64_t address, void* bytes, size_t size, Access) {
        const auto& m = *static_cast<Mapping*>(opaque);
        const auto offset = address - m.base;
        if (size > m.page.size() || offset > m.page.size() - size)
            return false;
        std::memcpy(bytes, m.page.data() + offset, size);
        return true;
    };
    Profile baked{.trusted_baking = true};
    baked.resolve = [](const DecodedInstruction& op, bool dead) -> detail::HandlerBinding {
        switch (op.word) {
        case adr:
            return BakedBinding<adr>(dead);
        case adrp:
            return BakedBinding<adrp>(dead);
        case literal:
            return BakedBinding<literal>(dead);
        case call:
            return BakedBinding<call>(dead);
        default:
            return {};
        }
    };
    baked.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched, uint32_t dead,
                                detail::ThreadHandler& threaded) -> Handler {
        if (count < 3 || words[0] != adr || words[1] != adrp || words[2] != literal)
            return nullptr;
        *matched = 3;
        return BindBakedRun<adr, adrp, literal>(dead, threaded);
    };
    constexpr std::array program{adr,         adrp,        literal,     call,       0xd4200000u,
                                 0xd4200000u, 0xaa1e03e3u, 0xd4000001u, 0xe1234567u};
    for (uint64_t pc : {0x20040ULL, 0x7ffe00001084ULL, 0x20040ULL}) {
        mapping.base = pc & ~uint64_t{4095};
        const auto offset = pc - mapping.base;
        for (unsigned i = 0; i < program.size(); ++i)
            detail::WriteLittle(mapping.page.data() + offset + i * 4, program[i]);
        for (const auto& profile : {Profile{}, baked}) {
            State state{};
            state.pc = pc;
            const auto result = Run(state, memory, 10, profile);
            if (result.reason != Stop::Syscall || result.executed != 6 || state.pc != pc + 32 ||
                state.x[0] != pc + 12 || state.x[1] != mapping.base || state.x[2] != program.back() ||
                state.x[30] != pc + 16 || state.x[3] != pc + 16)
                return false;
        }
    }
    return true;
}

} // namespace

int main() {
    if (!RelocatedBlockOperands()) {
        std::fprintf(stderr, "PC-relative operands or LR changed after relocating the block\n");
        return 1;
    }
    std::mt19937_64 random(Seed);
    const Memory memory;
    CodePage executable_page{};
    detail::WriteLittle(executable_page.data() + 4, 0xd503201fu); // NOP
    Memory mapped{&executable_page};
    mapped.executable_page = [](void* context, uint64_t address) -> const uint8_t* {
        return address == CodeAddress ? static_cast<const CodePage*>(context)->data() : nullptr;
    };
    Profile profile{.trusted_baking = true};
    profile.resolve = [](const DecodedInstruction& op, bool omit_nzcv) -> detail::HandlerBinding {
        const auto* test = FindCase(op.word);
        if (!test || omit_nzcv)
            return {};
        return {test->baked, test->threaded};
    };
    unsigned failing = 0;
    unsigned baked_cases = 0;
    unsigned dead_nzcv_cases = 0;
    for (const auto& test : Cases) {
        const auto op = Decode(test.word);
        const auto dead_nzcv = detail::LowerDeadNZCV(op);
        if ((dead_nzcv.scalar != nullptr) != test.supports_dead_nzcv ||
            (FIBERCPU_AARCH64_TAIL_DISPATCH && test.supports_dead_nzcv && !dead_nzcv.threaded)) {
            std::fprintf(stderr, "%08" PRIx32 " %s has the wrong dead-NZCV binding\n", test.word, test.mnemonic);
            ++failing;
            continue;
        }
        baked_cases += test.baked != nullptr;
        dead_nzcv_cases += dead_nzcv.scalar != nullptr;
        detail::WriteLittle(executable_page.data(), test.word);
        for (unsigned trial = 0; trial < TrialsPerCase; ++trial) {
            auto expected = MakeState(random, trial);
            auto got = expected;
            test.reference(expected);
            got.sp = expected.sp; // The corpus reads SP without modifying it.
            auto baked = got;
            auto block = got;
            const auto initial = got;
            const auto step = op.handler(got, memory, op);
            if (step.reason != Stop::Continue) {
                std::fprintf(stderr, "%08" PRIx32 " %s trial %u stopped with %u\n", test.word, test.mnemonic, trial,
                             unsigned(step.reason));
                ++failing;
                break;
            }
            if (!LastStateMatches(test, trial, got, expected)) {
                ++failing;
                break;
            }
            if (test.baked && (test.baked(baked, memory, op).reason != Stop::Continue ||
                               !LastStateMatches(test, trial, baked, expected))) {
                std::fprintf(stderr, "%08" PRIx32 " %s trial %u failed in the baked handler\n", test.word,
                             test.mnemonic, trial);
                ++failing;
                break;
            }
            block.pc = CodeAddress;
            const auto outcome = Run(block, mapped, 2, profile);
            if (outcome.reason != Stop::Budget || outcome.executed != 2 || block.pc != CodeAddress + 8 ||
                !LastStateMatches(test, trial, block, expected)) {
                std::fprintf(stderr, "%08" PRIx32 " %s trial %u failed through block dispatch\n", test.word,
                             test.mnemonic, trial);
                ++failing;
                break;
            }
            if (dead_nzcv.scalar) {
                auto elided = initial;
                auto elided_expected = expected;
                elided_expected.nzcv = initial.nzcv;
                if (dead_nzcv.scalar(elided, memory, op).reason != Stop::Continue ||
                    !LastStateMatches(test, trial, elided, elided_expected)) {
                    std::fprintf(stderr, "%08" PRIx32 " %s trial %u failed with dead NZCV output\n", test.word,
                                 test.mnemonic, trial);
                    ++failing;
                    break;
                }
                if (dead_nzcv.threaded) {
                    Profile dead_profile{.trusted_baking = true};
                    dead_profile.resolve = [](const DecodedInstruction& instruction, bool) {
                        return detail::LowerDeadNZCV(instruction);
                    };
                    auto threaded = initial;
                    threaded.pc = CodeAddress;
                    const auto result = Run(threaded, mapped, 2, dead_profile);
                    if (result.reason != Stop::Budget || result.executed != 2 || threaded.pc != CodeAddress + 8 ||
                        !LastStateMatches(test, trial, threaded, elided_expected)) {
                        std::fprintf(stderr, "%08" PRIx32 " %s trial %u failed through dead-NZCV dispatch\n", test.word,
                                     test.mnemonic, trial);
                        ++failing;
                        break;
                    }
                }
            }
        }
    }
    for (const auto& test : InvalidCases) {
        const auto op = Decode(test.word);
        State state;
        if (op.handler(state, memory, op).reason == Stop::Undefined && NativeTraps(test))
            continue;
        std::fprintf(stderr, "Undefined instruction mismatch at %08" PRIx32 "\n", test.word);
        ++failing;
    }
    if (failing)
        return 1;
    std::printf("%zu AArch64 instructions matched native hardware directly and through block dispatch\n"
                "using %u states per encoding (seed %u).\n"
                "%u baked instructions matched the same reference. %zu invalid encodings trapped in both.\n"
                "%u dead-NZCV handlers preserved the input flags and matched all other native outputs.\n",
                std::size(Cases), TrialsPerCase, Seed, baked_cases, std::size(InvalidCases), dead_nzcv_cases);
    return 0;
}
