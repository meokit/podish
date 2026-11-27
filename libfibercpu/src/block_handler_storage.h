#pragma once

#include <cstddef>
#include <cstdint>

namespace fiberish {

using BlockHandlerIndex = uint16_t;

enum class BlockHandlerStorage : uint8_t {
    None = 0,
    Indexed = sizeof(BlockHandlerIndex),
    Pointers = sizeof(void*),
};

template <typename Handler>
constexpr size_t BlockHandlerWidth(BlockHandlerStorage storage) {
    static_assert(sizeof(Handler) == sizeof(void*));
    return static_cast<uint8_t>(storage);
}

}  // namespace fiberish
