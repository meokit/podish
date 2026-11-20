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
BoundScalarHandler BindScalarHandler(const DecodedInstruction& op);
HandlerFunc CanonicalBoundHandler(HandlerFunc handler);
HandlerFunc FindGenericInstructionHandler(uint16_t opcode, const DecodedInstruction* op);
uint32_t FindScalarTrainingId(HandlerFunc handler);
uint32_t TrainingOperationIdAt(uint32_t index);
const char* FindTrainingOperationName(uint32_t id);
const char* InstructionCatalogHash();
const std::vector<X86_InstructionVariant>& InstructionVariantCatalog();
void RegisterInstructionVariant(uint16_t opcode, SpecCriteria criteria, HandlerFunc handler, const char* logic,
                                const char* family, const char* parent, bool bake, int rank);
void RegisterInstructionFallback(uint16_t opcode, HandlerFunc handler, const char* logic);

struct DeclaredInstructionVariant {
    uint16_t opcode;
    SpecCriteria criteria;
    HandlerFunc handler;
    const char* logic;
    const char* family;
    const char* parent;
    bool bake;
    int rank;
};

template <class Family, spec::Spec S>
consteval DeclaredInstructionVariant DeclareVariant(HandlerFunc handler, const char* name, const char* family,
                                                    const char* parent, bool bake, int rank) {
    static_assert(spec::Valid<Family>(S), "Invalid catalog specialization");
    return {Family::opcode, spec::Criteria<Family>(S), handler, name, family, parent, bake, rank};
}

}  // namespace fiberish
