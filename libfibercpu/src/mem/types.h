#pragma once
#include <bit>
#include <cstddef>
#include <cstdint>
#include <expected>

namespace fiberish::mem {

#ifdef PAGE_SIZE
#undef PAGE_SIZE
#endif

#ifdef PAGE_MASK
#undef PAGE_MASK
#endif

enum class FaultCode : uint8_t {
    None = 0,
    PageFault = 14,
    GeneralProtection = 13,
    InvalidOpcode = 6,
};

template <typename T>
using MemResult = std::expected<T, FaultCode>;

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
    ForceWriteSlow = 1 << 5,
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

constexpr Property operator&(Property lhs, Property rhs) {
    return static_cast<Property>(static_cast<uint32_t>(lhs) & static_cast<uint32_t>(rhs));
}

constexpr Property operator~(Property v) { return static_cast<Property>(~static_cast<uint32_t>(v)); }

constexpr bool has_property(Property target, Property check) {
    return (static_cast<uint32_t>(target) & static_cast<uint32_t>(check)) == static_cast<uint32_t>(check);
}

// Keep has_perm as an alias
constexpr bool has_perm(Property target, Property check) { return has_property(target, check); }
}  // namespace fiberish::mem
