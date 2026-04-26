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

# CPM Integration
include(../cmake/CPM.cmake)
find_package(Python3 REQUIRED COMPONENTS Interpreter)

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
option(FIBERCPU_ENABLE_BLOCK_PROFILE "Enable lightweight per-block execution counting" OFF)
option(FIBERCPU_ENABLE_HANDLER_PROFILE "Enable approximate per-handler execution profiling" OFF)
option(FIBERCPU_ENABLE_SUPEROPCODES "Enable generated SuperOpcode matching and handlers" ON)

set(FIBERCPU_GENERATED_SUPEROPCODES_FILE
    "${CMAKE_CURRENT_SOURCE_DIR}/src/generated/superopcodes.generated.cpp")

set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES OFF)
if(FIBERCPU_ENABLE_SUPEROPCODES AND EXISTS "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES ON)
endif()

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

# Source files
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS "src/*.cpp" "src/*.c" "src/*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "build/.*")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")
if(NOT FIBERCPU_ENABLE_SUPEROPCODES)
    list(FILTER SOURCES EXCLUDE REGEX "generated/superopcodes\\.generated\\.cpp$")
endif()
list(FILTER SOURCES EXCLUDE REGEX "handler_reflection_exports\\.cpp$")
list(FILTER SOURCES EXCLUDE REGEX "jit/.*")

if(NOT MSVC AND NOT EMSCRIPTEN)
    add_compile_options(
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-O3>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fomit-frame-pointer>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fno-stack-protector>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-march=native>"
    )
endif()

# Object library for core parts (contains the opcodes to scan)
add_library(fibercpu_core OBJECT ${SOURCES})
set_target_properties(fibercpu_core PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_include_directories(fibercpu_core PRIVATE .)
target_include_directories(fibercpu_core SYSTEM PRIVATE ${simde_SOURCE_DIR})
target_link_libraries(fibercpu_core PRIVATE unordered_dense::unordered_dense)

if(EMSCRIPTEN)
    target_compile_options(fibercpu_core PRIVATE
        $<$<COMPILE_LANGUAGE:C>:-mtail-call>
        $<$<COMPILE_LANGUAGE:C>:-msimd128>
        $<$<COMPILE_LANGUAGE:C>:-Wno-pass-failed>
        $<$<COMPILE_LANGUAGE:CXX>:-mtail-call>
        $<$<COMPILE_LANGUAGE:CXX>:-msimd128>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-pass-failed>
    )
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(fibercpu_core PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unused-parameter>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-nested-anon-types>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-strict-aliasing>
    )
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND NOT MSVC)
    target_compile_options(fibercpu_core PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-fno-rtti>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-exceptions>
    )
endif()

if(NOT MSVC)
    target_compile_options(fibercpu_core PRIVATE
        $<$<COMPILE_LANGUAGE:C>:-fno-strict-aliasing>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-strict-aliasing>
    )
endif()



# SoftFloat library
if(USE_SOFTFLOAT)
    add_definitions(-DX86EMU_USE_SOFTFLOAT)
    add_subdirectory(3rdparty/softfloat)
endif()

set(FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP
    "${CMAKE_CURRENT_BINARY_DIR}/generated/handler_reflection.generated.cpp")

add_custom_command(
    OUTPUT ${FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/generated"
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_handler_reflection.py
            --output ${FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP}
            $<TARGET_OBJECTS:fibercpu_core>
    DEPENDS fibercpu_core ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_handler_reflection.py
    COMMENT "Generating handler reflection table from fibercpu_core objects"
    COMMAND_EXPAND_LISTS
    VERBATIM
)

# Final library includes objects from core
set(FIBERCPU_LIBRARY_TYPE SHARED)
if(FIBERCPU_BUILD_STATIC)
    set(FIBERCPU_LIBRARY_TYPE STATIC)
endif()

set(FIBERCPU_FINAL_SOURCES
    $<TARGET_OBJECTS:fibercpu_core>
    src/handler_reflection_exports.cpp
    ${FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP}
)

add_library(fibercpu ${FIBERCPU_LIBRARY_TYPE} ${FIBERCPU_FINAL_SOURCES})
set_target_properties(fibercpu PROPERTIES POSITION_INDEPENDENT_CODE ON)

target_include_directories(fibercpu PRIVATE src)
target_include_directories(fibercpu SYSTEM PRIVATE ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(fibercpu PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unused-parameter>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-nested-anon-types>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-strict-aliasing>
    )
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND NOT MSVC)
    target_compile_options(fibercpu PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-fno-rtti>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-exceptions>
    )
endif()

if(NOT MSVC)
    target_compile_options(fibercpu PRIVATE
        $<$<COMPILE_LANGUAGE:C>:-fno-strict-aliasing>
        $<$<COMPILE_LANGUAGE:CXX>:-fno-strict-aliasing>
    )
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
    target_compile_definitions(fibercpu_core PRIVATE ENABLE_TLB_STATS)
    target_compile_definitions(fibercpu PRIVATE ENABLE_TLB_STATS)
endif()
if(FIBERCPU_ENABLE_HANDLER_PROFILE)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_ENABLE_HANDLER_PROFILE=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_HANDLER_PROFILE=1)
endif()
if(FIBERCPU_ENABLE_BLOCK_PROFILE)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_ENABLE_BLOCK_PROFILE=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_BLOCK_PROFILE=1)
endif()
if(FIBERCPU_ENABLE_SUPEROPCODES)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=1)
else()
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=0)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_SUPEROPCODES=0)
endif()
if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_HAVE_GENERATED_SUPEROPCODES=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_HAVE_GENERATED_SUPEROPCODES=1)
else()
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_HAVE_GENERATED_SUPEROPCODES=0)
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
        set_target_properties(fibercpu_core PROPERTIES INTERPROCEDURAL_OPTIMIZATION TRUE)
    endif()
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
