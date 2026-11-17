cmake_minimum_required(VERSION 3.20)

# Keep native objects aligned with the NativeAOT macOS deployment target.
if(APPLE)
    set(CMAKE_OSX_DEPLOYMENT_TARGET "12.0" CACHE STRING "Minimum macOS deployment version" FORCE)
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
    OPTIONS "SIMDE_ENABLE_NATIVE_ALIASES OFF"
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
option(FIBERCPU_ENABLE_SEQUENCE_PROFILE "Count actual unfused within-block Continue edges (training only)" OFF)
if(FIBERCPU_ENABLE_SEQUENCE_PROFILE AND FIBERCPU_ENABLE_SUPEROPCODES)
    message(FATAL_ERROR "Sequence training requires FIBERCPU_ENABLE_SUPEROPCODES=OFF")
endif()

if(MSVC AND FIBERCPU_BUILD_STATIC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "MSVC runtime library" FORCE)
endif()

# Independent compile-time choices; AUTO promotes only the measured host/build.
set(FIBERCPU_FUSION_STRATEGY "AUTO" CACHE STRING "Reviewed fusion set (AUTO, BASE, EXTENDED, SHAPED)")
set_property(CACHE FIBERCPU_FUSION_STRATEGY PROPERTY STRINGS AUTO BASE EXTENDED SHAPED)
set(FIBERCPU_OPERAND_SPECIALIZATION "AUTO" CACHE STRING "Operand lowering (AUTO, OFF, PROFILE, REGISTER, HYBRID)")
set_property(CACHE FIBERCPU_OPERAND_SPECIALIZATION PROPERTY STRINGS AUTO OFF PROFILE REGISTER HYBRID)
set(FIBERCPU_OPERAND_BUDGET "256" CACHE STRING "Maximum frozen operand shapes")
set(FIBERCPU_OPERAND_SCOPE "AUTO" CACHE STRING "Operand lowering scope (AUTO, ALL, ADDED_FUSIONS)")
set_property(CACHE FIBERCPU_OPERAND_SCOPE PROPERTY STRINGS AUTO ALL ADDED_FUSIONS)
set(FIBERCPU_GPR_PIN "OFF" CACHE STRING "ARM64 EAX/ESP cache (OFF, ABI_ONLY, CONSERVATIVE, EAX_ESP)")
set_property(CACHE FIBERCPU_GPR_PIN PROPERTY STRINGS OFF ABI_ONLY CONSERVATIVE EAX_ESP)
if(FIBERCPU_FUSION_STRATEGY STREQUAL "AUTO")
    set(FIBERCPU_FUSION_STRATEGY "BASE")
    if(APPLE AND CMAKE_SYSTEM_NAME STREQUAL "Darwin" AND
       CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64|ARM64)$" AND
       (NOT CMAKE_OSX_ARCHITECTURES OR CMAKE_OSX_ARCHITECTURES STREQUAL "arm64") AND
       CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND NOT FIBERCPU_BUILD_STATIC AND
       NOT EMSCRIPTEN AND NOT FIBERCPU_ENABLE_SEQUENCE_PROFILE AND
       NOT FIBERCPU_GENERATED_SUPEROPCODES_FILE)
        set(FIBERCPU_FUSION_STRATEGY "SHAPED")
    endif()
endif()
if(FIBERCPU_OPERAND_SPECIALIZATION STREQUAL "AUTO")
    set(FIBERCPU_OPERAND_SPECIALIZATION "OFF")
    if(FIBERCPU_FUSION_STRATEGY STREQUAL "SHAPED")
        set(FIBERCPU_OPERAND_SPECIALIZATION "PROFILE")
    endif()
endif()
if(FIBERCPU_OPERAND_SCOPE STREQUAL "AUTO")
    set(FIBERCPU_OPERAND_SCOPE "ALL")
    if(FIBERCPU_FUSION_STRATEGY STREQUAL "SHAPED" AND FIBERCPU_GPR_PIN STREQUAL "OFF")
        set(FIBERCPU_OPERAND_SCOPE "ADDED_FUSIONS")
    endif()
endif()
if(NOT FIBERCPU_OPERAND_SCOPE MATCHES "^(ALL|ADDED_FUSIONS)$")
    message(FATAL_ERROR "Unknown FIBERCPU_OPERAND_SCOPE")
endif()
set(FIBERCPU_OPERAND_FUSIONS_ONLY OFF)
if(FIBERCPU_OPERAND_SCOPE STREQUAL "ADDED_FUSIONS")
    set(FIBERCPU_OPERAND_FUSIONS_ONLY ON)
endif()
set(_operand_modes OFF PROFILE REGISTER HYBRID)
list(FIND _operand_modes "${FIBERCPU_OPERAND_SPECIALIZATION}" FIBERCPU_OPERAND_MODE)
set(_pin_modes OFF ABI_ONLY CONSERVATIVE EAX_ESP)
list(FIND _pin_modes "${FIBERCPU_GPR_PIN}" FIBERCPU_GPR_PIN_MODE)
if(FIBERCPU_OPERAND_MODE LESS 0 OR FIBERCPU_GPR_PIN_MODE LESS 0 OR NOT FIBERCPU_OPERAND_BUDGET MATCHES "^[0-9]+$" OR FIBERCPU_OPERAND_BUDGET GREATER 256)
    message(FATAL_ERROR "Invalid operand specialization, budget, or GPR pin mode")
endif()
if(NOT FIBERCPU_FUSION_STRATEGY MATCHES "^(BASE|EXTENDED|SHAPED)$")
    message(FATAL_ERROR "Unknown FIBERCPU_FUSION_STRATEGY")
endif()
if(FIBERCPU_GPR_PIN_MODE GREATER 0)
    if(NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64|ARM64)$" OR NOT CMAKE_CXX_COMPILER_ID MATCHES "Clang")
        message(FATAL_ERROR "GPR pin experiments require ARM64 Clang preserve_none")
    endif()
    include(CheckCXXSourceCompiles)
    check_cxx_source_compiles("#if !__has_attribute(preserve_none)\n#error preserve_none required\n#endif\nint main(){}" FIBERCPU_HAS_PRESERVE_NONE)
    if(NOT FIBERCPU_HAS_PRESERVE_NONE)
        message(FATAL_ERROR "GPR pin requires compiler support for preserve_none")
    endif()
endif()
set(FIBERCPU_LOWER_OPERANDS OFF)
if(FIBERCPU_OPERAND_MODE GREATER 0 OR FIBERCPU_GPR_PIN_MODE EQUAL 3)
    set(FIBERCPU_LOWER_OPERANDS ON)
endif()
set(FIBERCPU_FUSION_SHAPES OFF)
if(FIBERCPU_FUSION_STRATEGY STREQUAL "SHAPED")
    if(NOT FIBERCPU_OPERAND_MODE EQUAL 1 AND NOT FIBERCPU_OPERAND_MODE EQUAL 3)
        message(FATAL_ERROR "SHAPED fusion requires PROFILE or HYBRID operand lowering")
    endif()
    set(FIBERCPU_FUSION_SHAPES ON)
endif()
if(FIBERCPU_OPERAND_FUSIONS_ONLY AND NOT FIBERCPU_FUSION_SHAPES)
    message(FATAL_ERROR "ADDED_FUSIONS operand scope requires SHAPED fusion")
endif()
set(_default_fusion "${CMAKE_CURRENT_SOURCE_DIR}/src/generated/superopcodes.generated.cpp")
if(NOT FIBERCPU_FUSION_STRATEGY STREQUAL "BASE")
    set(_default_fusion "${CMAKE_CURRENT_SOURCE_DIR}/src/generated/superopcodes.extended.generated.cpp")
endif()
# An explicit external table wins; an empty cache value follows the strategy.
set(FIBERCPU_GENERATED_SUPEROPCODES_FILE
    "" CACHE FILEPATH "Explicit generated superopcode source (empty follows strategy)")
if(NOT FIBERCPU_GENERATED_SUPEROPCODES_FILE)
    set(FIBERCPU_GENERATED_SUPEROPCODES_FILE "${_default_fusion}")
endif()

set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES OFF)
if(FIBERCPU_ENABLE_SUPEROPCODES AND EXISTS "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(FIBERCPU_HAVE_GENERATED_SUPEROPCODES ON)
    file(SHA256 "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}" FIBERCPU_SUPEROPCODE_SET_SHA256)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
elseif(FIBERCPU_ENABLE_SUPEROPCODES)
    message(FATAL_ERROR "Selected superopcode source does not exist: ${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
else()
    set(FIBERCPU_SUPEROPCODE_SET_SHA256 "none")
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

# Older pair-only generated files do not include handwritten registrations.
set(FIBERCPU_GENERATED_WITH_HANDWRITTEN OFF)
if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    file(READ "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}" FIBERCPU_GENERATED_HEADER LIMIT 1024)
    if(FIBERCPU_GENERATED_HEADER MATCHES "includes_handwritten_superopcodes")
        set(FIBERCPU_GENERATED_WITH_HANDWRITTEN ON)
    endif()
endif()

# Source files
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS "src/*.cpp" "src/*.c" "src/*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "build/.*")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")
list(FILTER SOURCES EXCLUDE REGEX "generated/superopcodes.*\\.cpp$")
if(FIBERCPU_LOWER_OPERANDS)
    set(_operand_output "${CMAKE_CURRENT_BINARY_DIR}/generated/operand_specializations.inc")
    set(_operand_source "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(_combined "${CMAKE_CURRENT_BINARY_DIR}/generated/optimized_handlers.cpp")
    file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
    if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
        file(WRITE "${_combined}" "#include \"${_operand_source}\"\n#include \"${_operand_output}\"\n")
    else()
        set(_operand_source "${CMAKE_CURRENT_BINARY_DIR}/generated/empty_fusion.cpp")
        file(WRITE "${_operand_source}" "// Fusion disabled\n")
        file(GLOB _impl_headers RELATIVE "${CMAKE_CURRENT_SOURCE_DIR}/src" "src/ops/*_impl.h")
        file(WRITE "${_combined}" "#include \"ops.h\"\n")
        foreach(_header IN LISTS _impl_headers)
            file(APPEND "${_combined}" "#include \"${_header}\"\n")
        endforeach()
        file(APPEND "${_combined}" "#include \"${_operand_output}\"\n")
    endif()
    set(_plan "${CMAKE_CURRENT_SOURCE_DIR}/../benchmark/optimization/manifests/operand-shapes-v1.json")
    add_custom_command(OUTPUT "${_operand_output}"
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_operand_specializations.py"
            --source "${_operand_source}" --plan "${_plan}"
            --families "${CMAKE_CURRENT_SOURCE_DIR}/src/instruction_families.h"
            --variants "${CMAKE_CURRENT_SOURCE_DIR}/src/instruction_variants.inc"
            --fallbacks "${CMAKE_CURRENT_SOURCE_DIR}/src/instruction_fallbacks.inc"
            --output "${_operand_output}" --mode "${FIBERCPU_OPERAND_MODE}"
            --budget "${FIBERCPU_OPERAND_BUDGET}" --fused-only "$<BOOL:${FIBERCPU_OPERAND_FUSIONS_ONLY}>" --pin-mode "${FIBERCPU_GPR_PIN_MODE}"
        DEPENDS "${_operand_source}" "${_plan}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_operand_specializations.py"
            src/instruction_families.h src/instruction_variants.inc src/instruction_fallbacks.inc
        VERBATIM)
    set_source_files_properties("${_combined}" PROPERTIES OBJECT_DEPENDS "${_operand_output}")
    list(APPEND SOURCES "${_combined}" "${_operand_output}")
elseif(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    list(APPEND SOURCES "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
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

# Fingerprint the semantic catalog independently of host addresses/compiler ABI.
set(INSTRUCTION_CATALOG_INPUTS
    instruction_spec.h instruction_families.h instruction_variants.inc
    instruction_fallbacks.inc instruction_variants.cpp instruction_variants.h specialization.h)
set(INSTRUCTION_CATALOG_FINGERPRINT "")
foreach(input IN LISTS INSTRUCTION_CATALOG_INPUTS)
    set(path "${CMAKE_CURRENT_SOURCE_DIR}/src/${input}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${path}")
    file(READ "${path}" input_text)
    string(REPLACE "\r\n" "\n" input_text "${input_text}")
    string(SHA256 input_hash "${input_text}")
    string(APPEND INSTRUCTION_CATALOG_FINGERPRINT "${input}:${input_hash}\n")
endforeach()
string(SHA256 INSTRUCTION_CATALOG_SHA256 "${INSTRUCTION_CATALOG_FINGERPRINT}")
file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/instruction_catalog_hash.h"
    "#pragma once\n#define FIBERCPU_INSTRUCTION_CATALOG_SHA256 \"${INSTRUCTION_CATALOG_SHA256}\"\n")

set(_operand_plan_hash "none")
if(FIBERCPU_LOWER_OPERANDS)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_plan}")
    file(SHA256 "${_plan}" _operand_plan_hash)
endif()
set(FIBERCPU_OPTIMIZATION_CONFIG "fusion=${FIBERCPU_FUSION_STRATEGY};enabled=${FIBERCPU_ENABLE_SUPEROPCODES};operand=${FIBERCPU_OPERAND_SPECIALIZATION};budget=${FIBERCPU_OPERAND_BUDGET};scope=${FIBERCPU_OPERAND_SCOPE};pin=${FIBERCPU_GPR_PIN};plan=${_operand_plan_hash}")
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/optimization_config.h"
    "#pragma once\n#define FIBERCPU_OPTIMIZATION_CONFIG \"${FIBERCPU_OPTIMIZATION_CONFIG}\"\n")

# Object library for core parts (contains the opcodes to scan)
add_library(fibercpu_core OBJECT ${SOURCES})
set_target_properties(fibercpu_core PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_include_directories(fibercpu_core PRIVATE . "${CMAKE_CURRENT_BINARY_DIR}/generated")
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
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-c++98-compat>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-c++98-compat-pedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-pre-c++17-compat>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-pre-c++20-compat-pedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unused-parameter>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-nested-anon-types>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-old-style-cast>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-sign-conversion>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-covered-switch-default>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-zero-as-null-pointer-constant>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-reserved-macro-identifier>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unsafe-buffer-usage>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-implicit-int-conversion>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-global-constructors>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-exit-time-destructors>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-format-nonliteral>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-missing-prototypes>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-tautological-type-limit-compare>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-newline-eof>
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

if(MSVC)
    target_compile_definitions(fibercpu_core PRIVATE _CRT_SECURE_NO_WARNINGS)
endif()



# SoftFloat library
if(USE_SOFTFLOAT)
    add_definitions(-DX86EMU_USE_SOFTFLOAT)
    add_subdirectory(3rdparty/softfloat)
endif()

set(FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP
    "${CMAKE_CURRENT_BINARY_DIR}/generated/handler_reflection.generated.cpp")

# The full macOS solution also puts the Emscripten LLVM tools on PATH.
# Its llvm-nm may not understand the newer Apple Clang host LTO bitcode.
# Select Xcode's matching symbol reader for host objects only.
set(FIBERCPU_REFLECTION_TOOL_ARGS)
if(APPLE AND NOT EMSCRIPTEN)
    execute_process(COMMAND xcrun --find nm
        OUTPUT_VARIABLE FIBERCPU_APPLE_NM
        OUTPUT_STRIP_TRAILING_WHITESPACE
        COMMAND_ERROR_IS_FATAL ANY)
    list(APPEND FIBERCPU_REFLECTION_TOOL_ARGS --nm "${FIBERCPU_APPLE_NM}")
endif()

add_custom_command(
    OUTPUT ${FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP}
    COMMAND ${CMAKE_COMMAND} -E make_directory "${CMAKE_CURRENT_BINARY_DIR}/generated"
    COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_handler_reflection.py
            ${FIBERCPU_REFLECTION_TOOL_ARGS}
            --output ${FIBERCPU_GENERATED_HANDLER_REFLECTION_CPP}
            --compiler ${CMAKE_CXX_COMPILER}
            $<TARGET_OBJECTS:fibercpu_core>
    # The object-library target supplies ordering, not file-level invalidation.
    # Re-scan after any handler object changes (including a different selected set).
    DEPENDS fibercpu_core $<TARGET_OBJECTS:fibercpu_core>
            ${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_handler_reflection.py
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
target_compile_definitions(fibercpu PRIVATE FIBERCPU_SUPEROPCODE_SET_SHA256="${FIBERCPU_SUPEROPCODE_SET_SHA256}")
target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_SUPEROPCODE_SET_SHA256="${FIBERCPU_SUPEROPCODE_SET_SHA256}")
target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_GENERATED_WITH_HANDWRITTEN=$<BOOL:${FIBERCPU_GENERATED_WITH_HANDWRITTEN}>)
target_include_directories(fibercpu_core PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
set_target_properties(fibercpu PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(WIN32 AND NOT FIBERCPU_BUILD_STATIC)
    # The managed runtime loads fibercpu.dll via P/Invoke and expects the X86_* symbols
    # to be exported directly on Windows shared builds.
    set_target_properties(fibercpu PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
endif()

target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_GPR_PIN_MODE=${FIBERCPU_GPR_PIN_MODE}
    FIBERCPU_OPERAND_FUSIONS_ONLY=$<BOOL:${FIBERCPU_OPERAND_FUSIONS_ONLY}> FIBERCPU_FUSION_SHAPES=$<BOOL:${FIBERCPU_FUSION_SHAPES}> FIBERCPU_OPERAND_MODE=${FIBERCPU_OPERAND_MODE} FIBERCPU_LOWER_OPERANDS=$<BOOL:${FIBERCPU_LOWER_OPERANDS}>)
target_compile_definitions(fibercpu PUBLIC FIBERCPU_GPR_PIN_MODE=${FIBERCPU_GPR_PIN_MODE})
target_compile_definitions(fibercpu PRIVATE FIBERCPU_OPERAND_MODE=${FIBERCPU_OPERAND_MODE})
target_include_directories(fibercpu PRIVATE src)
target_include_directories(fibercpu SYSTEM PRIVATE ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)

if(MSVC AND CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    # When using Clang-CL on Windows, we need to link the compiler-rt builtins
    # to support 128-bit integer operations like __udivti3.
    execute_process(
        COMMAND ${CMAKE_CXX_COMPILER} --print-resource-dir
        OUTPUT_VARIABLE CLANG_RESOURCE_DIR
        OUTPUT_STRIP_TRAILING_WHITESPACE
        ERROR_QUIET
    )
    if(CLANG_RESOURCE_DIR)
        file(TO_CMAKE_PATH "${CLANG_RESOURCE_DIR}" CLANG_RESOURCE_DIR)
        if(CMAKE_SYSTEM_PROCESSOR MATCHES "ARM64|aarch64")
            set(CLANG_RT_ARCH "aarch64")
        elseif(CMAKE_SIZEOF_VOID_P EQUAL 4)
            set(CLANG_RT_ARCH "i386")
        else()
            set(CLANG_RT_ARCH "x86_64")
        endif()
        
        set(CLANG_RT_BUILTINS "${CLANG_RESOURCE_DIR}/lib/windows/clang_rt.builtins-${CLANG_RT_ARCH}.lib")
        if(EXISTS "${CLANG_RT_BUILTINS}")
            message(STATUS "Linking Clang runtime builtins: ${CLANG_RT_BUILTINS}")
            target_link_libraries(fibercpu PRIVATE "${CLANG_RT_BUILTINS}")
        else()
            # Fallback for older Clang versions or different layouts
            file(GLOB CLANG_RT_LIBS "${CLANG_RESOURCE_DIR}/lib/windows/clang_rt.builtins-*.lib")
            if(CLANG_RT_LIBS)
                list(GET CLANG_RT_LIBS 0 CLANG_RT_BUILTIN_FALLBACK)
                message(STATUS "Linking Clang runtime builtin (fallback): ${CLANG_RT_BUILTIN_FALLBACK}")
                target_link_libraries(fibercpu PRIVATE "${CLANG_RT_BUILTIN_FALLBACK}")
            endif()
        endif()
    endif()
endif()

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(fibercpu PRIVATE
        $<$<COMPILE_LANGUAGE:CXX>:-Wall>
        $<$<COMPILE_LANGUAGE:CXX>:-Wextra>
        $<$<COMPILE_LANGUAGE:CXX>:-Wpedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-c++98-compat>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-c++98-compat-pedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-pre-c++17-compat>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-pre-c++20-compat-pedantic>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unused-parameter>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-nested-anon-types>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-old-style-cast>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-sign-conversion>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-covered-switch-default>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-zero-as-null-pointer-constant>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-reserved-macro-identifier>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-unsafe-buffer-usage>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-implicit-int-conversion>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-global-constructors>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-exit-time-destructors>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-format-nonliteral>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-missing-prototypes>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-tautological-type-limit-compare>
        $<$<COMPILE_LANGUAGE:CXX>:-Wno-newline-eof>
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

if(MSVC)
    target_compile_definitions(fibercpu PRIVATE _CRT_SECURE_NO_WARNINGS)
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
if(FIBERCPU_ENABLE_SEQUENCE_PROFILE)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_ENABLE_SEQUENCE_PROFILE=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_ENABLE_SEQUENCE_PROFILE=1)
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

option(FIBERCPU_BUILD_DECODE_BENCHMARK "Build the direct block decode benchmark" OFF)
if(FIBERCPU_BUILD_DECODE_BENCHMARK)
    add_executable(decode_benchmark ../benchmark/decode/decode_benchmark.cpp)
    target_include_directories(decode_benchmark PRIVATE src ${simde_SOURCE_DIR})
    target_link_libraries(decode_benchmark PRIVATE fibercpu unordered_dense::unordered_dense)
    foreach(feature ENABLE_TLB_STATS FIBERCPU_ENABLE_HANDLER_PROFILE FIBERCPU_ENABLE_SEQUENCE_PROFILE FIBERCPU_ENABLE_BLOCK_PROFILE)
        if(${feature})
            target_compile_definitions(decode_benchmark PRIVATE ${feature}=1)
        endif()
    endforeach()
endif()

option(FIBERCPU_TEST_SPECIALIZATION_INDEX "Build specialization lookup equivalence test" OFF)
if(FIBERCPU_TEST_SPECIALIZATION_INDEX)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_VALIDATE_SPECIALIZATION_INDEX=1)
    enable_testing()
    add_executable(specialization_index_test ../benchmark/decode/specialization_test.cpp)
    target_include_directories(specialization_index_test PRIVATE src ${simde_SOURCE_DIR})
    target_link_libraries(specialization_index_test PRIVATE fibercpu unordered_dense::unordered_dense)
    if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES AND
       FIBERCPU_GENERATED_WITH_HANDWRITTEN)
        target_compile_definitions(specialization_index_test PRIVATE FIBERCPU_TEST_GENERATED_SUPEROPCODES=1)
    endif()
    add_test(NAME specialization_index COMMAND specialization_index_test)
    add_executable(decode_storage_test ../benchmark/decode/decode_storage_test.cpp)
    target_include_directories(decode_storage_test PRIVATE src ${simde_SOURCE_DIR})
    target_link_libraries(decode_storage_test PRIVATE fibercpu unordered_dense::unordered_dense)
    foreach(feature ENABLE_TLB_STATS FIBERCPU_ENABLE_HANDLER_PROFILE FIBERCPU_ENABLE_SEQUENCE_PROFILE FIBERCPU_ENABLE_BLOCK_PROFILE)
        if(${feature})
            target_compile_definitions(decode_storage_test PRIVATE ${feature}=1)
        endif()
    endforeach()
    add_test(NAME decode_storage COMMAND decode_storage_test)
endif()
