#pragma once

#include <vector>
#include "bindings.h"
#include "dispatch.h"
#include "instruction_families.h"

namespace fiberish {

struct BoundScalarHandler {
    HandlerFunc handler;
    uint32_t id;
    BoundOperandShape shape;
};

void RegisterInstructionVariants();
struct OpcodeBinding {
    ScalarBindingIndex fallback = ScalarBindingIndex::Unknown;
    bool has_variants = false;
};
extern const std::array<OpcodeBinding, 1024> kOpcodeBindings;
BoundScalarHandler BindScalarHandler(const DecodedInstruction& op,
                                     ScalarBindingIndex index = ScalarBindingIndex::Unknown);
HandlerFunc CanonicalBoundHandler(HandlerFunc handler);
HandlerFunc FindGenericInstructionHandler(uint16_t opcode, const DecodedInstruction* op);
uint32_t FindScalarTrainingId(HandlerFunc handler);
HandlerFunc ScalarBindingHandler(ScalarBindingIndex index);
uint32_t TrainingOperationIdAt(uint32_t index);
const char* FindTrainingOperationName(uint32_t id);
const char* InstructionCatalogHash();
const std::vector<X86_InstructionVariant>& InstructionVariantCatalog();
struct DeclaredInstructionVariant {
    uint16_t opcode;
    SpecCriteria criteria;
    HandlerFunc handler;
    const char* logic;
    const char* family;
    const char* parent;
    bool bake;
    int rank;
    ScalarBindingIndex binding;
};

template <class Family, spec::Spec S>
consteval DeclaredInstructionVariant DeclareVariant(HandlerFunc handler, const char* name, const char* family,
                                                    const char* parent, bool bake, int rank,
                                                    ScalarBindingIndex binding) {
    static_assert(spec::Valid<Family>(S), "Invalid catalog specialization");
    return {Family::opcode, spec::Criteria<Family>(S), handler, name, family, parent, bake, rank, binding};
}

}  // namespace fiberish
