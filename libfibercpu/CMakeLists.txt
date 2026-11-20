cmake_minimum_required(VERSION 3.20)

option(FIBERCPU_ENABLE_LLVM_JIT "Enable the experimental LLVM basic-block JIT" OFF)
set(FIBERCPU_LINKED_BLOCK_CACHE_FILE "" CACHE FILEPATH "Offline LLVM block cache to link into the interpreter")
# Keep native objects aligned with the NativeAOT macOS deployment target.
if(APPLE AND NOT FIBERCPU_ENABLE_LLVM_JIT AND NOT FIBERCPU_LINKED_BLOCK_CACHE_FILE)
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
option(FIBERCPU_STATIC_SPECIALIZATION "Build profile-baked native instruction variants" ON)
set(FIBERCPU_MODRM_PLAN_FILE "${CMAKE_CURRENT_SOURCE_DIR}/src/generated/modrm-specializations.json"
    CACHE FILEPATH "Profile-baked ModRM specialization table")
option(FIBERCPU_ENABLE_SUPEROPCODES "Enable generated SuperOpcode matching and handlers" ON)
option(FIBERCPU_ENABLE_SEQUENCE_PROFILE "Count actual unfused within-block Continue edges (training only)" OFF)
if(NOT FIBERCPU_STATIC_SPECIALIZATION)
    set(FIBERCPU_ENABLE_SUPEROPCODES OFF)
endif()
if(FIBERCPU_ENABLE_SEQUENCE_PROFILE AND FIBERCPU_ENABLE_SUPEROPCODES)
    message(FATAL_ERROR "Sequence training requires FIBERCPU_ENABLE_SUPEROPCODES=OFF")
endif()

if(MSVC AND FIBERCPU_BUILD_STATIC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "MSVC runtime library" FORCE)
endif()

# Independent compile-time choices; AUTO selects the production defaults for the target.
set(FIBERCPU_FUSION_STRATEGY "AUTO" CACHE STRING "Baked fusion set (AUTO, BASE, EXTENDED)")
set_property(CACHE FIBERCPU_FUSION_STRATEGY PROPERTY STRINGS AUTO BASE EXTENDED)
set(FIBERCPU_OPERAND_BUDGET "256" CACHE STRING "Maximum baked fusion operand shapes (0..1024)")
set(FIBERCPU_OPERAND_PLAN_FILE "" CACHE FILEPATH "Override the baked fusion operand plan")
set(FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE "" CACHE FILEPATH "Decoded operand plan for runtime templates (macOS arm64)")
option(FIBERCPU_ENABLE_DYNAMIC_SPECIALIZATION "Enable native runtime instruction specialization" ON)
set(FIBERCPU_GPR_PIN "AUTO" CACHE STRING "Guest GPR pin (AUTO: supported ARM64 only; ON, OFF, ABI_ONLY, CONSERVATIVE, EAX_ESP, WRITE_BACK)")
set_property(CACHE FIBERCPU_GPR_PIN PROPERTY STRINGS AUTO ON OFF ABI_ONLY CONSERVATIVE EAX_ESP WRITE_BACK)
option(FIBERCPU_GPR_WRITE_BACK "Use delayed GPR write-back when GPR pin is AUTO or ON" ON)
set(FIBERCPU_GPR_PIN_REGISTERS "EAX,ESP" CACHE STRING "Pinned guest GPRs (comma- or semicolon-separated)")
set(FIBERCPU_PIN_SPECIALIZATION "AUTO" CACHE STRING "Pin access coverage (AUTO, BASE, SCALAR, MEMORY)")
set_property(CACHE FIBERCPU_PIN_SPECIALIZATION PROPERTY STRINGS AUTO BASE SCALAR MEMORY)

# Sequence training and external fusion tables use their own configuration.
set(_production_optimizations ON)
if(FIBERCPU_ENABLE_SEQUENCE_PROFILE OR FIBERCPU_GENERATED_SUPEROPCODES_FILE)
    set(_production_optimizations OFF)
endif()

set(_can_pin_gprs OFF)
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND NOT EMSCRIPTEN AND NOT FIBERCPU_GPR_PIN STREQUAL "OFF" AND
   (NOT FIBERCPU_GPR_PIN STREQUAL "AUTO" OR _production_optimizations))
    include(CheckCXXSourceCompiles)
    set(_pin_abi_probe [=[
#if !(defined(__aarch64__) || defined(_M_ARM64) || defined(__x86_64__) || defined(_M_X64)) || !__has_attribute(preserve_none) || !__has_cpp_attribute(clang::musttail)
#error Native handler pin ABI unavailable
#endif
#pragma clang diagnostic error "-Wignored-attributes"
using U64 = unsigned long long;
using Handler = U64(__attribute__((preserve_none)) *)(void*, void*, U64, U64, U64, unsigned, U64, unsigned, unsigned);
__attribute__((preserve_none)) U64 dispatch(void* state, void* op, U64 limit, U64 tags, U64 addend, unsigned branch,
                                           U64 flags, unsigned eax, unsigned esp) {
    [[clang::musttail]] return (*static_cast<Handler*>(op))(state, op, limit, tags, addend, branch, flags, eax, esp);
}
int main() { return 0; }
]=])
    if(FIBERCPU_GPR_PIN STREQUAL "AUTO")
        # CoreMark regressed with pinning on Windows and Linux x86-64. Keep
        # x86-64 opt-in. Use the compiler target so cross/universal builds
        # don't accidentally enable pinning for an x86-64 slice.
        check_cxx_source_compiles("
#if !(defined(__aarch64__) || defined(_M_ARM64))
#error Automatic guest GPR pinning is only enabled on ARM64
#endif
${_pin_abi_probe}" FIBERCPU_HAS_AUTO_PIN_ABI)
        set(_can_pin_gprs ${FIBERCPU_HAS_AUTO_PIN_ABI})
    else()
        check_cxx_source_compiles("${_pin_abi_probe}" FIBERCPU_HAS_PIN_ABI)
        set(_can_pin_gprs ${FIBERCPU_HAS_PIN_ABI})
    endif()
endif()

if(FIBERCPU_GPR_PIN STREQUAL "AUTO" AND NOT _can_pin_gprs)
    set(FIBERCPU_GPR_PIN "OFF")
elseif(FIBERCPU_GPR_PIN MATCHES "^(AUTO|ON)$")
    if(FIBERCPU_GPR_WRITE_BACK)
        set(FIBERCPU_GPR_PIN "WRITE_BACK")
    else()
        set(FIBERCPU_GPR_PIN "EAX_ESP")
    endif()
endif()

# Normalize to architectural order. This fixes the parameter ABI independent
# of the spelling and order of names in the user's list.
set(_guest_gprs EAX ECX EDX EBX ESP EBP ESI EDI)
string(TOUPPER "${FIBERCPU_GPR_PIN_REGISTERS}" _requested_pin_regs)
string(STRIP "${_requested_pin_regs}" _requested_pin_regs)
string(REGEX REPLACE "[,; 	]+" ";" _requested_pin_regs "${_requested_pin_regs}")
if(NOT _requested_pin_regs)
    message(FATAL_ERROR "FIBERCPU_GPR_PIN_REGISTERS must name at least one guest GPR; use GPR_PIN=OFF to disable pin")
endif()
set(_seen_pin_regs "")
foreach(_reg IN LISTS _requested_pin_regs)
    if(NOT _reg IN_LIST _guest_gprs)
        message(FATAL_ERROR "Unknown guest GPR '${_reg}' in FIBERCPU_GPR_PIN_REGISTERS")
    endif()
    if(_reg IN_LIST _seen_pin_regs)
        message(FATAL_ERROR "Duplicate guest GPR '${_reg}' in FIBERCPU_GPR_PIN_REGISTERS")
    endif()
    list(APPEND _seen_pin_regs "${_reg}")
endforeach()

set(_active_pin_regs "")
set(_pin_gpr_ids "")
set(_pin_mask 0)
set(_pin_items "")
set(_pin_members "")
set(_pin_slots "")
set(_pin_count 0)
if(NOT FIBERCPU_GPR_PIN STREQUAL "OFF")
    foreach(_reg IN LISTS _guest_gprs)
        if(_reg IN_LIST _requested_pin_regs)
            list(FIND _guest_gprs "${_reg}" _id)
            string(TOLOWER "${_reg}" _field)
            list(APPEND _active_pin_regs "${_reg}")
            list(APPEND _pin_gpr_ids "${_id}")
            list(APPEND _pin_members "((index) == ${_id}u)")
            math(EXPR _pin_mask "${_pin_mask} | (1 << ${_id})")
            string(APPEND _pin_items " X(${_id}, ${_field}, ${_pin_count}, __VA_ARGS__)")
            string(APPEND _pin_slots "#define FIBERCPU_PIN_ID_${_pin_count} ${_id}u\n#define FIBERCPU_PIN_FIELD_${_pin_count} ${_field}\n")
            math(EXPR _pin_count "${_pin_count} + 1")
        endif()
    endforeach()
endif()
list(JOIN _active_pin_regs "," _pin_registers)
if(NOT _pin_count)
    set(_pin_registers "none")
endif()
math(EXPR _pin_keep_mask "(1 << ${_pin_count}) - 1")
list(JOIN _pin_members " || " _pin_membership)
if(_pin_count EQUAL 2)
    list(GET _pin_gpr_ids 0 _first_pin)
    list(GET _pin_gpr_ids 1 _second_pin)
    math(EXPR _different_bits "${_first_pin} ^ ${_second_pin}")
    math(EXPR _remaining_bits "${_different_bits} & (${_different_bits} - 1)")
    if(_remaining_bits EQUAL 0)
        set(_pin_membership "((index) & ~${_different_bits}u) == ${_first_pin}u")
    endif()
endif()

file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
if(_pin_count)
    file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/generated/gpr_pins.generated.h" CONTENT
"#pragma once
#define FIBERCPU_PIN_COUNT @_pin_count@
#define FIBERCPU_PIN_KEEP_MASK @_pin_keep_mask@u
#define FIBERCPU_FOR_EACH_PIN(X, ...)@_pin_items@
#define FIBERCPU_PIN_CONTAINS(index) (@_pin_membership@)
@_pin_slots@" @ONLY)
endif()

if(FIBERCPU_FUSION_STRATEGY STREQUAL "AUTO" OR FIBERCPU_FUSION_STRATEGY STREQUAL "SHAPED")
    set(FIBERCPU_FUSION_STRATEGY "EXTENDED")
endif()
if(NOT FIBERCPU_FUSION_STRATEGY MATCHES "^(BASE|EXTENDED)$")
    message(FATAL_ERROR "Unknown FIBERCPU_FUSION_STRATEGY")
endif()
if(FIBERCPU_PIN_SPECIALIZATION STREQUAL "AUTO")
    set(FIBERCPU_PIN_SPECIALIZATION "MEMORY")
endif()
set(_pin_specializations BASE SCALAR MEMORY)
list(FIND _pin_specializations "${FIBERCPU_PIN_SPECIALIZATION}" FIBERCPU_PIN_SPECIALIZATION_MODE)
set(_pin_modes OFF ABI_ONLY CONSERVATIVE EAX_ESP WRITE_BACK)
list(FIND _pin_modes "${FIBERCPU_GPR_PIN}" FIBERCPU_GPR_PIN_MODE)
if(FIBERCPU_PIN_SPECIALIZATION_MODE LESS 0 OR FIBERCPU_GPR_PIN_MODE LESS 0)
    message(FATAL_ERROR "Invalid pin specialization or GPR pin mode")
endif()
if(FIBERCPU_GPR_PIN_MODE GREATER 0 AND NOT _can_pin_gprs)
    message(FATAL_ERROR "GPR pin requires a Clang target that supports the preserve_none tail-call handler ABI")
endif()
if(FIBERCPU_OPERAND_BUDGET STREQUAL "AUTO")
    set(FIBERCPU_OPERAND_BUDGET "256")
endif()
if(NOT FIBERCPU_OPERAND_BUDGET MATCHES "^[0-9]+$" OR FIBERCPU_OPERAND_BUDGET GREATER 1024)
    message(FATAL_ERROR "Invalid baked fusion operand budget")
endif()
set(FIBERCPU_LOWER_OPERANDS ${FIBERCPU_ENABLE_SUPEROPCODES})
set(FIBERCPU_OPERAND_MODE 0)
if(FIBERCPU_LOWER_OPERANDS)
    set(FIBERCPU_OPERAND_MODE 1)
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

set(_instruction_catalog "${CMAKE_CURRENT_SOURCE_DIR}/src/instruction_catalog.json")
set(_catalog_generator "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_instruction_catalog.py")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_instruction_catalog}")
file(READ "${_instruction_catalog}" _catalog_schema)
string(JSON _catalog_source_count ERROR_VARIABLE _catalog_source_error LENGTH "${_catalog_schema}" specialization_sources)
set(_catalog_inputs "${_instruction_catalog}")
set(_catalog_fragments "")
if(NOT _catalog_source_error AND _catalog_source_count GREATER 0)
    math(EXPR _catalog_source_last "${_catalog_source_count} - 1")
    foreach(index RANGE "${_catalog_source_last}")
        string(JSON source GET "${_catalog_schema}" specialization_sources "${index}")
        list(APPEND _catalog_fragments "${source}")
        list(APPEND _catalog_inputs "${CMAKE_CURRENT_SOURCE_DIR}/src/${source}")
    endforeach()
endif()
set(_catalog_fusion_args "")
set(_catalog_fusion_input "")
if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    set(_catalog_fusion_input "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(_catalog_fusion_args --fusion-source "${_catalog_fusion_input}")
endif()
set(_catalog_products "${CMAKE_CURRENT_BINARY_DIR}/generated/instruction_families.h"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/instruction_catalog.inc"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/instruction_kernel_bindings.inc"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/llvm_block_operations.inc"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/llvm_block_entries.inc"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/llvm_op_ids.h"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/training_scalar_handlers.inc"
                      "${CMAKE_CURRENT_BINARY_DIR}/generated/training_fusion_handlers.inc")
# Keep the scalar trampolines and baked bindings in separate translation units.
set(_scalar_bindings)
foreach(_shard RANGE 0 31)
    list(APPEND _scalar_bindings "${CMAKE_CURRENT_BINARY_DIR}/generated/scalar_bindings_${_shard}.cpp")
endforeach()
list(APPEND _catalog_products ${_scalar_bindings})
set(_modrm_plan_args)
set(_modrm_plan_input)
if(FIBERCPU_STATIC_SPECIALIZATION)
    set(_modrm_plan_input "${FIBERCPU_MODRM_PLAN_FILE}")
    set(_modrm_plan_args --modrm-plan "${_modrm_plan_input}")
endif()
add_custom_command(OUTPUT ${_catalog_products}
    COMMAND "${Python3_EXECUTABLE}" "${_catalog_generator}" --catalog "${_instruction_catalog}"
        --output "${CMAKE_CURRENT_BINARY_DIR}/generated" --pin-mode "${FIBERCPU_PIN_SPECIALIZATION_MODE}"
        --pin-mask "${_pin_mask}" ${_catalog_fusion_args} ${_modrm_plan_args}
    DEPENDS "${_catalog_generator}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/operand_specialization_data.py"
        "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/scalar_modrm.py" "${CMAKE_CURRENT_SOURCE_DIR}/src/decoder_lut.h"
        ${_catalog_inputs} ${_catalog_fusion_input} ${_modrm_plan_input}
    VERBATIM)
add_custom_target(fibercpu_instruction_catalog DEPENDS ${_catalog_products})

# Source files
file(GLOB_RECURSE SOURCES CONFIGURE_DEPENDS RELATIVE "${CMAKE_CURRENT_SOURCE_DIR}" "src/*.cpp" "src/*.c" "src/*.h")
list(APPEND SOURCES ${_scalar_bindings})
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "build/.*")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")
list(FILTER SOURCES EXCLUDE REGEX "generated/superopcodes.*\\.cpp$")
list(FILTER SOURCES EXCLUDE REGEX "^src/jit/")
if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
    set(_operand_output "${CMAKE_CURRENT_BINARY_DIR}/generated/operand_specializations.inc")
    set(_operand_source "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    set(_combined "${CMAKE_CURRENT_BINARY_DIR}/generated/optimized_handlers.cpp")
    file(MAKE_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/generated")
    file(WRITE "${_combined}" "#include \"${_operand_source}\"\n#include \"${_operand_output}\"\n")
    set(_plan "${FIBERCPU_OPERAND_PLAN_FILE}")
    if(NOT _plan)
        set(_plan "${CMAKE_CURRENT_SOURCE_DIR}/../benchmark/optimization/manifests/baked-fusion-operands-v3.json")
    endif()
    set(_operand_options "${CMAKE_CURRENT_BINARY_DIR}/generated/operand-options.txt")
    file(GENERATE OUTPUT "${_operand_options}" CONTENT
        "${_operand_source};${_plan};${FIBERCPU_OPERAND_BUDGET};${FIBERCPU_PIN_SPECIALIZATION_MODE};${_pin_mask}\n")
    add_custom_command(OUTPUT "${_operand_output}"
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_operand_specializations.py"
            --source "${_operand_source}" --plan "${_plan}" --catalog "${_instruction_catalog}"
            --output "${_operand_output}" --budget "${FIBERCPU_OPERAND_BUDGET}"
            --pin-specialization "${FIBERCPU_PIN_SPECIALIZATION_MODE}" --pin-mask "${_pin_mask}"
        DEPENDS "${_operand_source}" "${_plan}" "${_operand_options}"
            "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_operand_specializations.py"
            "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/operand_specialization_data.py"
            "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/scalar_modrm.py" ${_catalog_inputs} "${_catalog_generator}"
        VERBATIM)
    set_source_files_properties("${_combined}" PROPERTIES OBJECT_DEPENDS "${_operand_output}")
    list(APPEND SOURCES "${_combined}" "${_operand_output}")
endif()
list(FILTER SOURCES EXCLUDE REGEX "handler_reflection_exports\\.cpp$")
list(FILTER SOURCES EXCLUDE REGEX "dynamic_operand_specialization\\.cpp$")

if(NOT MSVC AND NOT EMSCRIPTEN)
    add_compile_options(
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-O3>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fomit-frame-pointer>"
        "$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-fno-stack-protector>"
    )
    if(NOT CMAKE_CROSSCOMPILING AND
       (NOT CMAKE_OSX_ARCHITECTURES OR CMAKE_OSX_ARCHITECTURES STREQUAL CMAKE_HOST_SYSTEM_PROCESSOR))
        add_compile_options("$<$<OR:$<CONFIG:Release>,$<CONFIG:RelWithDebugInfo>>:-march=native>")
    endif()
endif()

# Fingerprint the semantic catalog independently of host addresses/compiler ABI.
set(INSTRUCTION_CATALOG_INPUTS
    instruction_spec.h instruction_kernel_spec.h instruction_catalog.json instruction_variants.cpp instruction_variants.h
    specialization.h ${_catalog_fragments})
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
set(_operand_plan_source "stock")
if(FIBERCPU_OPERAND_PLAN_FILE)
    set(_operand_plan_source "explicit")
endif()
if(FIBERCPU_LOWER_OPERANDS)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_plan}")
    file(SHA256 "${_plan}" _operand_plan_hash)
endif()
set(_modrm_hash "none")
if(_modrm_plan_input)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_modrm_plan_input}")
    file(SHA256 "${_modrm_plan_input}" _modrm_hash)
endif()
set(FIBERCPU_OPTIMIZATION_CONFIG "static=${FIBERCPU_STATIC_SPECIALIZATION};fusion=${FIBERCPU_FUSION_STRATEGY};enabled=${FIBERCPU_ENABLE_SUPEROPCODES};operand=${FIBERCPU_OPERAND_MODE};budget=${FIBERCPU_OPERAND_BUDGET};pin=${FIBERCPU_GPR_PIN};pins=${_pin_registers};coverage=${FIBERCPU_PIN_SPECIALIZATION};plan_source=${_operand_plan_source};plan=${_operand_plan_hash};modrm=${_modrm_hash}")
set(FIBERCPU_DYNAMIC_OPERANDS OFF)
if(FIBERCPU_ENABLE_DYNAMIC_SPECIALIZATION AND FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE)
    if(APPLE AND NOT CMAKE_CROSSCOMPILING AND CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "arm64" AND
       (NOT CMAKE_OSX_ARCHITECTURES OR CMAKE_OSX_ARCHITECTURES STREQUAL "arm64") AND
       CMAKE_CXX_COMPILER_ID MATCHES "Clang" AND PGO_MODE STREQUAL "OFF")
        set(FIBERCPU_DYNAMIC_OPERANDS ON)
    else()
        message(STATUS "Dynamic instruction specialization unavailable for this toolchain")
    endif()
endif()
if(FIBERCPU_DYNAMIC_OPERANDS)
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE}")
    file(SHA256 "${FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE}" _dynamic_plan_hash)
    string(APPEND FIBERCPU_OPTIMIZATION_CONFIG ";dynamic_plan=${_dynamic_plan_hash}")
endif()
file(WRITE "${CMAKE_CURRENT_BINARY_DIR}/generated/optimization_config.h"
    "#pragma once\n#define FIBERCPU_OPTIMIZATION_CONFIG \"${FIBERCPU_OPTIMIZATION_CONFIG}\"\n")

# Object library for core parts (contains the opcodes to scan)
add_library(fibercpu_core OBJECT ${SOURCES} ${_catalog_products})
add_dependencies(fibercpu_core fibercpu_instruction_catalog)
target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_STATIC_SPECIALIZATION=$<BOOL:${FIBERCPU_STATIC_SPECIALIZATION}>)
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
if(APPLE AND CMAKE_CXX_COMPILER_ID STREQUAL "AppleClang" AND NOT EMSCRIPTEN)
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

set(FIBERCPU_MODRM_TRAIN_PODISH "" CACHE FILEPATH "Published Podish.Cli.dll for short ModRM training")
set(FIBERCPU_MODRM_TRAIN_ROOTFS "" CACHE PATH "Prepared IA-32 Node/Webpack rootfs for short ModRM training")
set(FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS "" CACHE PATH "Prepared IA-32 GNU and CoreMark rootfs for full ModRM training")
if(FIBERCPU_MODRM_TRAIN_PODISH OR FIBERCPU_MODRM_TRAIN_ROOTFS OR FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS)
    if(NOT EXISTS "${FIBERCPU_MODRM_TRAIN_PODISH}" OR NOT IS_DIRECTORY "${FIBERCPU_MODRM_TRAIN_ROOTFS}")
        message(FATAL_ERROR "ModRM training requires FIBERCPU_MODRM_TRAIN_PODISH and FIBERCPU_MODRM_TRAIN_ROOTFS")
    endif()
    if(NOT FIBERCPU_ENABLE_BLOCK_PROFILE OR FIBERCPU_BUILD_STATIC OR CMAKE_CROSSCOMPILING)
        message(FATAL_ERROR "ModRM training requires a host shared build with FIBERCPU_ENABLE_BLOCK_PROFILE=ON")
    endif()
    find_program(FIBERCPU_MODRM_TRAIN_DOTNET dotnet REQUIRED)
    set(_modrm_training_output "${CMAKE_CURRENT_BINARY_DIR}/modrm-training/$<CONFIG>")
    set(_modrm_training_fusions)
    if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
        list(APPEND _modrm_training_fusions --fusions "${FIBERCPU_GENERATED_SUPEROPCODES_FILE}")
    endif()
    set(_modrm_training_command
        "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/run_modrm_training.py"
        --dotnet "${FIBERCPU_MODRM_TRAIN_DOTNET}" --podish "${FIBERCPU_MODRM_TRAIN_PODISH}"
        --library "$<TARGET_FILE:fibercpu>" --rootfs "${FIBERCPU_MODRM_TRAIN_ROOTFS}"
        ${_modrm_training_fusions})
    add_custom_target(fibercpu_train_modrm
        COMMAND ${_modrm_training_command} --output "${_modrm_training_output}"
        DEPENDS fibercpu
        BYPRODUCTS "${_modrm_training_output}/blocks.bin" "${_modrm_training_output}/modrm-hotness.json"
        COMMENT "Capturing a short Node.js ModRM training profile"
        USES_TERMINAL
        VERBATIM
    )
    if(FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS)
        if(NOT IS_DIRECTORY "${FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS}")
            message(FATAL_ERROR "FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS must be a prepared rootfs directory")
        endif()
        set(_modrm_suite_output "${CMAKE_CURRENT_BINARY_DIR}/modrm-training-suite/$<CONFIG>")
        add_custom_target(fibercpu_train_modrm_suite
            COMMAND ${_modrm_training_command} --suite-rootfs "${FIBERCPU_MODRM_TRAIN_SUITE_ROOTFS}"
                    --output "${_modrm_suite_output}"
            DEPENDS fibercpu
            BYPRODUCTS "${_modrm_suite_output}/modrm-hotness.json"
            COMMENT "Capturing the ModRM training suite"
            USES_TERMINAL
            VERBATIM
        )
    endif()
endif()

target_compile_definitions(fibercpu PRIVATE FIBERCPU_SUPEROPCODE_SET_SHA256="${FIBERCPU_SUPEROPCODE_SET_SHA256}")
target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_SUPEROPCODE_SET_SHA256="${FIBERCPU_SUPEROPCODE_SET_SHA256}")
target_compile_definitions(fibercpu_core PRIVATE)
target_include_directories(fibercpu_core PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
set_target_properties(fibercpu PROPERTIES POSITION_INDEPENDENT_CODE ON)
if(WIN32 AND NOT FIBERCPU_BUILD_STATIC)
    # The managed runtime loads fibercpu.dll via P/Invoke and expects the X86_* symbols
    # to be exported directly on Windows shared builds.
    set_target_properties(fibercpu PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
endif()

target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_GPR_PIN_MODE=${FIBERCPU_GPR_PIN_MODE}
    FIBERCPU_OPERAND_MODE=${FIBERCPU_OPERAND_MODE} FIBERCPU_LOWER_OPERANDS=$<BOOL:${FIBERCPU_LOWER_OPERANDS}>)
target_compile_definitions(fibercpu PUBLIC FIBERCPU_GPR_PIN_MODE=${FIBERCPU_GPR_PIN_MODE})
target_compile_definitions(fibercpu PRIVATE FIBERCPU_STATIC_SPECIALIZATION=$<BOOL:${FIBERCPU_STATIC_SPECIALIZATION}>)
target_compile_definitions(fibercpu PRIVATE FIBERCPU_LOWER_OPERANDS=$<BOOL:${FIBERCPU_LOWER_OPERANDS}>)
target_compile_definitions(fibercpu PRIVATE FIBERCPU_OPERAND_MODE=${FIBERCPU_OPERAND_MODE})
target_include_directories(fibercpu PRIVATE src PUBLIC "$<BUILD_INTERFACE:${CMAKE_CURRENT_BINARY_DIR}/generated>")
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

if(FIBERCPU_DYNAMIC_OPERANDS OR FIBERCPU_ENABLE_LLVM_JIT)
    target_sources(fibercpu PRIVATE src/jit/executable_arena.cpp)
endif()

if(FIBERCPU_ENABLE_LLVM_JIT OR FIBERCPU_LINKED_BLOCK_CACHE_FILE)
    if(NOT APPLE OR CMAKE_CROSSCOMPILING OR NOT CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "arm64" OR
       (CMAKE_OSX_ARCHITECTURES AND NOT CMAKE_OSX_ARCHITECTURES STREQUAL "arm64") OR
       NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang" OR NOT PGO_MODE STREQUAL "OFF" OR
       FIBERCPU_ENABLE_HANDLER_PROFILE OR FIBERCPU_ENABLE_SEQUENCE_PROFILE)
        message(FATAL_ERROR "LLVM block JIT and linked block caches require native macOS arm64, upstream Clang, and no PGO or handler tracing")
    endif()
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_BLOCK_COMPILATION=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_BLOCK_COMPILATION=1)
    target_sources(fibercpu PRIVATE src/jit/block_recipe.cpp)

    # Linked machine code embeds VM structure offsets and uses the native threaded ABI.
    # Hash the sources which define those contracts and the relevant compiler settings
    # without depending on LLVM or a binary built for this machine.
    set(_linked_abi "fibercpu-linked-block-cache-v1")
    foreach(setting CMAKE_CXX_COMPILER_VERSION CMAKE_OSX_DEPLOYMENT_TARGET
                    CMAKE_BUILD_TYPE CMAKE_CXX_FLAGS CMAKE_CXX_FLAGS_DEBUG
                    CMAKE_CXX_FLAGS_RELEASE CMAKE_CXX_FLAGS_MINSIZEREL CMAKE_CXX_FLAGS_RELWITHDEBINFO
                    USE_SOFTFLOAT ENABLE_TLB_STATS FIBERCPU_ENABLE_BLOCK_PROFILE
                    FIBERCPU_GPR_PIN_MODE _pin_registers FIBERCPU_PIN_SPECIALIZATION
                    FIBERCPU_ENABLE_HANDLER_PROFILE FIBERCPU_ENABLE_SEQUENCE_PROFILE
                    INSTRUCTION_CATALOG_SHA256)
        string(APPEND _linked_abi "
${setting}=${${setting}}")
    endforeach()
    file(GLOB_RECURSE _linked_abi_sources RELATIVE "${CMAKE_CURRENT_SOURCE_DIR}"
        CONFIGURE_DEPENDS "src/*.h")
    list(APPEND _linked_abi_sources CMakeLists.txt src/decoder.cpp src/jit/block_recipe.cpp src/jit/llvm_block_ir.cpp
        src/jit/llvm_block_jit.cpp src/jit/llvm_operand_ir.cpp ../scripts/gen_instruction_catalog.py)
    foreach(source IN LISTS _linked_abi_sources)
        set(path "${CMAKE_CURRENT_SOURCE_DIR}/${source}")
        set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${path}")
        file(SHA256 "${path}" source_hash)
        string(APPEND _linked_abi "
${source}=${source_hash}")
    endforeach()
    string(SHA256 _linked_abi_sha256 "${_linked_abi}")
    file(CONFIGURE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/generated/linked_block_cache_abi.h" CONTENT
        [=[#pragma once
#define FIBERCPU_LINKED_BLOCK_CACHE_ABI "@_linked_abi_sha256@"
]=] @ONLY)
endif()

if(FIBERCPU_LINKED_BLOCK_CACHE_FILE)
    get_filename_component(_linked_cache "${FIBERCPU_LINKED_BLOCK_CACHE_FILE}" ABSOLUTE)
    set(_linked_script "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/extract_linked_block_cache.py")
    set(_linked_dir "${CMAKE_CURRENT_BINARY_DIR}/generated/linked_cache")
    set(_linked_table "${_linked_dir}/linked_block_cache.inc")
    set(_linked_args "${_linked_script}" --input "${_linked_cache}" --abi "${_linked_abi_sha256}"
        --catalog "${_instruction_catalog}")
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${_linked_cache}" "${_linked_script}" ${_catalog_inputs})
    execute_process(COMMAND "${Python3_EXECUTABLE}" ${_linked_args}
        OUTPUT_VARIABLE _linked_count OUTPUT_STRIP_TRAILING_WHITESPACE
        COMMAND_ERROR_IS_FATAL ANY)
    math(EXPR _linked_last "${_linked_count} - 1")
    set(_linked_objects "")
    foreach(index RANGE 0 ${_linked_last})
        list(APPEND _linked_objects "${_linked_dir}/block_${index}.o")
    endforeach()
    add_custom_command(OUTPUT "${_linked_table}" ${_linked_objects}
        COMMAND "${Python3_EXECUTABLE}" ${_linked_args} --output "${_linked_dir}"
        DEPENDS "${_linked_cache}" "${_linked_script}"
                "${CMAKE_CURRENT_BINARY_DIR}/generated/linked_block_cache_abi.h"
        VERBATIM)
    set_source_files_properties(${_linked_objects} PROPERTIES EXTERNAL_OBJECT TRUE)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_LINKED_BLOCK_CACHE=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_LINKED_BLOCK_CACHE=1)
    target_include_directories(fibercpu PRIVATE "${_linked_dir}")
    target_sources(fibercpu PRIVATE src/jit/linked_block_cache.cpp "${_linked_table}" ${_linked_objects})
    message(STATUS "Linking ${_linked_count} precompiled LLVM block objects")
endif()

if(FIBERCPU_DYNAMIC_OPERANDS OR FIBERCPU_ENABLE_LLVM_JIT)
    find_package(LLVM REQUIRED CONFIG)
    string(REGEX MATCH "^[0-9]+" _clang_major "${CMAKE_CXX_COMPILER_VERSION}")
    if(NOT CMAKE_CXX_COMPILER_ID STREQUAL "Clang" OR LLVM_VERSION_MAJOR LESS 21 OR
       NOT _clang_major STREQUAL "${LLVM_VERSION_MAJOR}")
        message(FATAL_ERROR "LLVM templates require upstream Clang and LLVM with matching major versions (21 or newer)")
    endif()
    if(TARGET LLVM)
        set(_llvm_components LLVM)
    else()
        llvm_map_components_to_libnames(_llvm_components native orcjit passes)
    endif()

    add_library(fibercpu_block_ir OBJECT src/jit/llvm_block_ir.cpp)
    add_dependencies(fibercpu_block_ir fibercpu_instruction_catalog)
    set_target_properties(fibercpu_block_ir PROPERTIES INTERPROCEDURAL_OPTIMIZATION OFF POSITION_INDEPENDENT_CODE ON)
    target_compile_definitions(fibercpu_block_ir PRIVATE $<TARGET_PROPERTY:fibercpu_core,COMPILE_DEFINITIONS>)
    target_compile_options(fibercpu_block_ir PRIVATE $<TARGET_PROPERTY:fibercpu_core,COMPILE_OPTIONS>
        -O1 -fno-lto -fno-stack-protector -emit-llvm)
    target_include_directories(fibercpu_block_ir PRIVATE $<TARGET_PROPERTY:fibercpu_core,INCLUDE_DIRECTORIES>)
    target_link_libraries(fibercpu_block_ir PRIVATE unordered_dense::unordered_dense)

    add_library(fibercpu_llvm_codegen OBJECT src/jit/llvm_operand_ir.cpp)
    set_target_properties(fibercpu_llvm_codegen PROPERTIES POSITION_INDEPENDENT_CODE ON)
    add_dependencies(fibercpu_llvm_codegen fibercpu_instruction_catalog)
    target_compile_definitions(fibercpu_llvm_codegen PRIVATE $<TARGET_PROPERTY:fibercpu_core,COMPILE_DEFINITIONS>)
    target_include_directories(fibercpu_llvm_codegen PRIVATE src $<TARGET_PROPERTY:fibercpu_core,INCLUDE_DIRECTORIES>)
    target_include_directories(fibercpu_llvm_codegen SYSTEM PRIVATE ${LLVM_INCLUDE_DIRS})
    target_link_libraries(fibercpu_llvm_codegen PRIVATE unordered_dense::unordered_dense)

    set(_llvm_dir "${CMAKE_CURRENT_BINARY_DIR}/generated")
    if(CMAKE_CONFIGURATION_TYPES)
        string(APPEND _llvm_dir "/$<CONFIG>")
    endif()
endif()

if(FIBERCPU_DYNAMIC_OPERANDS)
    add_executable(fibercpu_operand_stencils src/jit/llvm_operand_stencil_main.cpp)
    target_compile_definitions(fibercpu_operand_stencils PRIVATE $<TARGET_PROPERTY:fibercpu_core,COMPILE_DEFINITIONS>)
    target_include_directories(fibercpu_operand_stencils PRIVATE src $<TARGET_PROPERTY:fibercpu_core,INCLUDE_DIRECTORIES>)
    target_include_directories(fibercpu_operand_stencils SYSTEM PRIVATE ${LLVM_INCLUDE_DIRS})
    target_link_libraries(fibercpu_operand_stencils PRIVATE fibercpu_llvm_codegen unordered_dense::unordered_dense ${_llvm_components})

    set(_dynamic_script "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/gen_dynamic_operand_specializations.py")
    set(_dynamic_data "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/operand_specialization_data.py")
    set(_dynamic_arm64 "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/operand_stencil_arm64.py")
    set(_bundle_dir "${_llvm_dir}/dynamic_operands")
    set(_requests "${_bundle_dir}/requests.txt")
    set(_manifest "${_bundle_dir}/manifest.json")
    set(_objects "${_bundle_dir}/objects.bin")
    set(_bundle "${_bundle_dir}/dynamic_operand_bundle.inc")
    set(_report "${_bundle_dir}/report.json")
    set(_dynamic_fusion_args "")
    if(_catalog_fusion_input)
        set(_dynamic_fusion_args --fusions "${_catalog_fusion_input}")
    endif()
    add_custom_command(OUTPUT "${_requests}" "${_manifest}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_bundle_dir}"
        COMMAND "${Python3_EXECUTABLE}" "${_dynamic_script}" plan
            --src "${CMAKE_CURRENT_SOURCE_DIR}/src" --plan "${FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE}"
            --requests "${_requests}" --manifest "${_manifest}" ${_dynamic_fusion_args}
        DEPENDS "${FIBERCPU_DYNAMIC_OPERAND_PLAN_FILE}" "${_dynamic_script}" "${_dynamic_data}"
            "${_dynamic_arm64}" ${_catalog_fusion_input} ${_catalog_inputs} "${_catalog_generator}"
        VERBATIM)
    add_custom_command(OUTPUT "${_objects}"
        COMMAND fibercpu_operand_stencils $<TARGET_OBJECTS:fibercpu_block_ir> "${_requests}" "${_objects}"
        DEPENDS fibercpu_operand_stencils fibercpu_block_ir $<TARGET_OBJECTS:fibercpu_block_ir> "${_requests}"
        VERBATIM)
    add_custom_command(OUTPUT "${_bundle}" "${_report}"
        COMMAND "${Python3_EXECUTABLE}" "${_dynamic_script}" bundle --src "${CMAKE_CURRENT_SOURCE_DIR}/src"
            --manifest "${_manifest}" --output "${_bundle}" --report "${_report}" "${_objects}"
        DEPENDS "${_objects}" "${_manifest}" "${_dynamic_script}" "${_dynamic_data}" "${_dynamic_arm64}"
        VERBATIM)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_DYNAMIC_OPERANDS=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_DYNAMIC_OPERANDS=1)
    target_include_directories(fibercpu PRIVATE "${_bundle_dir}")
    target_sources(fibercpu PRIVATE src/dynamic_operand_specialization.cpp "${_bundle}")
endif()

if(FIBERCPU_ENABLE_LLVM_JIT)
    set(_llvm_bitcode "${_llvm_dir}/llvm_block_bitcode.inc")
    set(_embed_script "${CMAKE_CURRENT_SOURCE_DIR}/../scripts/embed_llvm_bitcode.py")
    add_custom_command(OUTPUT "${_llvm_bitcode}"
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${_llvm_dir}"
        COMMAND "${Python3_EXECUTABLE}" "${_embed_script}" $<TARGET_OBJECTS:fibercpu_block_ir> "${_llvm_bitcode}"
        DEPENDS fibercpu_block_ir $<TARGET_OBJECTS:fibercpu_block_ir> "${_embed_script}"
        COMMAND_EXPAND_LISTS VERBATIM)
    target_compile_definitions(fibercpu_core PRIVATE FIBERCPU_LLVM_JIT=1)
    target_compile_definitions(fibercpu PRIVATE FIBERCPU_LLVM_JIT=1)
    target_include_directories(fibercpu PRIVATE "${_llvm_dir}")
    target_include_directories(fibercpu SYSTEM PRIVATE ${LLVM_INCLUDE_DIRS})
    target_sources(fibercpu PRIVATE src/jit/llvm_block_jit.cpp src/jit/llvm_block_memory.cpp "${_llvm_bitcode}")
    target_link_libraries(fibercpu PRIVATE fibercpu_llvm_codegen ${_llvm_components})
    add_executable(fibercpu_llvm_cache src/jit/llvm_cache_main.cpp)
    target_include_directories(fibercpu_llvm_cache PRIVATE src)
    target_link_libraries(fibercpu_llvm_cache PRIVATE fibercpu)
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
    if(FIBERCPU_DYNAMIC_OPERANDS)
        target_compile_definitions(fibercpu PRIVATE FIBERCPU_VALIDATE_SPECIALIZATION_INDEX=1)
        target_compile_definitions(specialization_index_test PRIVATE FIBERCPU_DYNAMIC_OPERANDS=1)
    endif()
    if(FIBERCPU_HAVE_GENERATED_SUPEROPCODES)
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
    if(FIBERCPU_ENABLE_LLVM_JIT OR FIBERCPU_LINKED_BLOCK_CACHE_FILE)
        add_executable(llvm_block_test ../benchmark/decode/llvm_block_test.cpp)
        target_include_directories(llvm_block_test PRIVATE src)
        target_include_directories(llvm_block_test SYSTEM PRIVATE ${simde_SOURCE_DIR})
        target_link_libraries(llvm_block_test PRIVATE fibercpu)
        add_test(NAME llvm_block_disabled COMMAND llvm_block_test)
        set_tests_properties(llvm_block_disabled PROPERTIES
            ENVIRONMENT "FIBERCPU_LLVM_JIT=0;FIBERCPU_LINKED_BLOCK_CACHE=0")
    endif()
    if(FIBERCPU_ENABLE_LLVM_JIT)
        target_compile_definitions(llvm_block_test PRIVATE FIBERCPU_LLVM_JIT=1)
        add_test(NAME llvm_block_generic COMMAND llvm_block_test enabled)
        set_tests_properties(llvm_block_generic PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=generic;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_specialized COMMAND llvm_block_test enabled)
        set_tests_properties(llvm_block_specialized PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_hot COMMAND llvm_block_test hot)
        set_tests_properties(llvm_block_hot PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=4;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_reuse COMMAND llvm_block_test reuse)
        set_tests_properties(llvm_block_reuse PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_reuse_generic COMMAND llvm_block_test reuse)
        set_tests_properties(llvm_block_reuse_generic PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=generic;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_budget COMMAND llvm_block_test budget)
        set_tests_properties(llvm_block_budget PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_packed COMMAND llvm_block_test packed)
        set_tests_properties(llvm_block_packed PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=0")
        add_test(NAME llvm_block_async COMMAND llvm_block_test async)
        set_tests_properties(llvm_block_async PROPERTIES ENVIRONMENT
            "FIBERCPU_LLVM_JIT=1;FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1;FIBERCPU_LLVM_JIT_WORKERS=2")
        set(_weighted_test_profile "${CMAKE_CURRENT_BINARY_DIR}/llvm-weighted-block-test.profile")
        add_test(NAME llvm_cpu_run_options COMMAND llvm_block_test options "${_weighted_test_profile}")
        set_tests_properties(llvm_cpu_run_options PROPERTIES FIXTURES_SETUP llvm_weighted_profile
            ENVIRONMENT "FIBERCPU_LLVM_JIT=0;FIBERCPU_LINKED_BLOCK_CACHE=0")
        add_test(NAME llvm_block_weighted_bundle COMMAND fibercpu_llvm_cache --linked
            "${_weighted_test_profile}" "${CMAKE_CURRENT_BINARY_DIR}/llvm-weighted-block-test.linked"
            --top 1 --llvm-pass-pipeline "default<O1>")
        set_tests_properties(llvm_block_weighted_bundle PROPERTIES FIXTURES_REQUIRED llvm_weighted_profile
            PASS_REGULAR_EXPRESSION "Compiled 1 LLVM block objects")
        set(_linked_test_profile "${CMAKE_CURRENT_BINARY_DIR}/llvm-block-test.profile")
        set(_linked_test_bundle "${CMAKE_CURRENT_BINARY_DIR}/llvm-block-test.linked")
        add_test(NAME llvm_block_profile COMMAND llvm_block_test)
        set_tests_properties(llvm_block_profile PROPERTIES FIXTURES_SETUP llvm_block_profile
            ENVIRONMENT "FIBERCPU_LLVM_JIT=profile;FIBERCPU_LLVM_JIT_PROFILE=${_linked_test_profile};FIBERCPU_LLVM_JIT_HOT_THRESHOLD=1")
        add_test(NAME llvm_block_linked_bundle COMMAND fibercpu_llvm_cache --linked "${_linked_test_profile}" "${_linked_test_bundle}")
        set_tests_properties(llvm_block_linked_bundle PROPERTIES FIXTURES_REQUIRED llvm_block_profile)
        set_property(TEST llvm_block_generic llvm_block_specialized llvm_block_hot llvm_block_reuse llvm_block_reuse_generic llvm_block_budget
            llvm_block_packed llvm_block_async
            llvm_block_profile APPEND PROPERTY ENVIRONMENT "FIBERCPU_LINKED_BLOCK_CACHE=0")
    endif()
endif()
