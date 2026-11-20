#include "decoder.h"

#include <bit>
#include <cstdio>

using namespace fiberish;

struct RuntimeOp24;
using CompactHandler = int64_t(ATTR_PRESERVE_NONE*)(EmuState*, RuntimeOp24*, int64_t, mem::MicroTlbAbiWord,
                                                    mem::MicroTlbAbiWord, uint32_t, uint64_t FIBERCPU_PIN_PARAMS);

// The mandatory operand policy supplies semantic prefix and metadata bits.
// A direct conditional branch uses immediate for its relative displacement;
// its cached target pointer is in the owning block's side state.
struct RuntimeOp24 {
    CompactHandler handler;
    uint32_t next_eip;
    uint8_t len;
    uint8_t modrm;
    uint16_t address;
    uint32_t immediate;
    uint32_t displacement;
};

static_assert(sizeof(DecodedOp) == 32 && alignof(DecodedOp) == 16);
static_assert(sizeof(RuntimeOp24) == 24 && alignof(RuntimeOp24) == 8);

constexpr uint16_t PackAddress(uint32_t ea) {
    return uint16_t((memdesc::BaseOffset(ea) / sizeof(uint32_t)) |
                    ((memdesc::IndexOffset(ea) / sizeof(uint32_t)) << 4) | (memdesc::Scale(ea) << 8) |
                    (memdesc::Segment(ea) << 10) | (memdesc::ResolvedSegment(ea) << 13));
}

constexpr uint32_t ExpandAddress(uint16_t packed) {
    return (((packed & 15) * sizeof(uint32_t)) << memdesc::kBaseShift) |
           ((((packed >> 4) & 15) * sizeof(uint32_t)) << memdesc::kIndexShift) |
           (((packed >> 8) & 3) << memdesc::kScaleShift) | (((packed >> 10) & 7) << memdesc::kSegmentShift) |
           (((packed >> 13) & 7) << memdesc::kResolvedSegmentShift);
}

extern "C" ATTR_PRESERVE_NONE int64_t Dispatch32(EmuState* state, DecodedOp* op, int64_t limit,
                                                 mem::MicroTlbAbiWord tags, mem::MicroTlbAbiWord addend,
                                                 uint32_t branch, uint64_t flags FIBERCPU_PIN_PARAMS) {
    auto* next = op + 1;
    ATTR_MUSTTAIL return next->handler(state, next, limit, tags, addend, branch, flags FIBERCPU_PIN_ARGS);
}

extern "C" ATTR_PRESERVE_NONE int64_t Dispatch24(EmuState* state, RuntimeOp24* op, int64_t limit,
                                                 mem::MicroTlbAbiWord tags, mem::MicroTlbAbiWord addend,
                                                 uint32_t branch, uint64_t flags FIBERCPU_PIN_PARAMS) {
    auto* next = op + 1;
    ATTR_MUSTTAIL return next->handler(state, next, limit, tags, addend, branch, flags FIBERCPU_PIN_ARGS);
}

// Slot rank in a sparse per-block branch-cache table. The mask is empty for
// blocks without a cached branch. Synthetic Step supplies its own context.
extern "C" size_t EdgeIndex32(const DecodedOp* first, const DecodedOp* op, uint64_t mask) {
    const auto slot = op - first;
    return std::popcount(mask & ((uint64_t{1} << slot) - 1));
}

extern "C" size_t EdgeIndex24(const RuntimeOp24* first, const RuntimeOp24* op, uint64_t mask) {
    const auto slot = op - first;
    return std::popcount(mask & ((uint64_t{1} << slot) - 1));
}

int main() {
    unsigned address_count = 0;
    for (unsigned base = 0; base <= kNoRegOffset; base += sizeof(uint32_t)) {
        for (unsigned index = 0; index <= kNoRegOffset; index += sizeof(uint32_t)) {
            for (uint8_t scale = 0; scale < 4; ++scale) {
                for (uint8_t segment = 0; segment <= 6; ++segment) {
                    const auto ea = memdesc::PackEA(base, index, scale, segment);
                    if (ExpandAddress(PackAddress(ea)) != ea)
                        return 1;
                    ++address_count;
                }
            }
        }
    }
    RuntimeOp24 compact[64]{};
    DecodedOp original[64]{};
    const uint64_t mask = 0x8000000000000015;
    for (unsigned i = 0; i < 64; ++i)
        if (EdgeIndex24(compact, compact + i, mask) != EdgeIndex32(original, original + i, mask))
            return 1;
    std::printf("DecodedOp=%zu/%zu RuntimeOp24=%zu/%zu BasicBlockHeader=%zu RoundTrips=%u\n", sizeof(DecodedOp),
                alignof(DecodedOp), sizeof(RuntimeOp24), alignof(RuntimeOp24), offsetof(BasicBlock, slots),
                address_count);
}
