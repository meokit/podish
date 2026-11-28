#include "aarch64/aarch64.h"
#include "aarch64/baked.h"
#include "aarch64/lowering.h"

#include <algorithm>
#include <array>
#include <bit>
#include <cstdio>
#include <cstring>
#include <initializer_list>

using namespace fiberish::aarch64;

namespace {

using fiberish::mem::ArmPageTag;
using fiberish::mem::ArmTaskSize;

constexpr uint64_t Base = 0x1'0040'0000;
constexpr size_t Page = 4096;

struct Backing {
    std::array<uint8_t, 2 * Page> data{};
    std::array<bool, 2> writable{true, true};
    std::array<bool, 2> executable{true, false};
    uint64_t replace_after_read = 0;
    const State* observe_on_read = nullptr;
    uint64_t observed_pc = 0;
    uint32_t observed_nzcv = 0;
    unsigned observed_reads = 0;
    unsigned writes = 0;
    unsigned executes = 0;
    unsigned sequences = 0;

    void Put(uint64_t address, uint32_t word) {
        for (unsigned i = 0; i < 4; ++i)
            data[address - Base + i] = uint8_t(word >> (i * 8));
    }
    void Code(uint64_t address, std::initializer_list<uint32_t> words) {
        for (auto word : words) {
            Put(address, word);
            address += 4;
        }
    }
    static bool InBounds(uint64_t address, size_t size) {
        return address >= Base && address - Base < 2 * Page && size <= 2 * Page - (address - Base);
    }
    Memory Callbacks(bool span = true) {
        Memory memory{this};
        memory.read = [](void* context, uint64_t address, void* dest, size_t size, Access access) {
            auto& backing = *static_cast<Backing*>(context);
            if (!InBounds(address, size))
                return false;
            const auto offset = address - Base;
            if (access == Access::Execute) {
                ++backing.executes;
                if (!backing.executable[offset / Page])
                    return false;
            } else {
                if (backing.observe_on_read) {
                    backing.observed_pc = backing.observe_on_read->pc;
                    backing.observed_nzcv = backing.observe_on_read->nzcv;
                    ++backing.observed_reads;
                }
                if (backing.replace_after_read) {
                    backing.Put(backing.replace_after_read, 0x91000c21); // add x1, x1, #3
                    backing.replace_after_read = 0;
                }
            }
            std::memcpy(dest, backing.data.data() + offset, size);
            return true;
        };
        memory.write = [](void* context, uint64_t address, const void* source, size_t size) {
            auto& backing = *static_cast<Backing*>(context);
            if (!InBounds(address, size))
                return false;
            const auto offset = address - Base;
            if (!backing.writable[offset / Page] || !backing.writable[(offset + size - 1) / Page])
                return false;
            ++backing.writes;
            std::memcpy(backing.data.data() + offset, source, size);
            return true;
        };
        if (span) {
            memory.executable_page = [](void* context, uint64_t address) -> const uint8_t* {
                const auto& backing = *static_cast<Backing*>(context);
                if (!InBounds(address, Page) || !backing.executable[(address - Base) / Page])
                    return nullptr;
                return backing.data.data() + address - Base;
            };
        }
        return memory;
    }
};

bool CoreAndTraps(bool use_span) {
    Backing backing;
    backing.Code(Base, {
                           0xd2e24680, // movz x0, #0x1234, lsl #48
                           0xf2b579a0, // movk x0, #0xabcd, lsl #16
                           0x8b21cbe3, // add x3, sp, w1, sxtw #2
                           0x31000444, // adds w4, w2, #1
                           0xba1f0005, // adcs x5, x0, xzr
                           0xda9f10a6, // csinv x6, x5, xzr, ne
                           0x9a8630a7, // csel x7, x5, x6, lo
                           0xd51bd047, // msr tpidr_el0, x7
                           0xd53bd048, // mrs x8, tpidr_el0
                           0xb6f80048, // tbz x8, #63, +8
                           0xd2800129, // mov x9, #9
                           0xd4000f61, // svc #123
                           0xd4200540, // brk #42
                           0xffffffff, // unallocated
                       });
    State state;
    state.pc = Base;
    state.sp = Base + 512;
    state.x[1] = uint64_t(-2);
    state.x[2] = UINT32_MAX;
    constexpr auto Expected = 0x1234'0000'abcd'0000;
    const auto memory = backing.Callbacks(use_span);
    const auto result = Run(state, memory, 40);
    if (result.reason != Stop::Syscall || result.executed != 11 || result.immediate != 123 ||
        result.address != Base + 44 || state.pc != Base + 48 || state.x[0] != Expected || state.x[3] != state.sp - 8 ||
        state.x[4] || state.x[5] != Expected + 1 || state.x[6] != Expected + 1 || state.x[8] != Expected + 1 ||
        state.tpidr_el0 != Expected + 1 || state.x[9] || state.nzcv)
        return false;
    const auto breakpoint = Run(state, memory, 1);
    if (breakpoint.reason != Stop::Breakpoint || breakpoint.executed || breakpoint.immediate != 42 ||
        state.pc != Base + 48)
        return false;
    state.pc += 4;
    return Run(state, memory, 1).reason == Stop::Undefined && state.pc == Base + 52;
}

bool MemoryFaults() {
    Backing backing;
    backing.Code(Base, {
                           0xa9bf0460, // stp x0, x1, [x3, #-16]!
                           0xa8c10460, // ldp x0, x1, [x3], #16
                           0x79c00060, // ldrsh w0, [x3]
                           0x79800061, // ldrsh x1, [x3]
                       });
    const auto memory = backing.Callbacks();
    State state;
    state.pc = Base;
    state.x[0] = 0x0123456789abcdef;
    state.x[1] = 0xfedcba9876543210;
    state.x[3] = Base + Page + 8;
    const auto first_address = state.x[3];
    backing.writable[1] = false;
    const auto store = Run(state, memory, 2);
    if (store.reason != Stop::Fault || store.address != first_address - 16 || store.executed ||
        store.access != Access::Write || state.pc != Base || state.x[3] != first_address || backing.writes ||
        backing.data[Page - 1])
        return false;

    backing.writable[1] = true;
    if (Run(state, memory, 1).reason != Stop::Budget || state.x[3] != first_address - 16)
        return false;
    state.x[0] = state.x[1] = 0;
    if (Run(state, memory, 1).reason != Stop::Budget || state.x[3] != first_address ||
        state.x[0] != 0x0123456789abcdef || state.x[1] != 0xfedcba9876543210)
        return false;
    backing.Put(first_address, 0x8001);
    if (Run(state, memory, 2).reason != Stop::Budget || state.x[0] != 0xffff8001 || state.x[1] != 0xffff'ffff'ffff'8001)
        return false;

    state.pc = Base + Page - 4;
    backing.Put(state.pc, 0x91000421); // add x1, x1, #1
    const auto fetch = Run(state, memory, 2);
    if (fetch.reason != Stop::Fault || fetch.executed != 1 || fetch.access != Access::Execute ||
        state.pc != Base + Page || fetch.address != state.pc || backing.executes != 1)
        return false;
    ++state.pc;
    return Run(state, memory, 1).reason == Stop::Alignment;
}

bool PairAddressingModes(bool tlb_hit) {
    using fiberish::mem::Property;
    constexpr auto Data = Base + Page + 32;
    constexpr auto First = 0x0123456789abcdefull;
    constexpr auto Second = 0xfedcba9876543210ull;
    constexpr uint32_t Svc = 0xd4000001;
    constexpr struct {
        uint32_t store;
        uint32_t load;
        int offset;
        int writeback;
    } cases[] = {
        {0xa9010460, 0xa9411464, 16, 0},    // stp / ldp [x3, #16]
        {0xa8810460, 0xa8c11464, 0, 16},    // stp / ldp [x3], #16
        {0xa9bf0460, 0xa9ff1464, -16, -16}, // stp / ldp [x3, #-16]!
        {0xa8010460, 0xa8411464, 16, 0},    // stnp / ldnp [x3, #16]
    };
    Backing backing;
    auto memory = backing.Callbacks();
    fiberish::mem::SoftTlb tlb;
    if (tlb_hit) {
        tlb.fill_arm(Base + Page, reinterpret_cast<std::byte*>(backing.data.data() + Page),
                     Property::Read | Property::Write | Property::Dirty);
        memory.data_tlb = &tlb;
        memory.read = nullptr;
        memory.write = nullptr;
    }
    for (const auto& test : cases) {
        std::fill(backing.data.begin() + Page, backing.data.end(), 0);
        backing.Code(Base, {test.store, Svc});
        State state{};
        state.pc = Base;
        state.x[0] = First;
        state.x[1] = Second;
        state.x[3] = Data;
        state.exclusive_address = Data + test.offset;
        state.exclusive_size = 8;
        const auto* target = backing.data.data() + (Data + test.offset - Base);
        if (Run(state, memory, 2).reason != Stop::Syscall || state.x[3] != Data + test.writeback ||
            state.exclusive_size || detail::ReadLittle<uint64_t>(target) != First ||
            detail::ReadLittle<uint64_t>(target + 8) != Second)
            return false;

        backing.Code(Base, {test.load, Svc});
        state.pc = Base;
        state.x[3] = Data;
        if (Run(state, memory, 2).reason != Stop::Syscall || state.x[3] != Data + test.writeback ||
            state.x[4] != First || state.x[5] != Second)
            return false;
    }
    // An overlapping writeback must stop before any transfer.
    backing.Code(Base, {0xa9c11463, Svc}); // ldp x3, x5, [x3, #16]!
    State state{};
    state.pc = Base;
    state.x[3] = Data;
    const auto overlap = Run(state, memory, 2);
    return overlap.reason == Stop::Undefined && overlap.executed == 0 && state.pc == Base && state.x[3] == Data;
}

bool MemoryOverlapAtDecode() {
    constexpr std::array invalid{
        0x38401463u, // ldrb w3, [x3], #1
        0xf8008c63u, // str x3, [x3, #8]!
        0xa9401064u, // ldp x4, x4, [x3]
        0xa8c10c64u, // ldp x4, x3, [x3], #16
    };
    Backing backing;
    for (const auto word : invalid) {
        if (Decode(word).kind != Kind::Undefined)
            return false;
        backing.Code(Base, {word, 0xd4000001});
        for (const bool block : {false, true}) {
            State state{};
            state.pc = Base;
            state.x[3] = Base + Page;
            state.x[4] = 42;
            backing.observe_on_read = &state;
            const auto result = Run(state, backing.Callbacks(block), 2);
            if (result.reason != Stop::Undefined || result.executed != 0 || result.instruction != word ||
                result.address != Base || state.pc != Base || state.x[3] != Base + Page || state.x[4] != 42 ||
                backing.observed_reads != 0 || backing.writes != 0)
                return false;
        }
    }
    // SP and ZR have distinct roles even though they share the same encoding.
    return Decode(0x384017ff).kind == Kind::LoadStoreImmediate;
}

bool ExclusivesAndAtomics() {
    Backing backing;
    backing.Code(Base, {
                           0xc85ffc60, // ldaxr x0, [x3]
                           0xc801fc62, // stlxr w1, x2, [x3]
                           0xc85ffc60, // ldaxr x0, [x3]
                           0xd5033f5f, // clrex
                           0xc801fc62, // stlxr w1, x2, [x3]
                           0xf8e10062, // ldaddal x1, x2, [x3]
                       });
    State state;
    state.pc = Base;
    state.x[3] = Base + Page;
    state.x[2] = 17;
    backing.Put(state.x[3], 9);
    const auto result = Run(state, backing.Callbacks(), 6);
    return result.reason == Stop::Budget && result.executed == 6 && state.x[0] == 17 && state.x[1] == 1 &&
           state.x[2] == 17 && backing.data[Page] == 18 && backing.writes == 2 && !state.exclusive_size;
}

bool RegisterOpsKeepDispatchState() {
    Backing backing;
    backing.Code(Base, {
                           0x9e670120, // fmov d0, x9
                           0x9e660029, // fmov x9, d1
                           0xd51bd049, // msr tpidr_el0, x9
                           0xd51b4202, // msr nzcv, x2
                           0x1e614c02, // fcsel d2, d0, d1, mi
                           0x1e612040, // fcmp d2, d1
                           0xd53b4209, // mrs x9, nzcv
                           0x9100412b, // add x11, x9, #16
                           0x4e080d24, // dup v4.2d, x9
                           0x4e183c69, // umov x9, v3.d[1]
                           0x91000d2c, // add x12, x9, #3
                           0xd4000841, // svc #0x42
                       });
    State state;
    state.pc = Base;
    state.x[9] = std::bit_cast<uint64_t>(1.0);
    state.x[2] = detail::Negative;
    state.v[1].lo = std::bit_cast<uint64_t>(2.0);
    state.v[3].hi = 55;
    const auto result = Run(state, backing.Callbacks(), 12);
    return result.reason == Stop::Syscall && result.executed == 12 && result.immediate == 0x42 &&
           state.pc == Base + 48 && state.tpidr_el0 == state.v[1].lo && state.v[2].lo == state.v[0].lo &&
           state.v[2].lo == std::bit_cast<uint64_t>(1.0) && state.nzcv == detail::Negative &&
           state.x[11] == uint64_t(detail::Negative) + 16 && state.v[4].lo == detail::Negative &&
           state.v[4].hi == detail::Negative && state.x[9] == 55 && state.x[12] == 58;
}

bool DirectTlbTransfers() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    constexpr uint32_t Data = 0x00410000;
    constexpr unsigned TlbIndex = (Data >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK;
    Backing backing;
    backing.Code(Base, {
                           0xc85ffc60, // ldaxr x0, [x3]
                           0xc801fc62, // stlxr w1, x2, [x3]
                           0xf8e50066, // ldaddal x5, x6, [x3]
                           0x4c407060, // ld1 {v0.16b}, [x3]
                           0x91004063, // add x3, x3, #16
                           0x4c9f7060, // st1 {v0.16b}, [x3], #16
                           0xd50b7427, // dc zva, x7
                           0xd4000001, // svc #0
                       });
    std::array<uint8_t, Page> data{9};
    std::fill_n(data.data() + 64, 32, 0xff);
    fiberish::mem::SoftTlb tlb;
    tlb.read_tlb[TlbIndex] = tlb.write_tlb[TlbIndex] = {
        fiberish::mem::PackMicroTlbTags(ArmPageTag(Data), ArmPageTag(Data)),
        reinterpret_cast<uintptr_t>(data.data()) - Data,
    };
    auto memory = backing.Callbacks();
    // A scalar trampoline or an unexpected replay would fail without these callbacks.
    memory.read = nullptr;
    memory.write = nullptr;
    memory.data_tlb = &tlb;
    State state;
    state.pc = Base;
    state.x[2] = 17;
    state.x[3] = Data;
    state.x[5] = 3;
    state.x[7] = Data + 64;
    const auto result = Run(state, memory, 8);
    if (result.reason != Stop::Syscall || result.executed != 8 || state.x[0] != 9 || state.x[1] != 0 ||
        state.x[3] != Data + 32 || state.x[6] != 17 || state.exclusive_size ||
        detail::ReadLittle<uint64_t>(data.data()) != 20 || detail::ReadLittle<uint64_t>(data.data() + 16) != 20 ||
        !std::all_of(data.begin() + 64, data.begin() + 96, [](uint8_t byte) { return byte == 0; }))
        return false;

    backing.Code(Base, {0xc8e5fc62, 0xd4000001}); // casal x5, x2, [x3]; svc #0
    for (const auto expected : {19u, 20u}) {
        state = {};
        state.pc = Base;
        state.x[2] = 31;
        state.x[3] = Data;
        state.x[5] = expected;
        const auto swap = Run(state, memory, 2);
        if (swap.reason != Stop::Syscall || swap.executed != 2 || state.x[5] != 20 ||
            detail::ReadLittle<uint64_t>(data.data()) != (expected == 20 ? 31 : 20))
            return false;
    }

    // A read hit followed by a write miss must leave the exclusive monitor
    // intact until the replay can observe it and complete the store.
    backing.Code(Base, {0xc801fc62});
    backing.Put(Base + Page, 31);
    memory = backing.Callbacks();
    memory.data_tlb = &tlb;
    tlb.read_tlb[TlbIndex] = {fiberish::mem::PackMicroTlbTags(ArmPageTag(Data), fiberish::mem::kInvalidMicroTlbTag),
                              reinterpret_cast<uintptr_t>(data.data()) - Data};
    tlb.write_tlb[TlbIndex] = {};
    // Point the original callbacks at their backing while letting the probe use the low address.
    memory.read = [](void* context, uint64_t address, void* dest, size_t size, Access access) {
        auto& backing = *static_cast<Backing*>(context);
        if (access != Access::Read || address != Data || size != 8)
            return false;
        std::memcpy(dest, backing.data.data() + Page, size);
        ++backing.observed_reads;
        return true;
    };
    memory.write = [](void* context, uint64_t address, const void* src, size_t size) {
        auto& backing = *static_cast<Backing*>(context);
        if (address != Data || size != 8)
            return false;
        std::memcpy(backing.data.data() + Page, src, size);
        ++backing.writes;
        return true;
    };
    state = {};
    state.pc = Base;
    state.x[2] = 27;
    state.x[3] = Data;
    state.exclusive_address = Data;
    state.exclusive_size = 8;
    state.exclusive_value.front() = 31;
    const auto replay = Run(state, memory, 1);
    return replay.reason == Stop::Budget && replay.executed == 1 && state.pc == Base + 4 && state.x[1] == 0 &&
           !state.exclusive_size && backing.writes == 1 && backing.observed_reads == 1 &&
           detail::ReadLittle<uint64_t>(backing.data.data() + Page) == 27;
#else
    return true;
#endif
}

bool VectorLaneBoundaries() {
    constexpr uint32_t qc = 1u << 27;
    constexpr Vector128 table{0x1716151413121110, 0x1f1e1d1c1b1a1918};
    constexpr Vector128 dest{0xa7a6a5a4a3a2a1a0, 0xe7e6e5e4e3e2e1e0};
    struct Case {
        uint32_t word;
        Vector128 left;
        Vector128 right;
        uint64_t expected;
        uint32_t flags;
    };
    constexpr Case cases[]{
        // A D-width pair uses the low halves of both sources, in operand order.
        {0x2e22ac20, {0x0807060504030201, 0}, {0x1817161514131211, 0}, 0x1715131107050301, 0},
        // SQADD only raises QC for saturation within the active lanes.
        {0x0e220c20, {1, 0x7f}, {2, 1}, 3, 0},
        {0x0e220c20, {0x7f, 0}, {1, 0}, 0x7f, qc},
        // URSHR by the lane width rounds from the top bit.
        {0x2f082420, {0xff80017f00ff8055, 0}, {}, 0x0101000000010100, 0},
        // SRSHL rounds INT32_MAX without overflowing, and clears an overshifted lane.
        {0x0ea25420, {0x800000007fffffff, 0}, {0xffffffdfffffffff, 0}, 0x40000000, 0},
        // TBX wraps the table registers from v31 to v0, also the destination.
        {0x0e0233e0, {}, {0x1403ff201f100f00, 0}, 0xa413a5a4e7a01f10, 0},
    };
    Backing backing;
    const auto memory = backing.Callbacks();
    for (const auto& test : cases) {
        backing.Code(Base, {test.word, 0xd4200000}); // brk #0
        State state{};
        state.pc = Base;
        state.v[0] = dest;
        state.v[1] = test.left;
        state.v[2] = test.right;
        state.v[31] = table;
        state.fpsr = 2; // DZC stays sticky.
        if (Run(state, memory, 2).reason != Stop::Breakpoint || state.v[0].lo != test.expected || state.v[0].hi ||
            state.fpsr != (2 | test.flags))
            return false;
    }
    return true;
}

bool Va39DataBounds() {
    constexpr uint64_t LastPage = ArmTaskSize - Page;
    constexpr uint64_t LowAlias = LastPage & UINT32_MAX;
    constexpr uint64_t Value = 0x0213'4567'89ab'cdef;
    Backing backing;
    backing.Code(Base, {0xf9400020, 0xd4000001}); // ldr x0, [x1]; svc #0
    auto memory = backing.Callbacks();
    memory.read = [](void* context, uint64_t, void* dest, size_t size, Access access) {
        if (size != sizeof(uint64_t) || access != Access::Read)
            return false;
        ++static_cast<Backing*>(context)->observed_reads;
        detail::WriteLittle(dest, Value);
        return true;
    };
    fiberish::mem::SoftTlb tlb;
    memory.data_tlb = &tlb;
    std::array<uint8_t, Page> cached{};
    detail::WriteLittle(cached.data() + Page - 8, ~Value);
    const auto cache_page = [&](uint64_t address) {
        const auto tag = ArmPageTag(address);
        tlb.read_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = {
            fiberish::mem::PackMicroTlbTags(tag, fiberish::mem::kInvalidMicroTlbTag),
            reinterpret_cast<uintptr_t>(cached.data()) - static_cast<uintptr_t>(address),
        };
    };

    State state;
    state.pc = Base;
    state.x[1] = ArmTaskSize - 8;
    cache_page(LowAlias);
    const auto slow = Run(state, memory, 2);
    if (slow.reason != Stop::Syscall || state.x[0] != Value || backing.observed_reads != 1)
        return false;

    state.pc = Base;
    cache_page(LastPage);
    const auto fast = Run(state, memory, 2);
    if (fast.reason != Stop::Syscall || state.x[0] != ~Value || backing.observed_reads != 1)
        return false;

    for (const auto address : {ArmTaskSize - 4, ArmTaskSize, UINT64_MAX - 3}) {
        state.pc = Base;
        state.x[1] = address;
        const auto fault = Run(state, memory, 2);
        if (fault.reason != Stop::Fault || fault.access != Access::Read || fault.address != address || fault.executed ||
            state.pc != Base || state.x[0] != ~Value || backing.observed_reads != 1)
            return false;
    }

    state.pc = ArmTaskSize;
    const auto fetch = Run(state, memory, 1);
    return fetch.reason == Stop::Fault && fetch.access == Access::Execute && fetch.address == ArmTaskSize &&
           !fetch.executed;
}

bool FloatingAndVector() {
    Backing backing;
    backing.Code(Base, {
                           0x1e6f1000, // fmov d0, #1.5
                           0x1e609001, // fmov d1, #2.5
                           0x1e612802, // fadd d2, d0, d1
                           0x1e780043, // fcvtzs w3, d2
                           0x9e660044, // fmov x4, d2
                       });
    backing.Code(Base + 64, {
                                0x1e612802, // fadd d2, d0, d1
                                0x1e611802, // fdiv d2, d0, d1
                                0x1e612802, // fadd d2, d0, d1
                            });
    backing.Code(Base + 128, {
                                 0x4cdf2080, // ld1 {v0.16b, v1.16b, v2.16b, v3.16b}, [x4], #64
                                 0x4c008080, // st2 {v0.16b, v1.16b}, [x4]
                                 0x4c408084, // ld2 {v4.16b, v5.16b}, [x4]
                             });
    State state;
    state.pc = Base;
    const auto memory = backing.Callbacks();
    if (Run(state, memory, 5).reason != Stop::Budget || state.x[3] != 4 || state.x[4] != std::bit_cast<uint64_t>(4.0) ||
        state.v[2].hi || state.fpsr)
        return false;
    state.pc = Base + 64;
    state.fpcr = 1u << 22; // Round toward +infinity.
    state.v[0].lo = std::bit_cast<uint64_t>(1.0);
    state.v[1].lo = std::bit_cast<uint64_t>(0x1p-53);
    if (Run(state, memory, 1).reason != Stop::Budget || state.v[2].lo != 0x3ff0000000000001 || state.fpsr != (1u << 4))
        return false; // IXC
    state.fpcr = state.fpsr = 0;
    state.v[1].lo = 0;
    if (Run(state, memory, 1).reason != Stop::Budget || state.v[2].lo != 0x7ff0000000000000 || state.fpsr != (1u << 1))
        return false; // DZC
    state.fpsr = 0;
    state.v[0].lo = 0x7ff0000000000042; // Signaling NaN with a payload.
    if (Run(state, memory, 1).reason != Stop::Budget || state.v[2].lo != 0x7ff8000000000042 || state.fpsr != 1)
        return false; // IOC

    state.pc = Base + 128;
    state.x[4] = Base + Page;
    for (unsigned i = 0; i < 64; ++i)
        backing.data[Page + i] = uint8_t(i);
    if (Run(state, memory, 3).reason != Stop::Budget || state.x[4] != Base + Page + 64 ||
        state.v[3].hi != 0x3f3e3d3c3b3a3938 || state.v[4].hi != state.v[0].hi || state.v[4].lo != state.v[0].lo ||
        state.v[5].hi != state.v[1].hi || state.v[5].lo != state.v[1].lo)
        return false;
    for (unsigned i = 0; i < 16; ++i) {
        if (backing.data[Page + 64 + 2 * i] != i || backing.data[Page + 65 + 2 * i] != 16 + i)
            return false;
    }
    return backing.writes == 1;
}

bool CodeCoherence() {
    Backing backing;
    backing.Code(Base, {
                           0x91000421, // add x1, x1, #1
                           0xb9000062, // str w2, [x3]
                           0x91000821, // add x1, x1, #2
                       });
    backing.Code(Base + 64, {
                                0xb9400062, // ldr w2, [x3]
                                0x91000821, // add x1, x1, #2
                            });
    State state;
    state.pc = Base + 8;
    const auto memory = backing.Callbacks();
    if (Run(state, memory, 1).reason != Stop::Budget || state.x[1] != 2)
        return false;
    state.pc = Base;
    state.x[1] = 0;
    state.x[2] = 0x91000c21;
    state.x[3] = Base + 8;
    if (Run(state, memory, 3).reason != Stop::Budget || state.x[1] != 4 || backing.executes)
        return false;
    backing.replace_after_read = Base + 68;
    state.pc = Base + 64;
    state.x[3] = Base + Page;
    return Run(state, memory, 2).reason == Stop::Budget && state.x[1] == 7;
}

bool DispatchAcrossPages() {
    constexpr unsigned Iterations = 200'000;
    constexpr uint32_t Add = 0x91000421; // add x1, x1, #1
    constexpr auto First = Base + Page - 8;
    Backing backing;
    backing.executable[1] = true;
    backing.Code(First, {
                            Add,
                            Add,        // Executable mapping 0.
                            Add,        // Executable mapping 1.
                            0xf1000400, // subs x0, x0, #1
                            0x54ffff81, // b.ne -16
                            0xd4000001, // svc #0
                        });
    State state;
    state.pc = First;
    state.x[0] = Iterations;
    const auto result = Run(state, backing.Callbacks(), Iterations * 5 + 1);
    return result.reason == Stop::Syscall && result.executed == Iterations * 5 + 1 && state.x[0] == 0 &&
           state.x[1] == Iterations * 3 && result.address == First + 20 && state.pc == First + 24 &&
           backing.executes == 0;
}

bool Va39BlockLink() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    constexpr uint64_t Pc = ArmTaskSize - 16;
    constexpr unsigned Count = 3;
    State state;
    detail::BasicBlock block;
    block.header.entry = detail::GetExitHandlerFallthrough();
    block.header.chain = detail::BasicBlockChainPrefix::Pack(Pc, Count, false);
    block.sentinel_slot_index = 5;
    const Memory memory{};
    detail::RunFrame frame{memory};
    state.active_run = &frame;
    const detail::BasicBlock* target = nullptr;
    int64_t budget = 10;
    if (detail::TryChainBlock(state, target, detail::BlockLink(&block), Pc & UINT32_MAX, budget) || budget != 10)
        return false;
    if (detail::TryChainBlock(state, target, detail::BlockLink(&block), Pc, budget) != block.header.entry ||
        target != &block || budget != 10 - Count || state.last_block != &block || block.sentinel_slot_index != 5)
        return false;
#endif
    return true;
}

template <unsigned Counter>
A64_FORCE_INLINE StepResult CountProfiledAddLogic(State& state, const detail::DecodedOp*& current A64_UTLB_REF_PARAMS,
                                                  uint32_t& flags_cache A64_HOT_PIN_REF) {
    constexpr uint32_t Add = 0x91000400; // add x0, x0, #1
    const detail::HotRegisters regs{state A64_HANDLER_PIN_ARGS};
    regs.Write(Counter, 64, regs.Read(Counter, 64) + 1);
    return detail::BakedLogic<0, false, Add>(state, current A64_UTLB_ARGS, flags_cache A64_HANDLER_PIN_ARGS);
}

template <unsigned Counter>
detail::HandlerFunc CountProfiledAdd(const DecodedInstruction& op, bool) {
    constexpr uint32_t Add = 0x91000400; // add x0, x0, #1
    if (op.word != Add)
        return nullptr;
    return &detail::DispatchWrapper<CountProfiledAddLogic<Counter>, detail::BakedPolicy<0, Add>()>;
}

bool ReplacedSuccessorUsesCurrentProfile() {
    constexpr auto Source = Base + 128;
    constexpr auto Target = Source + 8;
    Backing backing;
    backing.Code(Source, {0x14000002, 0xd503201f, 0x91000400}); // b +8; nop; add x0, x0, #1
    const auto memory = backing.Callbacks();
    const Profile first{.resolve = CountProfiledAdd<1>};
    const Profile second{.resolve = CountProfiledAdd<2>};
    const Profile third{.resolve = CountProfiledAdd<3>};
    State state;
    state.pc = Source;
    if (Run(state, memory, 2, first).reason != Stop::Budget)
        return false;
    // The two other resolvers occupy both ways of the successor's cache set.
    // The guest PC and bytes stay identical throughout.
    state.pc = Target;
    if (Run(state, memory, 1, second).reason != Stop::Budget)
        return false;
    state.pc = Target;
    if (Run(state, memory, 1, third).reason != Stop::Budget)
        return false;
    state.pc = Source;
    return Run(state, memory, 2, first).reason == Stop::Budget && state.x[0] == 4 && state.x[1] == 2 &&
           state.x[2] == 1 && state.x[3] == 1;
}

bool TruncatedBlocks() {
    constexpr unsigned Words = detail::BlockMax;
    constexpr uint32_t Add = 0x91000421; // add x1, x1, #1
    Profile profile;
    profile.resolve_pair = [](uint32_t first, uint32_t second, uint32_t dead) -> detail::HandlerFunc {
        if (first != Add || second != Add)
            return nullptr;
        return BindBakedFusion<Add, Add>(dead);
    };
    profile.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                  uint32_t dead) -> detail::HandlerFunc {
        if (count < 4 || !std::all_of(words, words + 4, [](uint32_t word) { return word == Add; }))
            return nullptr;
        *matched = 4;
        return BindBakedFusion<Add, Add, Add, Add>(dead);
    };
    for (const auto start : {Base, Base + Page - Words * 4}) {
        Backing backing;
        for (unsigned i = 0; i < Words; ++i)
            backing.Put(start + 4 * i, Add);
        const auto memory = backing.Callbacks();
        for (unsigned budget = 0; budget <= Words + 1; ++budget) {
            State state;
            state.pc = start;
            const auto result = Run(state, memory, budget, profile);
            const auto retired = std::min(budget, Words);
            const auto reason = budget <= Words ? Stop::Budget : start == Base ? Stop::Undefined : Stop::Fault;
            if (result.reason != reason || result.executed != retired || state.x[1] != retired ||
                state.pc != start + 4 * retired)
                return false;
        }
    }
    return true;
}

bool StandaloneConditionBranch() {
    Backing backing;
    const auto memory = backing.Callbacks();
    for (uint32_t condition = 0; condition < 16; ++condition) {
        backing.Code(Base, {0x54000040 | condition, 0xd4000021, 0xd4000041}); // b.cond +8; svc #1; svc #2
        for (uint32_t flags = 0; flags < 16; ++flags) {
            const uint32_t nzcv = flags << 28;
            const bool taken = detail::ConditionHolds(nzcv, condition);
            State state{};
            state.pc = Base;
            state.nzcv = nzcv;
            const auto result = Run(state, memory, 3);
            if (result.reason != Stop::Syscall || result.executed != 2 || result.immediate != (taken ? 2 : 1) ||
                state.pc != Base + (taken ? 12 : 8) || state.nzcv != nzcv)
                return false;
        }
    }
    return true;
}

bool CompareBranchPair() {
    // Different encodings exercise both register-31 meanings and the W/X widths.
    constexpr std::array<uint32_t, 4> compares{0xf1000fff, 0x71000c1f, 0xeb01001f, 0x6b0103ff};
    constexpr std::array operands{uint64_t{0}, uint64_t{3}, uint64_t{1} << 31, uint64_t{1} << 63, UINT64_MAX};
    for (auto compare : compares) {
        for (uint32_t cond = 0; cond < 16; ++cond) {
            Backing backing;
            backing.Code(Base, {compare, 0x54000040 | cond, 0x91000442, 0x91000842}); // b.cond +8
            for (auto left : operands) {
                for (auto right : operands) {
                    for (unsigned budget = 1; budget <= 4; ++budget) {
                        State expected;
                        expected.pc = Base;
                        expected.sp = left;
                        expected.x[0] = left;
                        expected.x[1] = right;
                        expected.nzcv = 0xf0000000;
                        State actual = expected;
                        const auto scalar = Run(expected, backing.Callbacks(false), budget);
                        const auto blocked = Run(actual, backing.Callbacks(), budget);
                        if (blocked.reason != scalar.reason || blocked.executed != scalar.executed ||
                            actual.pc != expected.pc || actual.nzcv != expected.nzcv || actual.x != expected.x ||
                            actual.sp != expected.sp)
                            return false;
                    }
                }
            }
        }
    }
    return true;
}

bool CanonicalBakedWords() {
    const auto preserves_fields = [](uint32_t word) {
        const auto raw = detail::DecodeFields(word);
        const auto key = detail::CanonicalizeBakedWord(raw);
        const auto baked = detail::DecodeFields(key);
        return raw.kind == baked.kind && raw.width == baked.width && raw.operation == baked.operation &&
               raw.mode == baked.mode && raw.shift == baked.shift && raw.rd == baked.rd && raw.rn == baked.rn &&
               raw.rm == baked.rm && raw.ra == baked.ra && raw.mask == baked.mask &&
               detail::CanonicalizeBakedWord(baked) == key &&
               (detail::HasBlockTarget(raw.kind) ? baked.immediate == 0 : key == word);
    };
    // Vary every prefix and low register/condition combination, including the fixed
    // bit in B.cond and the reserved SIMD literal size.
    for (uint32_t prefix = 0; prefix < 256; ++prefix) {
        for (uint32_t low = 0; low < 32; ++low) {
            for (const uint32_t offset : {0u, 0x00555540u, 0x00aaaaa0u, 0x00ffffe0u}) {
                if (!preserves_fields((prefix << 24) | offset | low))
                    return false;
            }
        }
    }
    constexpr std::pair<uint32_t, uint32_t> examples[]{
        {0x17ffffff, 0x14000000}, // B
        {0x97ffffff, 0x94000000}, // BL
        {0x54ffffed, 0x5400000d}, // B.LE
        {0x54fffffd, 0x54fffffd}, // B.cond fixed bit 4 is set
        {0xb5ffffe9, 0xb5000009}, // CBNZ X9
        {0xb7ffffe9, 0xb7f80009}, // TBNZ X9, #63
        {0x70ffffea, 0x1000000a}, // ADR X10
        {0xf0ffffea, 0x9000000a}, // ADRP X10
        {0x9cffffeb, 0x9c00000b}, // LDR Q11, literal
        {0xd8ffffec, 0xd800000c}, // PRFM, literal
        {0xdcffffeb, 0xdcffffeb}, // Reserved SIMD literal
    };
    for (const auto [word, key] : examples) {
        if (detail::CanonicalizeBakedWord(word) != key || !preserves_fields(word))
            return false;
    }
    return true;
}

bool BakedCompareBranchSuffix() {
    constexpr uint32_t Prefix = 0x91000483;  // add x3, x4, #1
    constexpr uint32_t Compare = 0xeb01001f; // cmp x0, x1
    constexpr uint32_t Less = 0x5400004b;    // b.lt +8
    constexpr auto LessKey = detail::CanonicalizeBakedWord(Less);
    Backing backing;
    backing.Code(Base, {Prefix, Compare, Less, 0x91000463, 0xf94000a6}); // add x3, x3, #1; ldr x6, [x5]
    const Profile profile{
        .resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* length,
                               uint32_t dead) -> detail::HandlerFunc {
            if (count < 3 || words[0] != Prefix || words[1] != Compare || words[2] != LessKey)
                return nullptr;
            *length = 3;
            return BindBakedFusion<Prefix, Compare, LessKey>(dead);
        },
    };

    for (auto left : {uint64_t{1}, uint64_t{1} << 63}) {
        for (unsigned budget = 2; budget <= 5; ++budget) {
            State expected;
            expected.pc = Base;
            expected.x[0] = left;
            expected.x[1] = 1;
            expected.x[4] = 7;
            expected.x[5] = Base + 2 * Page; // The next data access faults.
            State actual = expected;
            const auto scalar = Run(expected, backing.Callbacks(false), budget);
            const auto baked = Run(actual, backing.Callbacks(), budget, profile);
            if (baked.reason != scalar.reason || baked.executed != scalar.executed || baked.address != scalar.address ||
                actual.pc != expected.pc || actual.nzcv != expected.nzcv || actual.x != expected.x)
                return false;
        }
    }
    return true;
}

bool BakedDispatch() {
    constexpr uint32_t Add1 = 0x91000400;
    constexpr uint32_t Add2 = 0x91000800;
    static_assert(CanBake(Add1));
    static_assert(!CanBake(0xd4000001));
    static_assert(GetBakedHandler<0xd4000001>() == nullptr);
    Backing backing;
    backing.Code(Base, {Add1, Add2});
    State state;
    state.pc = Base;
    Profile profile;
    profile.resolve = [](const DecodedInstruction& op, bool omit_nzcv) {
        return op.word == Add1 ? BakedBinding<Add1>(omit_nzcv) : BakedBinding<Add2>(omit_nzcv);
    };
    unsigned records = 0;
    profile.context = &records;
    profile.record = [](void* context, uint64_t, uint32_t, bool) { ++*static_cast<unsigned*>(context); };
    const auto result = Run(state, backing.Callbacks(), 2, profile);
    return result.reason == Stop::Budget && state.x[0] == 3 && records == 2 && backing.executes == 2;
}

bool PartialImmediateOperands() {
    constexpr uint32_t Add = 0x91000000;  // add x0, x0, #imm
    constexpr uint32_t Load = 0xb9400022; // ldr w2, [x1, #imm*4]
    constexpr uint32_t Data = 0x410000;
    Backing backing;
    auto memory = backing.Callbacks();
    std::array<uint32_t, Page / 4> data{};
    fiberish::mem::SoftTlb tlb{};
    tlb.read_tlb[(Data >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(ArmPageTag(Data), fiberish::mem::kInvalidMicroTlbTag),
        reinterpret_cast<uintptr_t>(data.data()) - Data,
    };
    memory.data_tlb = &tlb;
    Profile profile{};
    profile.resolve = [](const DecodedInstruction& op, bool dead) -> detail::HandlerFunc {
        switch (op.word & ~(0xfffu << 10)) {
        case Add:
            return BakedBinding<Add, true>(dead);
        case Load:
            return BakedBinding<Load, true>(dead);
        default:
            return nullptr;
        }
    };
    for (unsigned imm : {3u, 39u, 1023u}) {
        backing.Code(Base, {Add | (imm << 10), Load | (imm << 10), 0x14000001});
        data[imm] = 37 + imm;
        for (const bool mapped : {true, false}) {
            State state{};
            state.pc = Base;
            state.x[1] = mapped ? Data : Data + Page;
            const auto result = Run(state, memory, 3, profile);
            if (state.x[0] != imm || state.x[2] != (mapped ? data[imm] : 0))
                return false;
            if (mapped && (result.reason != Stop::Budget || result.executed != 3 || state.pc != Base + 12))
                return false;
            if (!mapped && (result.reason != Stop::Fault || result.executed != 1 || state.pc != Base + 4 ||
                            result.address != Data + Page + imm * 4))
                return false;
        }
    }
    return true;
}

bool BroaderPartialImmediateOperands() {
    constexpr uint64_t Data = Base + Page;
    constexpr uint64_t Value = 0x1234'0000'abcd'0000;
    Backing backing;
    backing.Code(Base, {
                           0xd2e24680, // movz x0, #0x1234, lsl #48
                           0xf2b579a0, // movk x0, #0xabcd, lsl #16
                           0xb2401c02, // orr x2, x0, #0xff
                           0xa9bf0860, // stp x0, x2, [x3, #-16]!
                           0xf85f8464, // ldr x4, [x3], #-8
                       });
    fiberish::mem::SoftTlb tlb{};
    const auto tag = ArmPageTag(Data);
    tlb.read_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = tlb.write_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(tag, tag),
        reinterpret_cast<uintptr_t>(backing.data.data() + Page) - Data,
    };
    auto memory = backing.Callbacks();
    memory.data_tlb = &tlb;
    Profile profile{};
    profile.resolve = [](const DecodedInstruction& op, bool dead) -> detail::HandlerFunc {
        switch (op.word & ~PartialImmediateMask(op)) {
        case 0xd2e00000:
            return BakedBinding<0xd2e00000, true>(dead);
        case 0xf2a00000:
            return BakedBinding<0xf2a00000, true>(dead);
        case 0xb2000002:
            return BakedBinding<0xb2000002, true>(dead);
        case 0xa9800860:
            return BakedBinding<0xa9800860, true>(dead);
        case 0xf8400464:
            return BakedBinding<0xf8400464, true>(dead);
        default:
            return nullptr;
        }
    };
    State state{};
    state.pc = Base;
    state.x[3] = Data + 128;
    const auto result = Run(state, memory, 5, profile);
    return result.reason == Stop::Budget && result.executed == 5 && state.x[0] == Value &&
           state.x[2] == (Value | 0xff) && state.x[3] == Data + 104 && state.x[4] == Value &&
           detail::ReadLittle<uint64_t>(backing.data.data() + Page + 120) == (Value | 0xff);
}

bool PartialImmediatePairs() {
    constexpr uint32_t Address = 0x90000003; // adrp x3, page
    constexpr uint32_t Load = 0xb9400062;    // ldr w2, [x3, #imm]
    constexpr uint32_t Cbz = 0x34000002;     // cbz w2, target
    constexpr uint32_t Cmp = 0xf100005f;     // cmp x2, #imm
    constexpr uint32_t Equal = 0x54000000;   // b.eq target
    constexpr uint64_t Data = Base + Page;
    static unsigned matched;
    matched = 0;
    const Profile profile{.resolve_partial_pair = [](const DecodedInstruction& first, const DecodedInstruction& second,
                                                     uint32_t dead) -> detail::HandlerFunc {
        const auto a = detail::CanonicalizeBakedWord(first) & ~PartialImmediateMask(first);
        const auto b = detail::CanonicalizeBakedWord(second) & ~PartialImmediateMask(second);
        if (a == Address && b == Load) {
            matched |= 1;
            return PartialBakedFusion<2, Address, Load>.Select(dead, 2);
        }
        if (a == Load && b == Cbz) {
            matched |= 2;
            return PartialBakedFusion<1, Load, Cbz>.Select(dead, 2);
        }
        if (a == Cmp && b == Equal) {
            matched |= 4;
            return PartialBakedFusion<1, Cmp, Equal>.Select(dead, 2);
        }
        return nullptr;
    }};
    Backing backing;
    auto memory = backing.Callbacks();
    fiberish::mem::SoftTlb tlb{};
    const auto tag = ArmPageTag(Data);
    tlb.read_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(tag, fiberish::mem::kInvalidMicroTlbTag),
        reinterpret_cast<uintptr_t>(backing.data.data() + Page) - Data,
    };
    memory.data_tlb = &tlb;
    for (unsigned offset : {6u, 11u}) {
        const uint32_t load = Load | (offset << 10);
        const uint32_t branch = offset << 5;
        backing.Code(Base, {Address | (1u << 29), load});
        backing.Code(Base + 64, {load, Cbz | branch, 0xd4000001});
        backing.Code(Base + 128, {Cmp | (offset << 10), Equal | branch, 0xd4000001});
        backing.Put(Data + offset * 4, offset);

        State state{};
        state.pc = Base;
        if (Run(state, memory, 2, profile).reason != Stop::Budget || state.x[3] != Data || state.x[2] != offset)
            return false;

        state.pc = Base + 64;
        state.x[3] = Data + Page; // Miss on the first member.
        const auto fault = Run(state, memory, 2, profile);
        if (fault.reason != Stop::Fault || fault.executed != 0 || fault.address != Data + Page + offset * 4)
            return false;

        state.pc = Base + 64;
        state.x[3] = Data;
        backing.Put(Data + offset * 4, 0);
        if (Run(state, memory, 2, profile).reason != Stop::Budget || state.pc != Base + 68 + offset * 4)
            return false;

        state.pc = Base + 128;
        state.x[2] = offset;
        if (Run(state, memory, 2, profile).reason != Stop::Budget || state.pc != Base + 132 + offset * 4 ||
            state.nzcv != 0x60000000)
            return false;
    }
    return matched == 7;
}

bool FusedDispatch() {
    constexpr uint32_t Add = 0x91000400;  // add x0, x0, #1
    constexpr uint32_t Load = 0xb9400062; // ldr w2, [x3]
    constexpr uint32_t Branch = 0x14000000;
    static unsigned branch_matches = 0;
    branch_matches = 0;
    Backing backing;
    backing.Code(Base, {Add, Load});
    const auto memory = backing.Callbacks();
    State state;
    state.pc = Base;
    Profile profile;
    profile.resolve_pair = [](uint32_t first, uint32_t second, uint32_t dead) -> detail::HandlerFunc {
        if (first != Add)
            return nullptr;
        if (second == Load)
            return BindBakedFusion<Add, Load>(dead);
        if (second == Branch) {
            ++branch_matches;
            return BindBakedFusion<Add, Branch>(dead);
        }
        return nullptr;
    };
    auto result = Run(state, memory, 2, profile);
    if (result.reason != Stop::Fault || result.executed != 1 || result.instruction != Load || state.pc != Base + 4 ||
        state.x[0] != 1)
        return false;
    state.pc = Base;
    result = Run(state, memory, 1, profile);
    if (result.reason != Stop::Budget || result.executed != 1 || state.pc != Base + 4 || state.x[0] != 2)
        return false;
    for (const auto displacement : {8u, 20u}) {
        state.pc = Base + 64;
        state.x[0] = 0;
        backing.Code(state.pc, {Add, Branch | displacement / 4});
        result = Run(state, memory, 2, profile);
        if (result.reason != Stop::Budget || result.executed != 2 || state.pc != Base + 68 + displacement ||
            state.x[0] != 1)
            return false;
    }
    return branch_matches == 2;
}

bool StackPointerAndZeroRegisterRoles() {
    constexpr uint32_t AddSp = 0x910043ff;     // add sp, sp, #16
    constexpr uint32_t ReadZero = 0xaa1f03e0;  // orr x0, xzr, xzr
    constexpr uint32_t WriteZero = 0xaa00001f; // orr xzr, x0, x0
    constexpr uint32_t ReadSp = 0x910003e1;    // add x1, sp, #0
    constexpr uint32_t Syscall = 0xd4000001;   // svc #0
    Backing backing;
    backing.Code(Base, {AddSp, ReadZero, WriteZero, ReadSp, Syscall});
    State state;
    state.pc = Base;
    state.sp = 0x1000;
    state.x[0] = 99;
    state.x[1] = 99;
    const auto result = Run(state, backing.Callbacks(), 5);
    return result.reason == Stop::Syscall && result.executed == 5 && state.sp == 0x1010 && state.x[0] == 0 &&
           state.x[1] == 0x1010;
}

bool PinnedOperandClassification() {
    using namespace fiberish::aarch64::detail;
    uint32_t pins = 0;
#define A64_TEST_PIN(id, slot) pins |= uint32_t{1} << id;
    FIBERCPU_AARCH64_FOR_EACH_PIN(A64_TEST_PIN)
#undef A64_TEST_PIN
    constexpr auto Gpr = [](unsigned id) { return uint32_t{1} << id; };
    constexpr auto Sp = Gpr(31);
    constexpr auto X0 = Gpr(0);
    constexpr auto X1 = Gpr(1);
    constexpr auto X6 = Gpr(6);
    constexpr auto X7 = Gpr(7);
    constexpr auto X8 = Gpr(8);
    constexpr auto Lr = Gpr(30);
    const struct {
        uint32_t word;
        Kind kind;
        uint32_t operands;
    } cases[] = {
        {0x910007e6, Kind::AddSubImmediate, X6 | Sp},        // add x6, sp, #1
        {0x910004df, Kind::AddSubImmediate, X6 | Sp},        // add sp, x6, #1
        {0xb10004df, Kind::AddSubImmediate, X6},             // adds xzr, x6, #1
        {0xb10007ff, Kind::AddSubImmediate, Sp},             // adds xzr, sp, #1
        {0x8b1f03e6, Kind::AddSubShifted, X6},               // add x6, xzr, xzr
        {0x8b2040df, Kind::AddSubExtended, Sp | X6 | X0},    // add sp, x6, w0, uxtw
        {0xb2401cdf, Kind::LogicalImmediate, X6 | Sp},       // orr sp, x6, #0xff
        {0xf2401cdf, Kind::LogicalImmediate, X6},            // ands xzr, x6, #0xff
        {0xf8410fe6, Kind::LoadStoreImmediate, Sp | X6},     // ldr x6, [sp, #16]!
        {0xf80107e6, Kind::LoadStoreImmediate, Sp | X6},     // str x6, [sp], #16
        {0xf8410c06, Kind::LoadStoreImmediate, X0 | X6},     // ldr x6, [x0, #16]!
        {0xf8010406, Kind::LoadStoreImmediate, X0 | X6},     // str x6, [x0], #16
        {0xf86068e6, Kind::LoadStoreRegister, X6 | X7 | X0}, // ldr x6, [x7, x0]
        {0xf87f68e6, Kind::LoadStoreRegister, X6 | X7},      // ldr x6, [x7, xzr]
        {0xf94000e0, Kind::LoadStoreImmediate, X0 | X7},     // ldr x0, [x7]
        {0xf90000e1, Kind::LoadStoreImmediate, X1 | X7},     // str x1, [x7]
        {0xa8c100e6, Kind::LoadStorePair, X6 | X7 | X0},     // ldp x6, x0, [x7], #16
        {0xa9bf04e6, Kind::LoadStorePair, X6 | X7 | X1},     // stp x6, x1, [x7, #-16]!
        {0x9b0800e6, Kind::Multiply, X6 | X7 | X8 | X0},     // madd x6, x7, x8, x0
        {0x9b0884e6, Kind::Multiply, X6 | X7 | X8 | X1},     // msub x6, x7, x8, x1
        {0x9b487ce6, Kind::Multiply, X6 | X7 | X8},          // smulh x6, x7, x8
        {0xfa4008c0, Kind::ConditionalCompare, X6},          // ccmp x6, #0, #0, eq
        {0xfa4000c0, Kind::ConditionalCompare, X6 | X0},     // ccmp x6, x0, #0, eq
        {0x94000002, Kind::BranchImmediate, Lr},             // bl +8
        {0x14000000, Kind::BranchImmediate, 0},              // b +0
        {0xd63f0100, Kind::BranchRegister, Lr | X8},         // blr x8
        {0xd63f03c0, Kind::BranchRegister, Lr},              // blr x30
        {0xd61f03c0, Kind::BranchRegister, Lr},              // br x30
        {0xd61f0000, Kind::BranchRegister, X0},              // br x0
        {0x3dc000e0, Kind::LoadStoreImmediate, X7},          // ldr q0, [x7]
        {0x3d800001, Kind::LoadStoreImmediate, X0},          // str q1, [x0]
        {0xad4007e0, Kind::LoadStorePair, Sp},               // ldp q0, q1, [sp]
        {0xad4000e6, Kind::LoadStorePair, X7},               // ldp q6, q0, [x7]
        {0x58000040, Kind::LoadLiteral, X0},                 // ldr x0, +8
        {0x9c000040, Kind::LoadLiteral, 0},                  // ldr q0, +8
        {0xd8000040, Kind::LoadLiteral, 0},                  // prfm pldl1keep, +8
        {0xeb0700df, Kind::AddSubShifted, X6 | X7},          // cmp x6, x7
        {0xeb0000df, Kind::AddSubShifted, X6 | X0},          // cmp x6, x0
        {0xf1000fff, Kind::AddSubImmediate, Sp},             // cmp sp, #3
        {0xf1000cdf, Kind::AddSubImmediate, X6},             // cmp x6, #3
    };
    for (const auto& test : cases) {
        const auto op = DecodeFields(test.word);
        const bool touches = (pins & test.operands) != 0;
        if (op.kind != test.kind || InstructionTouchesPinnedReg(op) != touches)
            return false;
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        const auto branch = DecodeFields(0x54000040); // b.eq +8
        if (IsUnshiftedCompare(op)) {
            const auto selected = LowerCompareBranch(op, branch);
            const auto expected =
                SplitPinTouches && !touches ? BindCompareBranch<false>(op, 0) : BindCompareBranch<true>(op, 0);
            if (selected != expected)
                return false;
        }
#endif
    }
    return true;
}

bool GenericLinkRegisterWrites() {
    Backing backing;
    backing.Code(Base, {
                           0x94000002, // bl +8
                           0xd4200000, // brk #0
                           0x910003c7, // mov x7, x30
                           0xd63f0100, // blr x8
                           0xd4200000, // brk #0
                           0x910003c9, // mov x9, x30
                           0xd4000001, // svc #0
                       });
    State state;
    state.pc = Base;
    state.x[8] = Base + 20;
    const auto result = Run(state, backing.Callbacks(), 8);
    return result.reason == Stop::Syscall && result.executed == 5 && state.x[7] == Base + 4 &&
           state.x[9] == Base + 16 && state.x[30] == Base + 16;
}

#if FIBERCPU_AARCH64_TAIL_DISPATCH
bool PinFreeDispatchKeepsLivePins() {
    using namespace fiberish::aarch64::detail;
    constexpr std::array<unsigned, FIBERCPU_AARCH64_PIN_COUNT> pins{
#define A64_TEST_PIN(id, slot) id,
        FIBERCPU_AARCH64_FOR_EACH_PIN(A64_TEST_PIN)
#undef A64_TEST_PIN
    };
    const unsigned counter = pins.empty() ? 0 : pins.front();
    std::array<unsigned, 3> scratch{};
    unsigned found = 0;
    for (unsigned id = 1; found < scratch.size(); ++id) {
        if (id != counter && std::ranges::find(pins, id) == pins.end())
            scratch[found++] = id;
    }
    const auto [accumulator, base, target] = scratch;
    const uint32_t increment_pin = 0x91000400 | (counter << 5) | counter;
    const uint32_t increment_scalar = 0x91000400 | (accumulator << 5) | accumulator;
    const uint32_t load = 0xf9400000 | (base << 5) | target;
    if (InstructionTouchesPinnedReg(DecodeFields(increment_pin)) != !pins.empty() ||
        InstructionTouchesPinnedReg(DecodeFields(increment_scalar)) || InstructionTouchesPinnedReg(DecodeFields(load)))
        return false;
    if constexpr (SplitPinTouches) {
        using enum Kind;
        constexpr DispatchPolicy AddPolicy{.wide = HasExtraOperandSlot(AddSubImmediate),
                                           .branch = BranchPolicy(AddSubImmediate)};
        constexpr DispatchPolicy LoadPolicy{.wide = HasExtraOperandSlot(LoadStoreImmediate), .may_replay = true};
        constexpr auto ScalarAdd =
            &DispatchWrapper<IntegerLogic<AddSubImmediate, 64, 0, -1, true, IntegerForm::Default, false>, AddPolicy>;
        constexpr auto ScalarLoad =
            &DispatchWrapper<LoadStoreLogic<LoadStoreImmediate, 64, 1, 0, -1, 0, false>, LoadPolicy>;
        const auto add_op = DecodeFields(increment_scalar);
        const auto load_op = DecodeFields(load);
        if (SelectPinHandler(Lower(add_op), add_op).handler != ScalarAdd ||
            SelectPinHandler(Lower(load_op), load_op).handler != ScalarLoad)
            return false;
    }

    Backing backing;
    backing.Code(Base, {increment_pin, increment_scalar, load, increment_pin, 0xd4000001});
    constexpr auto Data = Base + Page;
    alignas(Page) std::array<uint8_t, Page> host_data{42};
    backing.Put(Data, 42);
    fiberish::mem::SoftTlb tlb;
    tlb.read_tlb[(Data >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(ArmPageTag(Data), fiberish::mem::kInvalidMicroTlbTag),
        reinterpret_cast<uintptr_t>(host_data.data()) - Data,
    };
    for (const auto address : {Data, Data + Page}) {
        for (bool tlb_hit : {false, true}) {
            auto memory = backing.Callbacks();
            if (tlb_hit)
                memory.data_tlb = &tlb;
            State state;
            state.pc = Base;
            state.x[base] = address;
            state.x[accumulator] = 8;
            auto& pinned = counter == 31 ? state.sp : state.x[counter];
            pinned = 10;
            backing.observed_reads = 0;
            backing.observe_on_read = &state;
            const auto result = Run(state, memory, 5);
            if (state.x[accumulator] != 9)
                return false;
            if (address == Data) {
                if (result.reason != Stop::Syscall || result.executed != 5 || pinned != 12 || state.x[target] != 42 ||
                    backing.observed_reads != (tlb_hit ? 0u : 1u))
                    return false;
            } else if (result.reason != Stop::Fault || result.executed != 2 || pinned != 11 || state.pc != Base + 8 ||
                       state.x[target] != 0) {
                return false;
            }
        }
    }
    return true;
}
#endif

constexpr unsigned PinnedRegisterOr(unsigned fallback) {
#define A64_TEST_PIN(reg, slot)                                                                                        \
    if constexpr (reg < 31)                                                                                            \
        return reg;
    FIBERCPU_AARCH64_FOR_EACH_PIN(A64_TEST_PIN)
#undef A64_TEST_PIN
    return fallback;
}

bool FusedPCRelativeMembers() {
    constexpr unsigned Counter = PinnedRegisterOr(8);
    constexpr unsigned AddressReg = Counter == 3 ? 4 : 3;
    constexpr uint32_t Adds = 0x31000400 | (Counter << 5) | Counter; // adds wCounter, wCounter, #1
    constexpr uint32_t Address = 0x10000000 | AddressReg;
    constexpr uint32_t Link = 0x94000000;
    constexpr uint32_t Equal = 0x54000000;
    constexpr uint32_t ZAndC = 0x60000000;
    static unsigned sequence_matches = 0;
    sequence_matches = 0;
    const Profile profile{.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                                 uint32_t dead) -> detail::HandlerFunc {
        if (count != 3 || words[0] != Adds || words[1] != Address)
            return nullptr;
        if (words[2] == Link) {
            ++sequence_matches;
            *matched = 3;
            return BindBakedFusion<Adds, Address, Link>(dead);
        }
        if (words[2] == Equal) {
            ++sequence_matches;
            *matched = 3;
            return BindBakedFusion<Adds, Address, Equal>(dead);
        }
        return nullptr;
    }};
    Backing backing;
    const auto memory = backing.Callbacks();
    for (const auto [start, branch] : {std::pair{Base, Link}, std::pair{Base + 64, Equal}}) {
        for (const unsigned displacement : {8u, 20u}) {
            backing.Code(start, {Adds, Address | (displacement << 3),
                                 branch | (branch == Link ? displacement / 4 : displacement << 3)});
            for (unsigned budget : {2u, 3u}) {
                State state;
                state.pc = start;
                state.x[Counter] = UINT64_MAX;
                const auto result = Run(state, memory, budget, profile);
                const bool linked = branch == Link && budget == 3;
                const auto expected_counter = Counter == 30 && linked ? start + 12 : 0;
                if (result.reason != Stop::Budget || result.executed != budget ||
                    state.pc != start + 8 + (budget == 3 ? displacement : 0) || state.x[Counter] != expected_counter ||
                    state.x[AddressReg] != start + 4 + displacement || state.x[30] != (linked ? start + 12 : 0) ||
                    state.nzcv != ZAndC)
                    return false;
            }
        }
    }
    return sequence_matches == 8;
}

template <uint32_t... Words>
A64_FORCE_INLINE StepResult CountingBakedLogic(State& state, const detail::DecodedOp*& current A64_UTLB_REF_PARAMS,
                                               uint32_t& flags_cache A64_HOT_PIN_REF) {
    ++static_cast<Backing*>(state.active_run->memory.context)->sequences;
    return detail::BakedLogic<0, false, Words...>(state, current A64_UTLB_ARGS, flags_cache A64_HANDLER_PIN_ARGS);
}

bool FusedLoadRefetch() {
    constexpr auto Start = Base + Page - 12;
    constexpr uint32_t Add1 = 0x91000421; // add x1, x1, #1
    constexpr uint32_t Load = 0xb9400062; // ldr w2, [x3]
    constexpr uint32_t Add2 = 0x91000821; // add x1, x1, #2
    Backing backing;
    backing.Code(Start, {Add1, Load, Add2});
    Profile profile;
    profile.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                  uint32_t) -> detail::HandlerFunc {
        if (count < 3 || words[0] != Add1 || words[1] != Load || words[2] != Add2)
            return nullptr;
        *matched = 3;
        return &detail::DispatchWrapper<CountingBakedLogic<Add1, Load, Add2>,
                                        detail::BakedPolicy<0, Add1, Load, Add2>()>;
    };
    State state;
    state.pc = Start;
    auto memory = backing.Callbacks();
    auto result = Run(state, memory, 3, profile);
    if (result.reason != Stop::Fault || result.executed != 1 || state.pc != Start + 4 || result.instruction != Load ||
        state.x[1] != 1 || backing.sequences != 1)
        return false;

    state.pc = Start;
    state.x[1] = 0;
    state.x[3] = Base + Page;
    memory.read_fast = [](void* context, uint64_t address, void* destination, size_t size) {
        const auto& backing = *static_cast<Backing*>(context);
        if (!Backing::InBounds(address, size))
            return false;
        std::memcpy(destination, backing.data.data() + address - Base, size);
        return true;
    };
    result = Run(state, memory, 3, profile);
    if (result.reason != Stop::Budget || result.executed != 3 || state.pc != Start + 12 || state.x[1] != 3 ||
        backing.sequences != 2)
        return false;

    state.pc = Start;
    state.x[1] = 0;
    backing.replace_after_read = Start + 8;
    memory.read_fast = nullptr;
    result = Run(state, memory, 3, profile);
    return result.reason == Stop::Budget && result.executed == 3 && state.pc == Start + 12 && state.x[1] == 4 &&
           backing.sequences == 3;
}

bool FusedLoadReplay() {
    constexpr auto Start = Base + Page - 12;
    constexpr auto Lead = Start - 8;
    constexpr auto Data = Base + Page + 64;
    constexpr auto HotData = 0x00410000u;
    constexpr unsigned AddressReg = PinnedRegisterOr(3);
    constexpr unsigned Target = AddressReg == 2 ? 4 : 2;
    constexpr unsigned Counter = AddressReg == 1 ? 5 : 1;
    constexpr unsigned Observer = AddressReg == 6 ? 7 : 6;
    // Read the base register again in the final member to verify that a hit's
    // post-index writeback is forwarded to the rest of the hot fusion.
    constexpr uint32_t Prefix = 0x91000400 | (Counter << 5) | Counter;
    constexpr uint32_t Load = 0xb8404400 | (AddressReg << 5) | Target;      // ldr wTarget, [xAddressReg], #4
    constexpr uint32_t Observe = 0x91000000 | (AddressReg << 5) | Observer; // mov xObserver, xAddressReg
    Backing backing;
    backing.Put(Lead, 0x14000002); // b Start
    backing.Code(Start, {Prefix, Load, Observe});
    backing.Put(Data, 42);
    auto memory = backing.Callbacks();
    Profile profile;
    profile.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                  uint32_t dead) -> detail::HandlerFunc {
        if (count < 3 || words[0] != Prefix || words[1] != Load || words[2] != Observe)
            return nullptr;
        *matched = 3;
        return BindBakedFusion<Prefix, Load, Observe>(dead);
    };

    State state;
    state.pc = Start;
    const auto fault = Run(state, memory, 3, profile);
    if (fault.reason != Stop::Fault || fault.executed != 1 || fault.instruction != Load || state.pc != Start + 4 ||
        state.x[Counter] != 1 || state.x[Target] != 0 || state.x[AddressReg] != 0)
        return false;

    memory.read_fast = [](void* context, uint64_t address, void* destination, size_t size) {
        auto& backing = *static_cast<Backing*>(context);
        ++backing.sequences;
        if (!Backing::InBounds(address, size))
            return false;
        std::memcpy(destination, backing.data.data() + address - Base, size);
        return true;
    };
    state = {};
    state.pc = Start;
    state.x[AddressReg] = Data;
    const auto filled = Run(state, memory, 3, profile);
    if (filled.reason != Stop::Budget || filled.executed != 3 || state.pc != Start + 12 || state.x[Counter] != 1 ||
        state.x[Target] != 42 || state.x[AddressReg] != Data + 4 || state.x[Observer] != Data + 4 ||
        backing.sequences != 1)
        return false;

    std::array<uint8_t, Page + 4> data{42};
    fiberish::mem::SoftTlb tlb;
    tlb.read_tlb[(HotData >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(ArmPageTag(HotData), fiberish::mem::kInvalidMicroTlbTag),
        reinterpret_cast<uintptr_t>(data.data()) - HotData,
    };
    memory.data_tlb = &tlb;
    state = {};
    state.pc = Lead;
    state.x[AddressReg] = HotData;
    const auto hit = Run(state, memory, 4, profile);
    if (hit.reason != Stop::Budget || hit.executed != 4 || state.x[Target] != 42 ||
        state.x[AddressReg] != HotData + 4 || state.x[Observer] != HotData + 4 || backing.sequences != 1)
        return false;

    // Exercise replay after starting from the preceding branch.
    for (const auto address : {0x1'0000'0000ull + HotData, HotData + Page - 2ull}) {
        state = {};
        state.pc = Lead;
        state.x[AddressReg] = address;
        const auto miss = Run(state, memory, 4, profile);
        if (miss.reason != Stop::Fault || miss.executed != 2 || state.pc != Start + 4 || state.x[Counter] != 1 ||
            state.x[Target] != 0 || state.x[AddressReg] != address)
            return false;
    }

    memory.read_fast = nullptr;
    backing.replace_after_read = Start + 8;
    state = {};
    state.pc = Start;
    state.x[AddressReg] = Data;
    const auto refetched = Run(state, memory, 3, profile);
    const auto expected_x1 = AddressReg == 1 ? Data + 7 : 4;
    return refetched.reason == Stop::Budget && refetched.executed == 3 && state.pc == Start + 12 &&
           state.x[1] == expected_x1 && state.x[Target] == 42 && (AddressReg == 1 || state.x[AddressReg] == Data + 4);
}

bool FusedStoreReplay() {
    constexpr auto Start = Base + Page - 12;
    constexpr auto Data = Base + Page + 64;
    constexpr unsigned AddressReg = PinnedRegisterOr(3);
    constexpr unsigned Source = AddressReg == 2 ? 4 : 2;
    constexpr uint32_t Prefix = 0xb1004000 | (AddressReg << 5) | AddressReg;
    constexpr uint32_t Store = 0xb8004400 | (AddressReg << 5) | Source;
    constexpr uint32_t Suffix = 0x91000400 | (AddressReg << 5) | AddressReg;
    constexpr uint32_t Changed = 0x91001c00 | (AddressReg << 5) | AddressReg;
    State state;
    struct Fixture : Backing {
        State* cpu;
        bool observed_prefix = false;
    } backing{{}, &state};
    backing.Code(Start, {Prefix, Store, Suffix});
    auto memory = backing.Callbacks();
    memory.write = [](void* context, uint64_t address, const void* data, size_t size) {
        auto& fixture = *static_cast<Fixture*>(static_cast<Backing*>(context));
        if (address != Data || size != sizeof(uint32_t))
            return false;
        fixture.observed_prefix =
            fixture.cpu->pc == Start + 4 && fixture.cpu->nzcv == 0 && fixture.cpu->x[AddressReg] == Data;
        ++fixture.writes;
        std::memcpy(fixture.data.data() + address - Base, data, size);
        fixture.Put(Start + 8, Changed);
        return true;
    };
    Profile profile;
    profile.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                  uint32_t dead) -> detail::HandlerFunc {
        if (count < 3 || words[0] != Prefix || words[1] != Store || words[2] != Suffix)
            return nullptr;
        *matched = 3;
        return BindBakedFusion<Prefix, Store, Suffix>(dead);
    };
    state.pc = Start;
    state.nzcv = detail::FlagMask;
    state.x[AddressReg] = Data - 16;
    state.x[Source] = 123;
    const auto result = Run(state, memory, 3, profile);
    return result.reason == Stop::Budget && result.executed == 3 && state.pc == Start + 12 && backing.writes == 1 &&
           backing.observed_prefix && backing.data[Data - Base] == 123 && state.x[AddressReg] == Data + 11 &&
           state.nzcv == 0;
}

bool FusedStorePromotesReadWriteMicroTlb() {
#if FIBERCPU_AARCH64_TAIL_DISPATCH
    constexpr uint32_t Store = 0xb8004420;  // str w0, [x1], #4
    constexpr uint32_t Load = 0xb9400062;   // ldr w2, [x3]
    constexpr uint32_t Branch = 0x14000001; // b +4
    constexpr uint64_t Data = ArmTaskSize - Page;
    Backing backing;
    backing.Code(Base, {Store, Load, Branch});
    auto memory = backing.Callbacks();
    memory.read = nullptr;
    memory.write = nullptr;
    uint32_t word = 0;
    fiberish::mem::SoftTlb tlb;
    // When the register micro-TLB is enabled, a store hit in write_tlb promotes
    // the read tag into the handler ABI so the following load hits without a
    // read_tlb entry. When disabled, the load probes read_tlb directly.
    const auto tlb_index = (Data >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK;
    const fiberish::mem::MicroTLB entry{
        fiberish::mem::PackMicroTlbTags(ArmPageTag(Data), ArmPageTag(Data)),
        reinterpret_cast<uintptr_t>(&word) - static_cast<uintptr_t>(Data),
    };
    tlb.write_tlb[tlb_index] = entry;
#if !FIBERCPU_AARCH64_MICRO_TLB
    tlb.read_tlb[tlb_index] = entry;
#endif
    memory.data_tlb = &tlb;
    Profile profile{};
    profile.resolve_sequence = [](const uint32_t*, unsigned count, unsigned* length,
                                  uint32_t dead) -> detail::HandlerFunc {
        if (count != 3)
            return nullptr;
        *length = 3;
        return BindBakedFusion<Store, Load, Branch>(dead);
    };
    for (const auto& bindings : {Profile{}, profile}) {
        State state{};
        state.pc = Base;
        state.x[0] = 123;
        state.x[1] = state.x[3] = Data;
        state.exclusive_address = Data;
        state.exclusive_size = 4;
        const auto result = Run(state, memory, 3, bindings);
        if (result.reason != Stop::Budget || result.executed != 3 || state.pc != Base + 12 || state.x[1] != Data + 4 ||
            state.x[2] != 123 || word != 123 || state.exclusive_size)
            return false;
    }
#endif
    return true;
}

bool BoundLiteralAddress() {
    constexpr uint64_t GuestPage = Base;
    constexpr uint64_t Start = GuestPage + 32;
    constexpr uint32_t Add = 0x11000421;    // add w1, w1, #1
    constexpr uint32_t Load = 0x18ffffc2;   // ldr w2, pc - 8
    constexpr uint32_t Branch = 0x14000001; // b +4
    constexpr auto LoadKey = detail::CanonicalizeBakedWord(Load);
    constexpr auto BranchKey = detail::CanonicalizeBakedWord(Branch);
    Backing backing;
    backing.Put(Base + 28, 123);
    backing.Code(Base + 32, {Add, Load, Branch});
    fiberish::mem::SoftTlb tlb;
    tlb.read_tlb[(GuestPage >> fiberish::mem::PAGE_SHIFT) & fiberish::mem::TLB_INDEX_MASK] = {
        fiberish::mem::PackMicroTlbTags(ArmPageTag(GuestPage), fiberish::mem::kInvalidMicroTlbTag),
        reinterpret_cast<uintptr_t>(backing.data.data()) - static_cast<uintptr_t>(GuestPage),
    };
    Memory memory{&backing};
    memory.data_tlb = &tlb;
    memory.executable_page = [](void* context, uint64_t address) -> const uint8_t* {
        return address == GuestPage ? static_cast<Backing*>(context)->data.data() : nullptr;
    };
    Profile fused;
    fused.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* length,
                                uint32_t dead) -> detail::HandlerFunc {
        if (count < 3 || words[0] != Add || words[1] != LoadKey || words[2] != BranchKey)
            return nullptr;
        *length = 3;
        return BindBakedFusion<Add, LoadKey, BranchKey>(dead);
    };
    for (const auto& profile : {Profile{}, fused}) {
        for (const unsigned budget : {2u, 3u}) {
            State state;
            state.pc = Start;
            const auto result = Run(state, memory, budget, profile);
            if (result.reason != Stop::Budget || result.executed != budget || state.pc != Start + budget * 4 ||
                state.x[1] != 1 || state.x[2] != 123)
                return false;
        }
    }
    return true;
}

constexpr uint32_t NFlag = 1u << 31;
constexpr uint32_t ZFlag = 1u << 30;
constexpr uint32_t CFlag = 1u << 29;
constexpr uint32_t VFlag = 1u << 28;
constexpr uint32_t AddsX0 = 0xb1000400; // adds x0, x0, #1
constexpr uint32_t AddX4 = 0x91000c84;  // add x4, x4, #3
constexpr uint32_t AdcsX1 = 0xba1f0021; // adcs x1, x1, xzr
constexpr uint32_t AddsX2 = 0xb1000442; // adds x2, x2, #1
constexpr uint32_t CmpX3 = 0xf100047f;  // cmp x3, #1
constexpr uint32_t Skip = 0x14000002;   // b +8
constexpr uint32_t UndefinedWord = 0xffffffff;

#if defined(__aarch64__) && !defined(_MSC_VER)
bool HardwareFlagsAtBudget(unsigned budget, const State& initial, const State& expected) {
    auto registers = initial.x;
    uint64_t saved_flags, observed_flags;
    asm volatile("mrs %x[saved], nzcv\n"
                 "msr nzcv, %x[seed]\n"
                 "ldp x0, x1, [%[registers]]\n"
                 "ldp x2, x3, [%[registers], #16]\n"
                 "ldr x4, [%[registers], #32]\n"
                 "mov w5, %w[budget]\n"
                 "cbz w5, 1f\n"
                 ".inst %c[adds0]\n"
                 "sub w5, w5, #1\n"
                 "cbz w5, 1f\n"
                 ".inst %c[add4]\n"
                 "sub w5, w5, #1\n"
                 "cbz w5, 1f\n"
                 ".inst %c[adcs1]\n"
                 "sub w5, w5, #1\n"
                 "cbz w5, 1f\n"
                 ".inst %c[adds2]\n"
                 "sub w5, w5, #1\n"
                 "cbz w5, 1f\n"
                 ".inst %c[cmp3]\n"
                 "sub w5, w5, #1\n"
                 "cbz w5, 1f\n"
                 ".inst %c[skip]\n"
                 ".inst %c[undefined]\n"
                 "1:\n"
                 "mrs %x[observed], nzcv\n"
                 "msr nzcv, %x[saved]\n"
                 "stp x0, x1, [%[registers]]\n"
                 "stp x2, x3, [%[registers], #16]\n"
                 "str x4, [%[registers], #32]\n"
                 : [saved] "=&r"(saved_flags), [observed] "=&r"(observed_flags)
                 : [seed] "r"(uint64_t(initial.nzcv)), [registers] "r"(registers.data()), [budget] "r"(budget),
                   [adds0] "i"(AddsX0), [add4] "i"(AddX4), [adcs1] "i"(AdcsX1), [adds2] "i"(AddsX2), [cmp3] "i"(CmpX3),
                   [skip] "i"(Skip), [undefined] "i"(UndefinedWord)
                 : "x0", "x1", "x2", "x3", "x4", "x5", "cc", "memory");
    return observed_flags == expected.nzcv && registers == expected.x;
}
#endif

bool FlagElisionBudgets() {
    Backing backing;
    backing.Code(Base, {AddsX0, AddX4, AdcsX1, AddsX2, CmpX3, Skip, UndefinedWord});
    State initial;
    initial.pc = Base;
    initial.nzcv = NFlag;
    initial.x[0] = UINT64_MAX;
    initial.x[1] = INT64_MAX;
    initial.x[2] = UINT64_MAX;
    initial.x[3] = uint64_t{1} << 63;
    initial.x[4] = 11;
    constexpr std::array ExpectedFlags{NFlag,         ZFlag | CFlag, ZFlag | CFlag, NFlag | VFlag,
                                       ZFlag | CFlag, CFlag | VFlag, CFlag | VFlag};
    constexpr unsigned BlockWords = ExpectedFlags.size() - 1;
    static uint32_t fused_dead_mask = 0;
    static bool bound_fusion = false;
    Profile baked;
    baked.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                uint32_t dead_mask) -> detail::HandlerFunc {
        constexpr std::array Prefix{AddsX0, AddX4, AdcsX1, AddsX2};
        if (count < Prefix.size() || !std::equal(Prefix.begin(), Prefix.end(), words))
            return nullptr;
        *matched = Prefix.size();
        fused_dead_mask = dead_mask & ((1u << Prefix.size()) - 1);
        const auto handler = BindBakedFusion<AddsX0, AddX4, AdcsX1, AddsX2>(dead_mask);
        bound_fusion = handler != nullptr;
        return handler;
    };

    for (const auto& profile : {Profile{}, baked}) {
        // Warm a complete span before retiring any prefix of the same words.
        auto warmed = initial;
        if (Run(warmed, backing.Callbacks(), BlockWords, profile).reason != Stop::Budget)
            return false;
        for (unsigned budget = 0; budget <= BlockWords; ++budget) {
            auto scalar = initial;
            const auto scalar_result = Run(scalar, backing.Callbacks(false), budget);
            auto block = initial;
            const auto block_result = Run(block, backing.Callbacks(), budget, profile);
            const auto next_pc = Base + 4 * (budget == BlockWords ? budget + 1 : budget);
            if (scalar_result.reason != Stop::Budget || scalar_result.executed != budget || scalar.pc != next_pc ||
                scalar.nzcv != ExpectedFlags[budget] || block_result.reason != scalar_result.reason ||
                block_result.executed != scalar_result.executed || block.pc != scalar.pc || block.nzcv != scalar.nzcv ||
                block.x != scalar.x)
                return false;
            // ADCS needs the incoming carry even when the next instruction overwrites its NZCV.
            if (budget >= 3 && block.x[1] != (uint64_t{1} << 63))
                return false;
#if defined(__aarch64__) && !defined(_MSC_VER)
            if (!HardwareFlagsAtBudget(budget, initial, block))
                return false;
#endif
        }
    }
#if defined(FIBERCPU_AARCH64_DFE) && !FIBERCPU_AARCH64_DFE
    constexpr uint32_t ExpectedDeadMask = 0;
#else
    constexpr uint32_t ExpectedDeadMask = 0b1100;
#endif
    return bound_fusion && fused_dead_mask == ExpectedDeadMask;
}

bool FlagElisionBranchAndCache() {
    constexpr auto Other = Base + 128;
    constexpr uint32_t BNe = 0x54000041;  // b.ne +8
    constexpr uint32_t Adc = 0x9a1f03e8;  // adc x8, xzr, xzr
    constexpr uint32_t CSet = 0x9a9f77e9; // cset x9, vs
    for (const bool span : {false, true}) {
        Backing backing;
        backing.Code(Base, {AddsX2, CmpX3, BNe, UndefinedWord, Adc, CSet});
        backing.Code(Other, {AddsX2, Skip, UndefinedWord});
        const auto memory = backing.Callbacks(span);
        State state;
        for (unsigned i = 0; i < 3; ++i) {
            state.pc = Base;
            state.x[2] = UINT64_MAX;
            state.x[3] = uint64_t{1} << 63;
            state.nzcv = NFlag | ZFlag;
            const auto branch = Run(state, memory, 3);
            // B.NE only consumes Z. C and V are observable after the block and
            // must also reach the first two instructions on the taken path.
            if (branch.reason != Stop::Budget || branch.executed != 3 || state.pc != Base + 16 || state.x[2] ||
                state.nzcv != (CFlag | VFlag))
                return false;
            if (Run(state, memory, 2).reason != Stop::Budget || state.x[8] != 1 || state.x[9] != 1 ||
                state.nzcv != (CFlag | VFlag))
                return false;

            // The exact same ADDS word is a live flag writer in this block.
            state.pc = Other;
            state.x[2] = UINT64_MAX;
            const auto second = Run(state, memory, 2);
            if (second.reason != Stop::Budget || second.executed != 2 || state.pc != Other + 12 || state.x[2] ||
                state.nzcv != (ZFlag | CFlag))
                return false;
        }
    }
    return true;
}

bool FlagElisionEvents() {
    constexpr uint32_t Load = 0xb94000c5; // ldr w5, [x6]
    constexpr uint32_t Svc = 0xd4000841;  // svc #0x42
    static uint32_t fused_dead_mask = UINT32_MAX;
    const Profile baked{.resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* matched,
                                               uint32_t dead_mask) -> detail::HandlerFunc {
        if (count < 3 || words[0] != AddsX0 || words[1] != Load || words[2] != CmpX3)
            return nullptr;
        *matched = 3;
        const auto handler = BindBakedFusion<AddsX0, Load, CmpX3>(dead_mask);
        if (handler)
            fused_dead_mask = dead_mask & 0b111;
        return handler;
    }};
    const Profile generic;
    for (unsigned mode = 0; mode < 3; ++mode) {
        Backing backing;
        backing.Code(Base, {AddsX0, Load, CmpX3, Skip, UndefinedWord});
        const auto memory = backing.Callbacks(mode != 0);
        const auto& profile = mode == 2 ? baked : generic;
        State state;
        state.pc = Base;
        state.x[0] = UINT64_MAX;
        state.x[5] = 99;
        state.x[6] = Base + 2 * Page;
        const auto fault = Run(state, memory, 4, profile);
        if (fault.reason != Stop::Fault || fault.executed != 1 || fault.instruction != Load ||
            fault.address != state.x[6] || fault.access != Access::Read || state.pc != Base + 4 || state.x[0] ||
            state.x[5] != 99 || state.nzcv != (ZFlag | CFlag))
            return false;

        state.pc = Base;
        state.x[0] = UINT64_MAX;
        state.x[6] = Base + Page;
        state.nzcv = NFlag;
        backing.Put(state.x[6], 42);
        backing.observe_on_read = &state;
        backing.replace_after_read = Base + 8;
        const auto callback = Run(state, memory, 4, profile);
        if (callback.reason != Stop::Budget || callback.executed != 4 || state.pc != Base + 20 || state.x[0] ||
            state.x[1] != 3 || state.x[5] != 42 || state.nzcv != (ZFlag | CFlag) || backing.observed_reads != 1 ||
            backing.observed_pc != Base + 4 || backing.observed_nzcv != (ZFlag | CFlag))
            return false;

        for (const auto word : {Svc, UndefinedWord}) {
            backing.Code(Base, {AddsX0, word, CmpX3, Skip, UndefinedWord});
            state.pc = Base;
            state.x[0] = UINT64_MAX;
            state.nzcv = NFlag;
            const auto trap = Run(state, memory, 4, profile);
            const bool syscall = word == Svc;
            if (trap.reason != (syscall ? Stop::Syscall : Stop::Undefined) || trap.executed != (syscall ? 2u : 1u) ||
                trap.address != Base + 4 || trap.instruction != word || trap.immediate != (syscall ? 0x42u : 0u) ||
                state.pc != Base + (syscall ? 8u : 4u) || state.x[0] || state.nzcv != (ZFlag | CFlag))
                return false;
        }
    }
    return fused_dead_mask == 0;
}

bool FlagElisionFloatingCompare() {
    Backing backing;
    backing.Code(Base, {0x1e612000, CmpX3, Skip, UndefinedWord}); // fcmp d0, d1
    for (const bool span : {false, true}) {
        const auto memory = backing.Callbacks(span);
        for (const unsigned budget : {3u, 1u}) {
            State state;
            state.pc = Base;
            state.fpsr = 1u << 1;               // Preserve the existing DZC bit.
            state.v[0].lo = 0x7ff0000000000042; // Signaling NaN.
            state.v[1].lo = std::bit_cast<uint64_t>(1.0);
            const auto result = Run(state, memory, budget);
            const auto expected_flags = budget == 1 ? CFlag | VFlag : NFlag;
            // The following CMP overwrites all NZCV but does not clear FCMP's IOC.
            if (result.reason != Stop::Budget || result.executed != budget || state.nzcv != expected_flags ||
                state.fpsr != 3)
                return false;
        }
    }
    return true;
}

bool CompactBitfieldOperands() {
    Backing backing;
    backing.Code(Base, {0xd360fe81, 0x9360fe82, 0xd4000001}); // lsr x1, x20, #32; asr x2, x20, #32; svc #0
    State state;
    state.pc = Base;
    state.x[20] = 0x80000006'00000402;
    const auto result = Run(state, backing.Callbacks(), 3);
    return result.reason == Stop::Syscall && result.executed == 3 && state.x[1] == 0x80000006 &&
           state.x[2] == 0xffffffff'80000006;
}

bool ExhaustiveCompactBitfieldOperands() {
    using namespace detail;
    constexpr uint64_t values[] = {0, ~uint64_t{0}, 0x8000'0001'8000'0001, 0x5678'1234'a9bc'def0};
    for (unsigned sf = 0; sf < 2; ++sf) {
        const unsigned width = sf ? 64 : 32;
        for (unsigned n = 0; n < 2; ++n)
            for (unsigned operation = 0; operation < 4; ++operation)
                for (unsigned r = 0; r < 64; ++r)
                    for (unsigned s = 0; s < 64; ++s) {
                        const uint32_t word =
                            0x13000022 | (sf << 31) | (operation << 29) | (n << 22) | (r << 16) | (s << 10);
                        const auto decoded = DecodeFields(word);
                        const bool valid = sf == n && operation < 3 && r < width && s < width;
                        if ((decoded.kind == Kind::Bitfield) != valid)
                            return false;
                        if (!valid)
                            continue;
                        auto reference = decoded;
                        if (!DecodeBitMasks(reference, n, r, s, false) || HasExtraOperandSlot(decoded))
                            return false;
                        const DecodedOp slot{.payload = DecodedOp::Pack(PackOpOperands(decoded), 31, 0, 1, 0)};
                        const auto compact = GetOpOperands<Kind::Bitfield>(slot);
                        if (compact.width != width || compact.operation != operation || compact.shift != r ||
                            compact.rm != s || compact.rn != 1 || compact.rd != 2)
                            return false;
                        for (const auto source : values)
                            for (const auto dest : values) {
                                State expected, actual;
                                expected.x[1] = actual.x[1] = source;
                                expected.x[2] = actual.x[2] = dest;
                                IntegerKernel<Kind::Bitfield>(ScalarRegisters{expected}, reference, 0, expected.nzcv);
                                const auto check = [&]<unsigned bit_width, int op>() {
                                    IntegerKernel<Kind::Bitfield, bit_width, op, -1, true, false,
                                                  IntegerForm::BitfieldShifts>(ScalarRegisters{actual}, compact, 0,
                                                                               actual.nzcv);
                                };
                                if (sf) {
                                    if (operation == 0)
                                        check.template operator()<64, 0>();
                                    if (operation == 1)
                                        check.template operator()<64, 1>();
                                    if (operation == 2)
                                        check.template operator()<64, 2>();
                                } else {
                                    if (operation == 0)
                                        check.template operator()<32, 0>();
                                    if (operation == 1)
                                        check.template operator()<32, 1>();
                                    if (operation == 2)
                                        check.template operator()<32, 2>();
                                }
                                if (actual.x[2] != expected.x[2])
                                    return false;
                            }
                    }
    }
    return true;
}

struct CachedBacking : Backing {
    alignas(detail::BasicBlock) std::array<std::array<std::byte, 1024>, 4> storage;
    std::array<detail::BasicBlock*, 4> blocks{};
    std::array<size_t, 4> sizes{};
    unsigned allocations = 0;

    Memory Callbacks() {
        auto memory = Backing::Callbacks();
        memory.lookup_cached_block = [](void* context, uint64_t pc, const uint8_t*) -> detail::BasicBlock* {
            const auto& backing = *static_cast<const CachedBacking*>(static_cast<const Backing*>(context));
            for (auto* block : backing.blocks)
                if (block && block->start_pc() == pc)
                    return block;
            return nullptr;
        };
        memory.allocate_block = [](void* context, size_t size, uint64_t) -> void* {
            auto& backing = *static_cast<CachedBacking*>(static_cast<Backing*>(context));
            if (backing.allocations >= backing.storage.size() || size > backing.storage.front().size())
                return nullptr;
            backing.sizes[backing.allocations] = size;
            return backing.storage[backing.allocations].data();
        };
        memory.cache_block = [](void* context, detail::BasicBlock* block) {
            auto& backing = *static_cast<CachedBacking*>(static_cast<Backing*>(context));
            backing.blocks[backing.allocations++] = block;
        };
        return memory;
    }
};

bool CompactSlotsAndFoldedNopBudgets() {
    constexpr uint32_t Add = 0x91000421; // add x1, x1, #1
    constexpr uint32_t Nop = 0xd503201f;
    constexpr uint32_t Mov = 0xd2e24680;  // movz x0, #0x1234, lsl #48
    constexpr uint32_t Load = 0xf9400043; // ldr x3, [x2]
    constexpr uint32_t Svc = 0xd4000001;
    constexpr auto Branch = Base + 128;
    constexpr auto FaultAddress = Base + 2 * Page;

    CachedBacking backing;
    backing.Code(Base, {Add, Nop, Nop, Mov, Nop, Load, Nop, Svc});
    backing.Code(Branch, {0x17ffffe1}); // b Base + 4
    const auto memory = backing.Callbacks();

    for (unsigned budget = 1; budget <= 8; ++budget) {
        State state;
        state.pc = Base;
        state.x[2] = FaultAddress;
        const auto result = Run(state, memory, budget);
        if (budget <= 5) {
            if (result.reason != Stop::Budget || result.executed != budget || state.pc != Base + 4 * budget)
                return false;
        } else if (result.reason != Stop::Fault || result.executed != 5 || result.address != FaultAddress ||
                   result.instruction != Load || state.pc != Base + 20) {
            return false;
        }
        if (state.x[1] != 1 || state.x[0] != (budget >= 4 ? 0x1234'0000'0000'0000ull : 0))
            return false;
    }

    const auto& block = *backing.blocks.front();
    if (backing.allocations != 1 || block.inst_count() != 8 || block.sentinel_slot_index != 4 || !block.HasSentinel() ||
        block.HasInlineWords() || backing.sizes[0] != 112 || block.Word(1) != Nop || block.Word(2) != Nop ||
        block.Word(4) != Nop || block.Word(6) != Nop)
        return false;
    State inspect;
    inspect.last_block = &block;
    const auto* slot = block.FirstOp();
    for (const unsigned ordinal : {0u, 3u, 5u, 7u}) {
        if (slot->GuestIndex() != ordinal || slot->GetStartPc(inspect) != Base + 4 * ordinal ||
            slot->Has(detail::HasDataSlot))
            return false;
        slot = slot->NextOp();
    }
    if (slot != block.Sentinel() || block.end_pc() != Base + 32)
        return false;

    State state;
    state.pc = Branch;
    const auto result = Run(state, memory, 2);
    return result.reason == Stop::Budget && result.executed == 2 && state.pc == Base + 8 && state.x[1] == 0 &&
           backing.allocations == 3 && !backing.blocks[1]->HasSentinel() && !backing.blocks[1]->HasInlineWords() &&
           backing.sizes[1] == 64 && backing.blocks[2]->start_pc() == Base + 4 && backing.blocks[2]->Word(0) == Nop;
}

bool CompactOperandDispatch() {
    constexpr uint32_t Adrp = 0xb0000005; // adrp x5, Base + Page
    constexpr uint32_t Mov = 0xd2e24680;  // movz x0, #0x1234, lsl #48
    constexpr uint32_t Pair = 0xa9bf0860; // stp x0, x2, [x3, #-16]!
    constexpr uint32_t Bl = 0x94000002;   // bl .+8
    constexpr uint32_t AdrpKey = detail::CanonicalizeBakedWord(Adrp);
    constexpr uint32_t MovKey = Mov & ~PartialImmediateMask(detail::DecodeFields(Mov));
    constexpr uint32_t PairKey = Pair & ~PartialImmediateMask(detail::DecodeFields(Pair));
    constexpr auto Data = Base + Page;
    constexpr uint64_t Value = 0x1234'0000'0000'0000;

    const Profile baked{.resolve = [](const DecodedInstruction& op, bool) -> detail::HandlerFunc {
        switch (detail::CanonicalizeBakedWord(op)) {
        case AdrpKey:
            return GetBakedHandler<AdrpKey>();
        case Mov:
            return GetBakedHandler<Mov>();
        case Pair:
            return GetBakedHandler<Pair>();
        case detail::CanonicalizeBakedWord(Bl):
            return GetBakedHandler<detail::CanonicalizeBakedWord(Bl)>();
        default:
            return nullptr;
        }
    }};
    const Profile partial{
        .resolve_sequence =
            [](const uint32_t* words, unsigned count, unsigned* length, uint32_t dead) {
                if (count < 3 || words[0] != AdrpKey || words[1] != Mov || words[2] != Pair)
                    return detail::HandlerFunc{};
                *length = 3;
                return PartialBakedFusion<6, AdrpKey, MovKey, PairKey>.Select(dead, 3);
            },
    };
    for (const auto& profile : {Profile{}, baked, partial}) {
        CachedBacking backing;
        backing.Code(Base, {Adrp, Mov, Pair, Bl});
        auto memory = backing.Callbacks();
        fiberish::mem::SoftTlb tlb{};
        const auto tag = ArmPageTag(Data);
        tlb.write_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = {
            fiberish::mem::PackMicroTlbTags(fiberish::mem::kInvalidMicroTlbTag, tag),
            reinterpret_cast<uintptr_t>(backing.data.data() + Page) - Data,
        };
        memory.data_tlb = &tlb;
        State state{};
        state.pc = Base;
        state.x[2] = 7;
        state.x[3] = Data + 128;
        const auto result = Run(state, memory, 4, profile);
        if (result.reason != Stop::Budget || result.executed != 4 || state.pc != Base + 20 || state.x[0] != Value ||
            state.x[3] != Data + 112 || state.x[5] != Data || state.x[30] != Base + 16 ||
            detail::ReadLittle<uint64_t>(&backing.data[Page + 112]) != Value ||
            detail::ReadLittle<uint64_t>(&backing.data[Page + 120]) != 7 || backing.allocations != 1)
            return false;

        const auto& block = *backing.blocks.front();
        if (block.HasSentinel() || block.HasInlineWords() || backing.sizes.front() != 112 ||
            block.FirstOp()->Span() != (profile.resolve_sequence ? 3 : 1))
            return false;
        const auto* slot = block.FirstOp();
        for (unsigned ordinal = 0; ordinal < 3; ++ordinal, slot = slot->NextOp()) {
            if (slot->GuestIndex() != ordinal || slot->Has(detail::HasDataSlot))
                return false;
        }
        if (!slot->Has(detail::HasDataSlot) || slot->NextOp() != block.Sentinel())
            return false;
    }
    return true;
}

bool CompactSlotMetadata() {
    using namespace detail;
    constexpr auto flags = HasDataSlot | RetainsPage | HasBranchLink;
    const DecodedOp empty{.payload = DecodedOp::Pack(0, 0, 0, 1, 0)};
    const DecodedOp compact{.payload = DecodedOp::Pack(DecodedOp::OperandMask, 31, flags, MaxBakedFusionLength, 0)};
    const DecodedOp branch{.payload = DecodedOp::Pack(0xffffffff, 0, flags, BlockMax, 31)};
    return !empty.Operands() && !empty.GuestIndex() && empty.Span() == 1 && !empty.BranchSuffix() &&
           !empty.Has(HasDataSlot) && !empty.Has(RetainsPage) && !empty.Has(HasBranchLink) &&
           compact.Operands() == DecodedOp::OperandMask && compact.GuestIndex() == 31 &&
           compact.Span() == MaxBakedFusionLength && compact.Has(HasDataSlot) && compact.Has(RetainsPage) &&
           compact.Has(HasBranchLink) && branch.BranchSuffix() == 31 && uint32_t(branch.Operands()) == 0xffffffff &&
           branch.GuestIndex() == 0 && branch.Span() == BlockMax;
}

bool CompactAddressPreservesFullTarget() {
    for (const auto pc : {uint64_t{0}, ArmTaskSize - 4}) {
        for (const uint32_t word : {0x10800005u, 0x707fffe5u, 0x90800005u, 0xf07fffe5u}) {
            auto decoded = Decode(word);
            detail::BindBlockOperands(decoded, pc);
            const detail::DecodedOp slot{.payload =
                                             detail::DecodedOp::Pack(detail::PackOpOperands(decoded), 3, 0, 2, 0)};
            const auto op = detail::GetOpOperands<Kind::Address>(slot);
            if (decoded.kind != Kind::Address || detail::HasExtraOperandSlot(decoded) || op.operation != (word >> 31) ||
                op.rd != 5 || op.target_pc != decoded.target_pc || slot.GuestIndex() != 3 || slot.Span() != 2)
                return false;
        }
    }
    return true;
}

bool CompactLiteralOperands() {
    using namespace detail;
    constexpr struct {
        uint32_t encoding;
        unsigned width;
        unsigned operation;
        bool vector;
    } shapes[] = {{0x18000000, 32, Load, false},         {0x58000000, 64, Load, false},
                  {0x98000000, 32, LoadSigned64, false}, {0xd8000000, 32, Prefetch, false},
                  {0x1c000000, 32, Load, true},          {0x5c000000, 64, Load, true},
                  {0x9c000000, 128, Load, true}};
    for (const auto& shape : shapes) {
        for (const auto pc : {uint64_t{0}, ArmTaskSize - 4}) {
            for (const int offset : {-(1 << 18), -1, 0, (1 << 18) - 1}) {
                auto decoded = DecodeFields(shape.encoding | ((uint32_t(offset) & 0x7ffff) << 5) | 31);
                BindBlockOperands(decoded, pc);
                const DecodedOp slot{.payload = DecodedOp::Pack(PackOpOperands(decoded), 31, RetainsPage, 1, 0)};
                const auto op = GetOpOperands<Kind::LoadLiteral>(slot);
                if (decoded.kind != Kind::LoadLiteral || HasExtraOperandSlot(decoded) || op.rd != 31 ||
                    op.width != shape.width || op.operation != shape.operation ||
                    Field(op.word, 26, 1) != shape.vector || op.target_pc != pc + uint64_t(int64_t(offset) * 4))
                    return false;
            }
        }
        constexpr auto Start = Base + 32;
        constexpr uint64_t Lo = 0x1234'5678'89ab'cdef;
        constexpr uint64_t Hi = 0x7654'3210'fedc'ba98;
        constexpr uint32_t Svc = 0xd4000001;
        CachedBacking backing;
        WriteLittle(&backing.data[0], Lo);
        WriteLittle(&backing.data[8], Hi);
        backing.Code(Start, {shape.encoding | (0x7fff8 << 5) | 3, Svc});
        auto memory = backing.Callbacks();
        fiberish::mem::SoftTlb tlb{};
        const auto tag = ArmPageTag(Base);
        tlb.read_tlb[tag & fiberish::mem::TLB_INDEX_MASK] = {
            fiberish::mem::PackMicroTlbTags(tag, fiberish::mem::kInvalidMicroTlbTag),
            reinterpret_cast<uintptr_t>(backing.data.data()) - Base,
        };
        memory.data_tlb = &tlb;
        State state;
        state.pc = Start;
        state.x[3] = 7;
        state.v[3] = {11, 13};
        if (Run(state, memory, 2).reason != Stop::Syscall || backing.allocations != 1 ||
            backing.blocks[0]->FirstOp()->Has(HasDataSlot) || backing.sizes[0] != 80)
            return false;
        const auto lo = shape.width == 32 ? uint32_t(Lo) : Lo;
        if (shape.vector) {
            if (state.v[3].lo != lo || state.v[3].hi != (shape.width == 128 ? Hi : 0) || state.x[3] != 7)
                return false;
        } else {
            const auto expected =
                shape.operation == Prefetch ? 7 : (shape.operation == LoadSigned64 ? SignExtend(lo, 32) : lo);
            if (state.x[3] != expected || state.v[3].lo != 11 || state.v[3].hi != 13)
                return false;
        }
    }
    return true;
}

bool CompactLogicalImmediates() {
    using namespace detail;
    constexpr uint32_t WKey = 0x32000022; // orr w2, w1, #imm
    constexpr uint32_t XKey = 0xb2000043; // orr x3, x2, #imm
    constexpr uint32_t W = WKey | (1 << 16);
    constexpr uint32_t XPositive = XKey | (1 << 22) | (28 << 16);
    constexpr uint32_t XNegative = XKey | (1 << 22) | (61 << 16) | (60 << 10);
    constexpr uint32_t XOutside = XKey | (1 << 22) | (1 << 16);
    constexpr uint32_t Add = 0x91000464; // add x4, x3, #1
    constexpr uint32_t Svc = 0xd4000001;

    const Profile exact{.resolve = [](const DecodedInstruction& op, bool) -> HandlerFunc {
        switch (op.word) {
        case W:
            return GetBakedHandler<W>();
        case XPositive:
            return GetBakedHandler<XPositive>();
        case XNegative:
            return GetBakedHandler<XNegative>();
        case XOutside:
            return GetBakedHandler<XOutside>();
        default:
            return nullptr;
        }
    }};
    const Profile exact_pair{.resolve_pair = [](uint32_t first, uint32_t second, uint32_t dead) -> HandlerFunc {
        if (first != W)
            return nullptr;
        switch (second) {
        case XPositive:
            return BindBakedFusion<W, XPositive>(dead);
        case XNegative:
            return BindBakedFusion<W, XNegative>(dead);
        case XOutside:
            return BindBakedFusion<W, XOutside>(dead);
        default:
            return nullptr;
        }
    }};
    const Profile partial{.resolve = [](const DecodedInstruction& op, bool) -> HandlerFunc {
        const auto key = op.word & ~PartialImmediateMask(op);
        if (key == WKey)
            return GetBakedHandler<WKey, true>();
        if (key == XKey)
            return GetBakedHandler<XKey, true>();
        return nullptr;
    }};
    const Profile pair{.resolve_partial_pair = [](const DecodedInstruction& first, const DecodedInstruction& second,
                                                  uint32_t dead) -> HandlerFunc {
        if ((first.word & ~PartialImmediateMask(first)) != WKey ||
            (second.word & ~PartialImmediateMask(second)) != XKey)
            return nullptr;
        return PartialBakedFusion<3, WKey, XKey>.Select(dead, 2);
    }};
    const Profile sequence{
        .resolve_sequence = [](const uint32_t* words, unsigned count, unsigned* length, uint32_t dead) -> HandlerFunc {
            if (count < 3 || (words[0] & ~(0x1fffu << 10)) != WKey || (words[1] & ~(0x1fffu << 10)) != XKey ||
                words[2] != Add)
                return nullptr;
            *length = 3;
            return PartialBakedFusion<3, WKey, XKey, Add>.Select(dead, 3);
        }};
    const std::array cases{std::pair{XPositive, uint64_t{1} << 36}, std::pair{XNegative, ~uint64_t{7}},
                           std::pair{XOutside, uint64_t{1} << 63}};
    for (const auto& [word, mask] : cases)
        for (const auto& profile : {Profile{}, exact, exact_pair, partial, pair, sequence}) {
            CachedBacking backing;
            backing.Code(Base, {W, word, Add, Svc});
            State state;
            state.pc = Base;
            state.x[1] = 0xabcd'1234'0000'0006;
            const auto result = Run(state, backing.Callbacks(), 4, profile);
            const uint64_t w = 0x80000006;
            if (result.reason != Stop::Syscall || result.executed != 4 || state.x[2] != w || state.x[3] != (w | mask) ||
                state.x[4] != (w | mask) + 1 || backing.allocations != 1)
                return false;

            const auto* first = backing.blocks[0]->FirstOp();
            const auto* second = first->NextOp();
            const bool wide = word == XOutside;
            if (first->Has(HasDataSlot) || second->Has(HasDataSlot) != wide || second->GuestIndex() != 1 ||
                GetOpOperands<Kind::LogicalImmediate>(*second).immediate != mask ||
                second->NextOp()->GuestIndex() != 2 || backing.sizes[0] != 112 + 16 * wide ||
                first->Span() != (profile.resolve_sequence                                 ? 3
                                  : (profile.resolve_pair || profile.resolve_partial_pair) ? 2
                                                                                           : 1))
                return false;
        }
    return true;
}

bool LogicalImmediateSlotRoundTrip() {
    using namespace detail;
    for (unsigned sf = 0; sf < 2; ++sf)
        for (unsigned encoding = 0; encoding < (1u << 13); ++encoding) {
            const auto op = DecodeFields(0x32000022 | (sf << 31) | (encoding << 10));
            if (op.kind != Kind::LogicalImmediate)
                continue;
            const auto immediate = std::bit_cast<int64_t>(op.immediate);
            const bool wide = sf && (immediate < -(int64_t{1} << 37) || immediate >= (int64_t{1} << 37));
            const auto flags = uint8_t(wide ? HasDataSlot : 0);
            const DecodedOpWithDataSlot slot{
                {.payload = DecodedOp::Pack(PackOpOperands(op), 31, flags, MaxBakedFusionLength, 0)},
                wide ? op.immediate : ~op.immediate,
            };
            const auto decoded = GetOpOperands<Kind::LogicalImmediate>(slot.head);
            if (HasExtraOperandSlot(op) != wide || decoded.immediate != op.immediate || decoded.width != op.width ||
                decoded.operation != op.operation || decoded.rd != op.rd || decoded.rn != op.rn)
                return false;
        }
    return true;
}

bool MemoryFaultAfterExecutableUnmap() {
    constexpr uint32_t Load = 0xf9400020; // ldr x0, [x1]
    constexpr uint64_t FaultAddress = Base + 2 * Page;
    CachedBacking backing;
    backing.Code(Base, {Load, 0x14000001}); // b .+4
    auto memory = backing.Callbacks();
    memory.read = [](void* context, uint64_t address, void*, size_t, Access access) {
        auto& backing = *static_cast<CachedBacking*>(static_cast<Backing*>(context));
        if (access == Access::Read && address == FaultAddress && backing.blocks.front()) {
            backing.blocks.front()->Invalidate();
            backing.executable.front() = false;
            backing.Put(Base, detail::NopWord);
        }
        return false;
    };
    State state{};
    state.pc = Base;
    state.x[1] = FaultAddress;
    const auto result = Run(state, memory, 2);
    return result.reason == Stop::Fault && result.executed == 0 && result.address == FaultAddress &&
           result.access == Access::Read && result.instruction == Load && state.pc == Base &&
           backing.allocations == 1 && !backing.executable.front() && !backing.blocks.front()->IsValid();
}

bool RefetchChangedFoldedNop() {
    Backing backing;
    backing.Code(Base, {
                           0x91000421, // add x1, x1, #1
                           0xd503201f, // nop, replaced by the first load callback
                           0xf9400040, // ldr x0, [x2]
                           0xf1000463, // subs x3, x3, #1
                           0x54ffff81, // b.ne Base
                           0xd4000001, // svc #0
                       });
    backing.replace_after_read = Base + 4;
    State state;
    state.pc = Base;
    state.x[2] = Base + Page;
    state.x[3] = 2;
    const auto result = Run(state, backing.Callbacks(), 11);
    return result.reason == Stop::Syscall && result.executed == 11 && result.address == Base + 20 &&
           state.pc == Base + 24 && state.x[1] == 5;
}

} // namespace

int main() {
    const struct {
        const char* name;
        bool result;
    } tests[] = {
        {"integer, branches and traps through callbacks", CoreAndTraps(false)},
        {"integer, branches and traps through executable pages", CoreAndTraps(true)},
        {"precise store, fetch and alignment faults", MemoryFaults()},
        {"64-bit pair addressing and writeback through callbacks", PairAddressingModes(false)},
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        {"64-bit pair addressing and writeback on TLB hits", PairAddressingModes(true)},
        {"overlapping general-purpose transfers trap at decode", MemoryOverlapAtDecode()},
#endif
        {"exclusive monitor and atomic operations", ExclusivesAndAtomics()},
        {"direct SIMD, FP and system handlers keep live flags and pinned GPRs", RegisterOpsKeepDispatchState()},
        {"direct vector/atomic/cache-zero transfers and exclusive store replay", DirectTlbTransfers()},
        {"VA39 high page, TLB alias and upper data/fetch limits", Va39DataBounds()},
        {"scalar FP and SIMD structure transfers", FloatingAndVector()},
        {"SIMD lane order, saturation, rounding and table index boundaries", VectorLaneBoundaries()},
        {"cached instructions after memory callbacks", CodeCoherence()},
        {"dispatch across many blocks and executable pages", DispatchAcrossPages()},
        {"direct branch links compare all 39 PC bits", Va39BlockLink()},
        {"linked successor sees a replaced cache entry", ReplacedSuccessorUsesCurrentProfile()},
        {"truncated blocks and fusions at every budget", TruncatedBlocks()},
        {"baked dispatch and profiling", BakedDispatch()},
        {"PC-relative baking keys preserve decoded operands and reserved encodings", CanonicalBakedWords()},
        {"partial immediate shapes retain the decoded arithmetic value and scaled offset", PartialImmediateOperands()},
        {"partial pair, indexed, move-wide and logical immediates", BroaderPartialImmediateOperands()},
        {"partial pairs use decoded targets, immediates and fault PCs", PartialImmediatePairs()},
        {"fused dispatch branch, budget and fault accounting", FusedDispatch()},
        {"standalone conditional branches test all NZCV combinations", StandaloneConditionBranch()},
        {"compare and conditional branch preserve flags and budget boundaries", CompareBranchPair()},
        {"baked compare branch suffix preserves flags at a successor fault", BakedCompareBranchSuffix()},
        {"SP remains distinct from XZR across threaded writes", StackPointerAndZeroRegisterRoles()},
        {"decoded pin operands and compare-branch handler selection", PinnedOperandClassification()},
        {"generic BL and BLR publish their implicit link register writes", GenericLinkRegisterWrites()},
#if FIBERCPU_AARCH64_TAIL_DISPATCH
        {"pin-free dispatch preserves live pins on TLB hits, scalar replay and block exits",
         PinFreeDispatchKeepsLivePins()},
#endif
        {"fused PC-relative address, link and conditional branch use each member PC", FusedPCRelativeMembers()},
        {"fused load fault and instruction refetch", FusedLoadRefetch()},
        {"fused load replay preserves the prefix and commits writeback once", FusedLoadReplay()},
        {"fused store replay exposes the prefix once and refetches", FusedStoreReplay()},
        {"store promotes a shared read/write micro-TLB entry", FusedStorePromotesReadWriteMicroTlb()},
        {"literal addresses bind to the member in hot and bounded dispatch", BoundLiteralAddress()},
        {"NZCV at every instruction budget, including ADCS input carry", FlagElisionBudgets()},
        {"NZCV at a conditional branch and for reused instruction words", FlagElisionBranchAndCache()},
        {"NZCV visible to faults, memory callbacks, SVC and undefined instructions", FlagElisionEvents()},
        {"FCMP FPSR effect when its NZCV is overwritten", FlagElisionFloatingCompare()},
        {"compact bitfield preserves the six-bit endpoint", CompactBitfieldOperands()},
        {"all valid compact bitfield encodings match the architectural bit masks", ExhaustiveCompactBitfieldOperands()},
        {"compact slots, NOP folding, precise guest budgets and branch entry", CompactSlotsAndFoldedNopBudgets()},
        {"compact generic, baked and partially baked operands before a sentinel-free BL", CompactOperandDispatch()},
        {"decoded slot metadata preserves maximal operands, ordinals, spans and branch suffixes",
         CompactSlotMetadata()},
        {"compact ADR and ADRP preserve results outside the guest address space", CompactAddressPreservesFullTarget()},
        {"all literal loads preserve their transfer type and signed target", CompactLiteralOperands()},
        {"compact logical immediates agree across generic, baked, partial and fused dispatch",
         CompactLogicalImmediates()},
        {"every valid logical immediate round-trips through its compact or wide slot", LogicalImmediateSlotRoundTrip()},
        {"memory fault reports the original instruction after executable unmap", MemoryFaultAfterExecutableUnmap()},
        {"local cache refetches a modified folded NOP", RefetchChangedFoldedNop()},
    };
    for (const auto& test : tests) {
        if (!test.result) {
            std::fprintf(stderr, "AArch64 test failed: %s\n", test.name);
            return 1;
        }
    }
    return 0;
}
