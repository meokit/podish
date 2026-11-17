#include "specialization.h"
#include <cstdio>
#include <random>

namespace fiberish {
HandlerFunc FindSpecializedHandlerReference(uint16_t, DecodedOp*);
}

int main() {
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
    std::puts("Specialization index matches linear lookup");
}
