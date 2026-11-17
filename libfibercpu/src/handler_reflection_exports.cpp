#include "bindings.h"
#include "decoder.h"
#include "instruction_variants.h"

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

int32_t X86_GetHandlerCount() { return static_cast<int32_t>(fiberish::generated::handler_reflection::HandlerCount()); }

int32_t X86_GetHandlerId(void* handler) {
    return fiberish::generated::handler_reflection::IdForHandler(reinterpret_cast<fiberish::HandlerFunc>(handler));
}

void* X86_GetHandlerById(int32_t handler_id) {
    if (handler_id < 0) return nullptr;
    return reinterpret_cast<void*>(
        fiberish::generated::handler_reflection::HandlerForId(static_cast<uint32_t>(handler_id)));
}

const char* X86_GetHandlerSymbolById(int32_t handler_id) {
    if (handler_id < 0) return nullptr;
    return fiberish::generated::handler_reflection::NameForId(static_cast<uint32_t>(handler_id));
}

}  // extern "C"
