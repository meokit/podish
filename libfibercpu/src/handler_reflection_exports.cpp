#include "bindings.h"
#include "decoder.h"

namespace fiberish::generated::handler_reflection {
size_t HandlerCount();
HandlerFunc HandlerForId(uint32_t id);
int32_t IdForHandler(HandlerFunc handler);
const char* NameForId(uint32_t id);
}  // namespace fiberish::generated::handler_reflection

extern "C" {

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
