#include "specialization.h"
#include <cstdio>
#include <random>

namespace fiberish {
HandlerFunc FindSpecializedHandlerReference(uint16_t, DecodedOp*);
bool MatchesCompiledForTest(const SpecCriteria&, const DecodedOp*);
bool ValidateSpecializationWildcardIndex();
namespace handwritten_superopcodes {
bool ValidateHandwrittenSuperOpcodes();
}
#ifdef FIBERCPU_TEST_GENERATED_SUPEROPCODES
bool ValidateGeneratedSequenceLookup();
#endif
} // namespace fiberish

int main() {
    if (!fiberish::ValidateSpecializationWildcardIndex()) {
        std::fprintf(stderr, "Wildcard liveness/memory specialization index mismatch\n");
        return 1;
    }
    std::mt19937 rng(12345);
    for (unsigned opcode = 0; opcode < 1024; ++opcode) {
        for (unsigned encoded = 0; encoded <= 256; ++encoded) {
            for (unsigned flags = 0; flags < 2; ++flags) {
                for (unsigned sample = 0; sample < 8; ++sample) {
                    fiberish::DecodedOp op{};
                    op.meta.flags.has_modrm = encoded != 0;
                    op.modrm = encoded ? encoded - 1 : 0;
                    op.meta.flags.no_flags = flags;
                    op.meta.flags.has_mem = sample & 1;
                    op.prefixes.all = static_cast<uint8_t>(rng());
                    fiberish::GetExt(&op)->data.ea_desc = rng();
                    auto actual = fiberish::FindSpecializedHandler(opcode, &op);
                    auto expected = fiberish::FindSpecializedHandlerReference(opcode, &op);
                    if (actual != expected) {
                        std::fprintf(stderr, "Mismatch opcode=%u modrm=%u flags=%u sample=%u\n", opcode, encoded, flags,
                                     sample);
                        return 1;
                    }
                }
            }
        }
    }
    // Exercise arbitrary masks and values, including out-of-range bits,
    // unconstrained fields with nonzero values, and matching combinations.
    for (unsigned i = 0; i < 1000000; ++i) {
        fiberish::DecodedOp op{};
        op.meta.flags.has_mem = rng() & 1;
        op.prefixes.all = rng();
        auto* ext = fiberish::GetExt(&op);
        ext->data.ea_desc = rng();
        fiberish::SpecCriteria c{};
        auto field = [&](uint8_t& mask, uint8_t& value, uint8_t actual) {
            mask = (rng() % 3 == 0) ? 0 : static_cast<uint8_t>(rng());
            value = (rng() & 1) ? static_cast<uint8_t>(actual & mask) : static_cast<uint8_t>(rng());
        };
        field(c.base_offset_mask, c.base_offset_val, fiberish::memdesc::BaseOffset(ext->data.ea_desc));
        field(c.index_offset_mask, c.index_offset_val, fiberish::memdesc::IndexOffset(ext->data.ea_desc));
        field(c.scale_mask, c.scale_val, fiberish::memdesc::Scale(ext->data.ea_desc));
        field(c.segment_mask, c.segment_val, fiberish::memdesc::Segment(ext->data.ea_desc));
        c.memory_only = rng() & 1;
        c.prefix_mask = rng() % 3 ? static_cast<uint8_t>(rng()) : 0;
        c.prefix_val = (rng() & 1) ? op.prefixes.all & c.prefix_mask : static_cast<uint8_t>(rng());
        if (fiberish::MatchesCompiledForTest(c, &op) != c.MatchesIndexedRemainder(&op)) {
            std::fprintf(stderr, "Compiled mask mismatch at sample %u\n", i);
            return 1;
        }
    }
    if (!fiberish::handwritten_superopcodes::ValidateHandwrittenSuperOpcodes()) {
        std::fprintf(stderr, "Handwritten superopcode lookup mismatch\n");
        return 1;
    }
#ifdef FIBERCPU_TEST_GENERATED_SUPEROPCODES
    if (!fiberish::ValidateGeneratedSequenceLookup()) {
        std::fprintf(stderr, "Generated superopcode lookup mismatch\n");
        return 1;
    }
#endif
    std::puts("Specialization and generated superopcode lookups match reference");
}
