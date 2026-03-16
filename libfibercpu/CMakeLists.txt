cmake_minimum_required(VERSION 3.20)

# Keep native objects compatible with the Podish macOS deployment baseline.
if(APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "14.0" CACHE STRING "Minimum macOS deployment version" FORCE)
endif()

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
option(FIBERCPU_ENABLE_LTO "Enable interprocedural optimization (LTO/IPO)" ON)
option(FIBERCPU_ENABLE_HANDLER_PROFILE "Enable approximate per-handler execution profiling" OFF)
option(FIBERCPU_ENABLE_JCC_PROFILE "Enable conditional-branch profiling" OFF)
option(FIBERCPU_ENABLE_SUPEROPCODES "Enable generated SuperOpcode matching and handlers" ON)

set(FIBERCPU_GENERATED_SUPEROPCODES_FILE "${CMAKE_CURRENT_SOURCE_DIR}/generated/superopcodes.generated.cpp")
set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES OFF)
if(FIBERCPU_ENABLE_SUPEROPCODES AND EXISTS "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES ON)
endif()

# JIT Stencil Generation
set(FIBERCPU_JIT_STENCILS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/jit")
set(FIBERCPU_STENCIL_GENERATOR "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_jit_stencils.py")
set(FIBERCPU_GENERATED_STENCILS_CPP "${CMAKE_CURRENT_SOURCE_DIR}/generated/stencil_kernels.generated.cpp")
set(FIBERCPU_GENERATED_STENCILS_INC "${CMAKE_CURRENT_SOURCE_DIR}/generated/stencils.generated.inc")

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
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS "*.cpp" "*.c" "*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "build/.*")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")
if(NOT FIBERCPU_ENABLE_SUPEROPCODES)
    list(FILTER SOURCES EXCLUDE REGEX "generated/superopcodes\\.generated\\.cpp$")
endif()
list(FILTER SOURCES EXCLUDE REGEX "generated/stencil_kernels\\.generated\\.cpp$")

# Separate JIT sources from core to break circular dependency
set(SOURCES_CORE ${SOURCES})
list(FILTER SOURCES_CORE EXCLUDE REGEX "jit/block_builder\\.cpp$")
list(FILTER SOURCES_CORE EXCLUDE REGEX "jit/stencil_kernels\\.cpp$")

set(SOURCES_JIT
    jit/block_builder.cpp
)

# Object library for core parts (contains the opcodes to scan)
add_library(fibercpu_core OBJECT ${SOURCES_CORE})
target_include_directories(fibercpu_core PRIVATE . ${simde_SOURCE_DIR})
target_link_libraries(fibercpu_core PRIVATE unordered_dense::unordered_dense)

# SoftFloat library
if(USE_SOFTFLOAT)
    add_definitions(-DX86EMU_USE_SOFTFLOAT)
    add_subdirectory(3rdparty/softfloat)
endif()

# JIT extraction steps
set(FIBERCPU_PROBE_CPP ${CMAKE_CURRENT_BINARY_DIR}/jit/probe_kernels.generated.cpp)
set(FIBERCPU_PROBE_OBJ ${CMAKE_CURRENT_BINARY_DIR}/jit/probe_kernels.o)
set(FIBERCPU_USAGE_JSON ${CMAKE_CURRENT_BINARY_DIR}/jit/usage_analysis.json)
set(FIBERCPU_JIT_COMPILE_FLAGS
    -std=c++23
    -O3
    -fno-stack-protector
    -fomit-frame-pointer
    -fPIC
    -I${CMAKE_CURRENT_SOURCE_DIR}
    -I${CMAKE_CURRENT_SOURCE_DIR}/..
    -I${simde_SOURCE_DIR}
    -I${unordered_dense_SOURCE_DIR}/include
)
if(APPLE)
    list(APPEND FIBERCPU_JIT_COMPILE_FLAGS
        -isysroot ${CMAKE_OSX_SYSROOT}
        -mmacosx-version-min=${CMAKE_OSX_DEPLOYMENT_TARGET}
    )
endif()

# Step 1: Generate Probe Kernels by scanning CORE objects
add_custom_command(
    OUTPUT ${FIBERCPU_PROBE_CPP}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/jit"
    COMMAND python3 ${FIBERCPU_STENCIL_GENERATOR} --mode=probe $<TARGET_OBJECTS:fibercpu_core> ${FIBERCPU_PROBE_CPP}
    DEPENDS fibercpu_core ${FIBERCPU_STENCIL_GENERATOR}
    COMMENT "Generating JIT probe kernels (Pass 1)"
    COMMAND_EXPAND_LISTS
    VERBATIM
)

# Step 2: Compile Probe Kernels
add_custom_command(
    OUTPUT ${FIBERCPU_PROBE_OBJ}
    COMMAND ${CMAKE_CXX_COMPILER} ${FIBERCPU_JIT_COMPILE_FLAGS} -c ${FIBERCPU_PROBE_CPP} -o ${FIBERCPU_PROBE_OBJ}
    DEPENDS ${FIBERCPU_PROBE_CPP}
    COMMENT "Compiling JIT probe kernels"
    VERBATIM
)

# Step 3: Analyze Probe Results
add_custom_command(
    OUTPUT ${FIBERCPU_USAGE_JSON}
    COMMAND python3 ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/extract_stencils_obj.py --mode=analyze ${FIBERCPU_PROBE_OBJ} ${FIBERCPU_USAGE_JSON}
    DEPENDS ${FIBERCPU_PROBE_OBJ} ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/extract_stencils_obj.py
    COMMENT "Analyzing DecodedOp field usage from probes"
    VERBATIM
)

# Step 4: Generate Optimized Stencil Kernels
add_custom_command(
    OUTPUT ${FIBERCPU_GENERATED_STENCILS_CPP}
    COMMAND python3 ${FIBERCPU_STENCIL_GENERATOR} --mode=stencil --usage=${FIBERCPU_USAGE_JSON} $<TARGET_OBJECTS:fibercpu_core> ${FIBERCPU_GENERATED_STENCILS_CPP}
    DEPENDS fibercpu_core ${FIBERCPU_STENCIL_GENERATOR} ${FIBERCPU_USAGE_JSON}
    COMMENT "Generating optimized JIT stencil kernels (Pass 2)"
    COMMAND_EXPAND_LISTS
    VERBATIM
)

# Step 5: Compile Optimized Stencil Kernels
set(FIBERCPU_STENCIL_OBJ ${CMAKE_CURRENT_BINARY_DIR}/jit/stencil_kernels.o)
add_custom_command(
    OUTPUT ${FIBERCPU_STENCIL_OBJ}
    COMMAND ${CMAKE_CXX_COMPILER} ${FIBERCPU_JIT_COMPILE_FLAGS} -c ${FIBERCPU_GENERATED_STENCILS_CPP} -o ${FIBERCPU_STENCIL_OBJ}
    DEPENDS ${FIBERCPU_GENERATED_STENCILS_CPP}
    COMMENT "Compiling optimized JIT stencil kernels"
    VERBATIM
)

# Step 6: Extract Final Stencils
add_custom_command(
    OUTPUT ${FIBERCPU_GENERATED_STENCILS_INC}
    COMMAND python3 ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/extract_stencils_obj.py --mode=extract --usage=${FIBERCPU_USAGE_JSON} ${FIBERCPU_STENCIL_OBJ} ${FIBERCPU_GENERATED_STENCILS_INC}
    DEPENDS ${FIBERCPU_STENCIL_OBJ} ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/extract_stencils_obj.py
    COMMENT "Extracting final machine code stencils"
    VERBATIM
)

add_custom_target(fibercpu_jit_stencils DEPENDS ${FIBERCPU_GENERATED_STENCILS_INC})

# Final library includes objects from core and the JIT sources
set(FIBERCPU_LIBRARY_TYPE SHARED)
if(FIBERCPU_BUILD_STATIC)
    set(FIBERCPU_LIBRARY_TYPE STATIC)
endif()
add_library(fibercpu ${FIBERCPU_LIBRARY_TYPE} ${SOURCES_JIT} $<TARGET_OBJECTS:fibercpu_core> ${FIBERCPU_STENCIL_OBJ})
add_dependencies(fibercpu fibercpu_jit_stencils)

target_include_directories(fibercpu PRIVATE . ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)
if(APPLE)
    target_link_libraries(fibercpu PRIVATE c++)
endif()

# Link libraries
if(USE_SOFTFLOAT)
    if(FIBERCPU_BUILD_STATIC)
        target_sources(fibercpu PRIVATE $<TARGET_OBJECTS:softfloat_obj>)
    else()
        target_link_libraries(fibercpu PRIVATE softfloat)
    endif()
endif()

if(ENABLE_TLB_STATS)
    target_compile_definitions(fibercpu PRIVATE ENABLE_TLB_STATS)
endif()
if(FIBERCPU_ENABLE_HANDLER_PROFILE)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_HANDLER_PROFILE=1)
endif()
if(FIBERCPU_ENABLE_JCC_PROFILE)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_JCC_PROFILE=1)
endif()
if(FIBERCPU_ENABLE_SUPEROPCODES)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=1)
else()
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=0)
endif()
if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_HAVE_GENERATED_SUPEROPCODES=1)
else()
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_HAVE_GENERATED_SUPEROPCODES=0)
endif()
if(FIBERCPU_BUILD_STATIC)
    target_compile_definitions(fibercpu PUBLIC FIBERCPU_STATIC_BUILD=1)
endif()

if(FIBERCPU_BUILD_STATIC AND FIBERCPU_ENABLE_LTO)
    set(FIBERCPU_ENABLE_LTO OFF)
endif()

if(FIBERCPU_ENABLE_LTO)
    include(CheckIPOSupported)
    check_ipo_supported(RESULT result OUTPUT error)
    if(result)
        set_target_properties(fibercpu PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)
    endif()
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-O3>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fomit-frame-pointer>" 
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fno-stack-protector>"
    )
endif()

# macOS codesign support
if(APPLE)
    option(CODESIGN_IDENTITY "Codesign identity to sign the built library. Use empty for ad-hoc, SKIP to disable" "")
    if(NOT CODESIGN_IDENTITY STREQUAL "SKIP" AND NOT CODESIGN_IDENTITY STREQUAL "OFF")
        if(CODESIGN_IDENTITY STREQUAL "")
            set(_codesign_identity "-")
        else()
            set(_codesign_identity "${CODESIGN_IDENTITY}")
        endif()
        add_custom_command(TARGET fibercpu POST_BUILD
            COMMAND /usr/bin/codesign -f -s "${_codesign_identity}" --timestamp=none "$<TARGET_FILE:fibercpu>"
            COMMENT "Codesigning fibercpu with identity: ${_codesign_identity}"
            VERBATIM)
    endif()
endif()
