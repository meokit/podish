#include "bindings.h"
#include "decoder.h"
#include "instruction_variants.h"
#if FIBERCPU_DYNAMIC_OPERANDS
#include "dynamic_operand_specialization.h"
#endif

namespace fiberish::generated::handler_reflection {
size_t HandlerCount();
HandlerFunc HandlerForId(uint32_t id);
int32_t IdForHandler(HandlerFunc handler);
const char* NameForId(uint32_t id);
}  // namespace fiberish::generated::handler_reflection

extern "C" {
const char* X86_GetInstructionCatalogHash() { return fiberish::InstructionCatalogHash(); }
int32_t X86_GetInstructionVariantCount() { return static_cast<int32_t>(fiberish::InstructionVariantCatalog().size()); }
int32_t X86_GetInstructionVariant(int32_t index, X86_InstructionVariant* result) {
    const auto& catalog = fiberish::InstructionVariantCatalog();
    if (!result || index < 0 || static_cast<size_t>(index) >= catalog.size()) return 0;
    *result = catalog[index];
    return 1;
}

int32_t X86_GetHandlerCount() {
    size_t count = fiberish::generated::handler_reflection::HandlerCount();
#if FIBERCPU_DYNAMIC_OPERANDS
    count += fiberish::DynamicOperandCount();
#endif
    return static_cast<int32_t>(count);
}

int32_t X86_GetHandlerId(void* handler) {
    const auto fn = reinterpret_cast<fiberish::HandlerFunc>(handler);
    const auto id = fiberish::generated::handler_reflection::IdForHandler(fn);
#if FIBERCPU_DYNAMIC_OPERANDS
    if (id < 0) {
        const auto dynamic_id = fiberish::DynamicOperandId(fn);
        if (dynamic_id >= 0)
            return dynamic_id + static_cast<int32_t>(fiberish::generated::handler_reflection::HandlerCount());
    }
#endif
    if (id >= 0) return id;
    return fiberish::generated::handler_reflection::IdForHandler(fiberish::CanonicalBoundHandler(fn));
}

void* X86_GetHandlerById(int32_t handler_id) {
    if (handler_id < 0) return nullptr;
#if FIBERCPU_DYNAMIC_OPERANDS
    const auto count = fiberish::generated::handler_reflection::HandlerCount();
    if (static_cast<uint32_t>(handler_id) >= count)
        return reinterpret_cast<void*>(fiberish::DynamicOperandForId(static_cast<uint32_t>(handler_id) - count));
#endif
    return reinterpret_cast<void*>(
        fiberish::generated::handler_reflection::HandlerForId(static_cast<uint32_t>(handler_id)));
}

const char* X86_GetHandlerSymbolById(int32_t handler_id) {
    if (handler_id < 0) return nullptr;
#if FIBERCPU_DYNAMIC_OPERANDS
    const auto count = fiberish::generated::handler_reflection::HandlerCount();
    if (static_cast<uint32_t>(handler_id) >= count)
        return fiberish::DynamicOperandName(static_cast<uint32_t>(handler_id) - count);
#endif
    return fiberish::generated::handler_reflection::NameForId(static_cast<uint32_t>(handler_id));
}

}  // extern "C"
