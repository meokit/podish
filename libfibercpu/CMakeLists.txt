cmake_minimum_required(VERSION 3.20)
project(libfibercpu LANGUAGES C CXX)

# Enforce C++23 and C11
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)

if(MSVC)
    add_compile_definitions(THREAD_LOCAL=__declspec(thread))
else()
    add_compile_definitions(THREAD_LOCAL=__thread)
endif()

# CPM Integration
include(../cmake/CPM.cmake)

CPMAddPackage(
    NAME simde
    GITHUB_REPOSITORY simd-everywhere/simde
    VERSION 0.8.2
    OPTIONS "SIMDE_ENABLE_NATIVE_ALIASES ON"
)

CPMAddPackage(
    NAME unordered_dense
    GITHUB_REPOSITORY martinus/unordered_dense
    VERSION 4.8.1
)

option(USE_SOFTFLOAT "Use Berkeley SoftFloat 3 for x87 FPU emulation" ON)
option(ENABLE_TLB_STATS "Enable tracking of L1/L2 TLB hits and misses" OFF)
option(FIBERCPU_BUILD_STATIC "Build fibercpu as a static library instead of shared" OFF)

set(PGO_MODE "OFF" CACHE STRING "PGO build mode (OFF, GENERATE, USE)")
set_property(CACHE PGO_MODE PROPERTY STRINGS "OFF" "GENERATE" "USE")

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    if(PGO_MODE STREQUAL "GENERATE")
        message(STATUS "PGO: Profile Generate mode enabled")
        add_compile_options("-fprofile-instr-generate")
        add_link_options("-fprofile-instr-generate")
    elseif(PGO_MODE STREQUAL "USE")
        message(STATUS "PGO: Profile Use mode enabled")
        if(EXISTS "${CMAKE_BINARY_DIR}/pgo_data/default.profdata")
             add_compile_options("-fprofile-instr-use=${CMAKE_BINARY_DIR}/pgo_data/default.profdata")
             add_link_options("-fprofile-instr-use=${CMAKE_BINARY_DIR}/pgo_data/default.profdata")
        else()
             message(FATAL_ERROR "PGO: ${CMAKE_BINARY_DIR}/pgo_data/default.profdata not found. Run training and merge profiles first.")
        endif()
    endif()
endif()

# Compiler warnings and options
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        -Wall -Wextra -Wpedantic
        -Wno-unused-parameter
        -Wno-nested-anon-types
        -fno-rtti -fno-exceptions
    )
endif()

# Source files
file(GLOB_RECURSE SOURCES "*.cpp" "*.c" "*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "build/.*")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")

# SoftFloat library
if(USE_SOFTFLOAT)
    add_definitions(-DX86EMU_USE_SOFTFLOAT)
    add_subdirectory(3rdparty/softfloat)
endif()

# Generate library as 'fibercpu'
set(FIBERCPU_LIBRARY_TYPE SHARED)
if(FIBERCPU_BUILD_STATIC)
    set(FIBERCPU_LIBRARY_TYPE STATIC)
endif()
add_library(fibercpu ${FIBERCPU_LIBRARY_TYPE} ${SOURCES})

target_include_directories(fibercpu PRIVATE . ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)

# Link libraries
# If SoftFloat is used, link it
if(USE_SOFTFLOAT)
    target_link_libraries(fibercpu PRIVATE softfloat)
endif()

if(ENABLE_TLB_STATS)
    target_compile_definitions(fibercpu PRIVATE ENABLE_TLB_STATS)
endif()
if(FIBERCPU_BUILD_STATIC)
    target_compile_definitions(fibercpu PUBLIC FIBERCPU_STATIC_BUILD=1)
endif()

include(CheckIPOSupported)
check_ipo_supported(RESULT result OUTPUT error)
if(result)
    message(STATUS "LTO: Interprocedural Optimization (LTO) enabled for fibercpu")
    set_target_properties(fibercpu PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)
else()
    message(WARNING "LTO: Interprocedural Optimization (LTO) is NOT supported: ${error}")
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-O3>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fomit-frame-pointer>" 
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fno-stack-protector>"
    )
endif()

# macOS codesign support: allow providing an identity; if set to SKIP, skip signing.
if(APPLE)
    option(CODESIGN_IDENTITY "Codesign identity to sign the built library. Use empty for ad-hoc, SKIP to disable" "")

    # CMake 'option' may set the variable to the special value "OFF" when disabled from the cache.
    if(NOT CODESIGN_IDENTITY STREQUAL "SKIP" AND NOT CODESIGN_IDENTITY STREQUAL "OFF")
        if(CODESIGN_IDENTITY STREQUAL "")
            set(_codesign_identity "-")
            message(STATUS "CODESIGN_IDENTITY empty -> using ad-hoc codesign (-) for fibercpu")
        else()
            set(_codesign_identity "${CODESIGN_IDENTITY}")
            message(STATUS "Codesigning fibercpu with identity: ${CODESIGN_IDENTITY}")
        endif()

        add_custom_command(TARGET fibercpu POST_BUILD
            COMMAND /usr/bin/codesign -f -s "${_codesign_identity}" --timestamp=none "$<TARGET_FILE:fibercpu>"
            COMMENT "Codesigning fibercpu with identity: ${_codesign_identity}"
            VERBATIM)
    else()
        message(STATUS "CODESIGN_IDENTITY=SKIP -> skipping codesign for fibercpu on macOS")
    endif()
endif()
