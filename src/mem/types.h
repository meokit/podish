#pragma once
#include <cstdint>
#include <cstddef>
#include <bit>

namespace x86emu::mem {

    using GuestAddr = uint32_t;
    using HostAddr  = std::byte*;

    // Constants
    constexpr size_t PAGE_SHIFT = 12;
    constexpr size_t PAGE_SIZE  = 1 << PAGE_SHIFT; // 4096
    constexpr size_t PAGE_MASK  = PAGE_SIZE - 1;

    // Permission Bits
    enum class Perm : uint8_t {
        None  = 0,
        Read  = 1 << 0,
        Write = 1 << 1,
        Exec  = 1 << 2,
        // Combinations
        RW    = Read | Write,
        RX    = Read | Exec,
        RWX   = Read | Write | Exec
    };

    constexpr Perm operator|(Perm lhs, Perm rhs) {
        return static_cast<Perm>(static_cast<uint8_t>(lhs) | static_cast<uint8_t>(rhs));
    }
    
    constexpr bool has_perm(Perm target, Perm check) {
        return (static_cast<uint8_t>(target) & static_cast<uint8_t>(check)) == static_cast<uint8_t>(check);
    }
}
