#pragma once
#include <bit>
#include <cstddef>
#include <cstdint>

namespace fiberish::mem {

using GuestAddr = uint32_t;
using HostAddr = std::byte*;

// Constants
constexpr size_t PAGE_SHIFT = 12;
constexpr size_t PAGE_SIZE = 1 << PAGE_SHIFT;  // 4096
constexpr size_t PAGE_MASK = PAGE_SIZE - 1;

// Permission and Property Bits
enum class Property : uint32_t {
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,
    Exec = 1 << 2,
    Dirty = 1 << 3,
    External = 1 << 4,  // Page memory is not owned by MMU (for mmap passthrough)
    Valid = 1 << 11,
    // Combinations
    RW = Read | Write,
    RX = Read | Exec,
    RWX = Read | Write | Exec
};

// Alias for backward compatibility if needed, though we prefer Property
using Perm = Property;

constexpr Property operator|(Property lhs, Property rhs) {
    return static_cast<Property>(static_cast<uint32_t>(lhs) | static_cast<uint32_t>(rhs));
}

constexpr bool has_property(Property target, Property check) {
    return (static_cast<uint32_t>(target) & static_cast<uint32_t>(check)) == static_cast<uint32_t>(check);
}

// Keep has_perm as an alias
constexpr bool has_perm(Property target, Property check) { return has_property(target, check); }
}  // namespace fiberish::mem
