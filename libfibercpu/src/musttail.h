#pragma once

#if __has_cpp_attribute(clang::musttail)
#define ATTR_MUSTTAIL [[clang::musttail]]
#define FIBERCPU_HAS_MUSTTAIL 1
#elif __has_cpp_attribute(gnu::musttail)
#define ATTR_MUSTTAIL [[gnu::musttail]]
#define FIBERCPU_HAS_MUSTTAIL 1
#elif __has_cpp_attribute(msvc::musttail)
#define ATTR_MUSTTAIL [[msvc::musttail]]
#define FIBERCPU_HAS_MUSTTAIL 1
#else
#define ATTR_MUSTTAIL
#define FIBERCPU_HAS_MUSTTAIL 0
#endif

// The GNU attribute probe works on x64 and ARM64 Apple Clang, which reports
// zero for the unqualified C++ probe even when preserve_none is available.
#if defined(__clang__) && (defined(__x86_64__) || defined(_M_X64) || defined(__aarch64__) || defined(_M_ARM64))
#if __has_attribute(preserve_none)
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#else
#define ATTR_PRESERVE_NONE
#endif
#elif __has_cpp_attribute(preserve_none)
#define ATTR_PRESERVE_NONE __attribute__((preserve_none))
#else
#define ATTR_PRESERVE_NONE
#endif
