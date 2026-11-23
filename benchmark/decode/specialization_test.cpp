#include "bindings.h"
#include "operand_specialization.h"
#include "specialization.h"
#include <cstdio>
#include <random>

namespace fiberish {
HandlerFunc FindSpecializedHandlerReference(uint16_t, DecodedInstruction*);
bool MatchesCompiledForTest(const SpecCriteria&, const DecodedInstruction*);
bool ValidateSpecializationWildcardIndex();
#ifdef FIBERCPU_TEST_GENERATED_SUPEROPCODES
bool ValidateGeneratedSequenceLookup();
#endif
#ifdef FIBERCPU_DYNAMIC_OPERANDS
bool ValidateDynamicOperandSpecializations();
#endif
} // namespace fiberish

static bool ValidateProfileOperands() {
    using namespace fiberish;
    DecodedInstruction ops[2]{};
    ops[0].handler = g_Handlers[0x81];
    ops[1].handler = g_Handlers[0x83];
    ops[0].len = 8;
    ops[0].modrm = 0x44;
    ops[0].prefixes.all = 0x64;
    ops[0].meta.all = 7;
    ops[0].ext.data = {0x10203040, memdesc::PackEA(4, 0, 2, 4), 0xffffff80, 0};
    ops[1].len = 3;
    ops[1].modrm = 0xc0;
    ops[1].meta.all = 5;
    ops[1].ext.data.imm = 7;
    const auto first = DecodedOperandShape::Read(ops);
    const auto second = DecodedOperandShape::Read(ops + 1);
    auto different_immediate = first;
    different_immediate.immediate++;
    const auto exact = g_Handlers[0x05];
    const auto alternate = g_Handlers[0x0d];
    const ProfileOperandShapeEntry entries[] = {
        {ops[0].handler, exact, {first, second}, 2},
        {ops[0].handler, alternate, {different_immediate, second}, 2},
    };
    const OperandIndex index(entries);
    if (index.Find(ops, ops->handler) != exact)
        return false;
    ops[0].meta.flags.operand_lowered = 1;
    ops[0].next_eip = 0x10005000;
    if (index.Find(ops, ops->handler) != exact)
        return false;
    ops[0].ext.data.imm++;
    if (index.Find(ops, ops->handler) != alternate)
        return false;
    ops[1].ext.data.imm++;
    if (index.Find(ops, ops->handler) != nullptr)
        return false;
    ops[1].ext.data.imm--;

    const auto original = ops[0];
    const auto rejects = [&](auto mutate) {
        ops[0] = original;
        mutate(ops[0]);
        return index.Find(ops, ops->handler) == nullptr;
    };
    return rejects([](auto& op) { op.ext.data.disp++; }) && rejects([](auto& op) { op.ext.data.ea_desc++; }) &&
           rejects([](auto& op) { op.prefixes.all ^= 0x40; }) && rejects([](auto& op) { op.modrm ^= 1; }) &&
           rejects([](auto& op) { op.len++; }) && rejects([](auto& op) { op.meta.flags.no_flags = 1; });
}

int main() {
#ifdef FIBERCPU_DYNAMIC_OPERANDS
    if (!fiberish::ValidateDynamicOperandSpecializations()) {
        std::fprintf(stderr, "Dynamic operand handler lookup, publication or fallback mismatch\n");
        return 1;
    }
    // CPUID uses relocated constant-pool loads in the macOS arm64 stencil.
    auto* state = X86_CreateWithOptions(X86_CREATE_NO_FATAL_SIGNAL_HANDLERS);
    constexpr uint8_t cpuid[] = {0x0f, 0xa2};
    constexpr uint32_t address = 0x10000;
    uint8_t page[4096]{};
    if (!X86_MemMapHostPage(state, address, page, 7)) {
        X86_Destroy(state);
        return 1;
    }
    X86_MemWrite(state, address, cpuid, sizeof(cpuid));
    X86_SetEIP(state, address);
    X86_Run(state, address + sizeof(cpuid), 8);
    const bool vendor_matches = X86_RegRead(state, 0) == 7 && X86_RegRead(state, 1) == 0x6c65746e &&
                                X86_RegRead(state, 2) == 0x49656e69 && X86_RegRead(state, 3) == 0x756e6547;
    X86_Destroy(state);
    if (!vendor_matches) {
        std::fprintf(stderr, "CPUID constant pool relocation mismatch\n");
        return 1;
    }
#endif
    if (!ValidateProfileOperands()) {
        std::fprintf(stderr, "Full operand specialization index mismatch\n");
        return 1;
    }
    if (!fiberish::ValidateSpecializationWildcardIndex()) {
        std::fprintf(stderr, "Wildcard liveness/memory specialization index mismatch\n");
        return 1;
    }
    std::mt19937 rng(12345);
    for (unsigned opcode = 0; opcode < 1024; ++opcode) {
        for (unsigned encoded = 0; encoded <= 256; ++encoded) {
            for (unsigned flags = 0; flags < 2; ++flags) {
                for (unsigned sample = 0; sample < 8; ++sample) {
                    fiberish::DecodedInstruction op{};
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
        fiberish::DecodedInstruction op{};
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
#ifdef FIBERCPU_TEST_GENERATED_SUPEROPCODES
    if (!fiberish::ValidateGeneratedSequenceLookup()) {
        std::fprintf(stderr, "Generated superopcode lookup mismatch\n");
        return 1;
    }
#endif
    using namespace fiberish;
    DecodedInstruction runtime_op{};
    auto fallback = g_Handlers[0x90];
    auto registered = g_Handlers[0x05];
    RegisterSpecializedHandler(0x90, SpecCriteria{.prefix_mask = 0xff, .prefix_val = 0x40}, registered, 0);
    if (FindSpecializedHandler(0x90, &runtime_op) == registered)
        return 1;
    runtime_op.prefixes.all = 0x40;
    if (!fallback || FindSpecializedHandler(0x90, &runtime_op) != registered)
        return 1;
    std::puts("Specialization and generated superopcode lookups match reference");
}
