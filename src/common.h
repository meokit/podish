#pragma once

#include <cstdint>

// Use SIMDe for cross-platform intrinsics (Arm64 -> x86 SSE/AVX emulation)
// This definition allows us to use _mm_add_ps etc. on non-x86 hardware.
#define SIMDE_ENABLE_NATIVE_ALIASES
#include <simde/x86/sse2.h>
#include "float80.h"
#include <thread>

#if defined(__x86_64__) || defined(_M_X64)
    #include <immintrin.h>
    #define CPU_RELAX() _mm_pause()
#elif defined(__arm64__) || defined(__aarch64__) || defined(_M_ARM64)
    #define CPU_RELAX() __asm__ __volatile__("yield")
#else
    #define CPU_RELAX() std::this_thread::yield()
#endif

namespace x86emu {

enum class EmuStatus {
    Running,
    Stopped,
    Fault,
    Yield
};

// General Purpose Registers (Index mapping)
enum Reg {
    EAX = 0,
    ECX = 1,
    EDX = 2,
    EBX = 3,
    ESP = 4,
    EBP = 5,
    ESI = 6,
    EDI = 7,
};

// Segment Registers
enum Seg {
    ES = 0,
    CS = 1,
    SS = 2,
    DS = 3,
    FS = 4,
    GS = 5,
};

// CPU Context
// Aligned to cache line (64 bytes) to avoid false sharing if we ever go multi-threaded (though this is single threaded)
struct alignas(64) Context {
    uint32_t regs[8];    // General Purpose Registers
    uint32_t eip;        // Instruction Pointer
    uint32_t eflags;     // EFLAGS Register
    uint32_t eflags_mask; // Mask for user-modifiable flags (1=modifiable)
    
    // SSE/SSE2 Registers
    alignas(16) simde__m128 xmm[8];
    uint32_t mxcsr;

// FPU Registers (80-bit Extended Precision)
    alignas(16) float80 fpu_regs[8];
    uint16_t fpu_sw = 0;
    uint16_t fpu_cw = 0; // Default to 0 to match Unicorn (was 0x037F)
    uint16_t fpu_tw = 0xFFFF; // Empty
    int fpu_top = 0;

    // Segment Base Addresses
    // For user-mode simulation:
    // CS, DS, SS, ES, are typically base 0 (Flat Model).
    // FS and GS are often used for TLS (Thread Local Storage).
    // The emulator allows the caller to set these bases.
    uint32_t seg_base[6]; 

    // System Environment (Pointers to Managers)
    // We store these here so handlers can access memory/hooks via the Context pointer.
    void* mmu;   // Type-erased or forward-declared SoftMMU*
    void* hooks; // Type-erased or forward-declared HookManager*
};

} // namespace x86emu
