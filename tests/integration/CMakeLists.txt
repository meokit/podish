cmake_minimum_required(VERSION 3.20)
project(FiberishIntegrationAssets LANGUAGES NONE)

set(FIBERISH_PROJECT_ROOT "" CACHE PATH "Path to Fiberish repository root")
if (NOT FIBERISH_PROJECT_ROOT)
    get_filename_component(FIBERISH_PROJECT_ROOT "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)
endif()

find_program(ZIG_EXECUTABLE zig)
if (NOT ZIG_EXECUTABLE)
    message(FATAL_ERROR "zig executable not found. Install zig to build integration test assets.")
endif()

set(INTEGRATION_ASSETS_DIR "${CMAKE_CURRENT_BINARY_DIR}/assets")
set(INTEGRATION_ZIG_CACHE_DIR "${CMAKE_CURRENT_BINARY_DIR}/zig-cache")
file(MAKE_DIRECTORY "${INTEGRATION_ASSETS_DIR}")
file(MAKE_DIRECTORY "${INTEGRATION_ZIG_CACHE_DIR}")

set(INTEGRATION_TEST_OUTPUTS)

function(add_integration_binary output_name source_relpath)
    set(options)
    set(oneValueArgs OPT_LEVEL)
    cmake_parse_arguments(ARG "${options}" "${oneValueArgs}" "" ${ARGN})

    if (NOT ARG_OPT_LEVEL)
        set(ARG_OPT_LEVEL "-O2")
    endif()

    set(source_path "${FIBERISH_PROJECT_ROOT}/${source_relpath}")
    set(output_path "${INTEGRATION_ASSETS_DIR}/${output_name}")

    add_custom_command(
        OUTPUT "${output_path}"
        COMMAND ${CMAKE_COMMAND} -E env
            "ZIG_GLOBAL_CACHE_DIR=${INTEGRATION_ZIG_CACHE_DIR}"
            "ZIG_LOCAL_CACHE_DIR=${INTEGRATION_ZIG_CACHE_DIR}"
            "${ZIG_EXECUTABLE}" cc -target x86-linux-musl -static ${ARG_OPT_LEVEL} -o "${output_path}" "${source_path}"
        DEPENDS "${source_path}"
        COMMENT "Building integration binary ${output_name}"
        VERBATIM
    )

    list(APPEND INTEGRATION_TEST_OUTPUTS "${output_path}")
    set(INTEGRATION_TEST_OUTPUTS "${INTEGRATION_TEST_OUTPUTS}" PARENT_SCOPE)
endfunction()

add_integration_binary("hello_static" "tests/linux/hello.c")
add_integration_binary("syscall_test" "tests/linux/syscall_test.c")
add_integration_binary("test_futex" "tests/linux/test_futex.c")
add_integration_binary("test_mutex" "tests/linux/test_mutex.c")
add_integration_binary("test_pthread" "tests/linux/test_pthread.c")
add_integration_binary("test_full_features" "tests/linux/test_full_features.c")
add_integration_binary("test_fork_wait" "tests/test_fork_wait.c")
add_integration_binary("test_smc_linux" "tests/linux/test_smc_linux.c" OPT_LEVEL "-O0")
add_integration_binary("test_rdtsc" "tests/linux/test_rdtsc.c")
add_integration_binary("test_sysv_sem" "tests/src/test_sysv_sem.c")
add_integration_binary("test_posix_timers" "tests/src/test_posix_timers.c")
add_integration_binary("test_virtual_fds" "tests/src/test_virtual_fds.c")
add_integration_binary("test_nanosleep" "tests/src/test_nanosleep.c")
add_integration_binary("test_rt_signals" "tests/src/test_rt_signals.c")

add_custom_target(integration-tests-build DEPENDS ${INTEGRATION_TEST_OUTPUTS})

message(STATUS "Integration test assets target: integration-tests-build")
message(STATUS "Integration test assets output: ${INTEGRATION_ASSETS_DIR}")
message(STATUS "Integration test project root: ${FIBERISH_PROJECT_ROOT}")
