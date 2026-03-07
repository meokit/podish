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
    set(oneValueArgs OPT_LEVEL EXTRA_FLAGS)
    cmake_parse_arguments(ARG "${options}" "${oneValueArgs}" "" ${ARGN})

    if (NOT ARG_OPT_LEVEL)
        set(ARG_OPT_LEVEL "-O2")
    endif()

    set(source_path "${FIBERISH_PROJECT_ROOT}/${source_relpath}")
    set(output_path "${INTEGRATION_ASSETS_DIR}/${output_name}")

    separate_arguments(EXTRA_FLAGS_LIST NATIVE_COMMAND "${ARG_EXTRA_FLAGS}")

    add_custom_command(
        OUTPUT "${output_path}"
        COMMAND ${CMAKE_COMMAND} -E env
            "ZIG_GLOBAL_CACHE_DIR=${INTEGRATION_ZIG_CACHE_DIR}"
            "ZIG_LOCAL_CACHE_DIR=${INTEGRATION_ZIG_CACHE_DIR}"
            "${ZIG_EXECUTABLE}" cc -target x86-linux-musl -static ${ARG_OPT_LEVEL} ${EXTRA_FLAGS_LIST} -o "${output_path}" "${source_path}"
        DEPENDS "${source_path}"
        COMMENT "Building integration binary ${output_name}"
        VERBATIM
    )

    list(APPEND INTEGRATION_TEST_OUTPUTS "${output_path}")
    set(INTEGRATION_TEST_OUTPUTS "${INTEGRATION_TEST_OUTPUTS}" PARENT_SCOPE)
endfunction()

add_integration_binary("hello_static" "tests/src/hello.c")
add_integration_binary("syscall_test" "tests/src/syscall_test.c")
add_integration_binary("test_futex" "tests/src/test_futex.c")
add_integration_binary("test_mutex" "tests/src/test_mutex.c")
add_integration_binary("test_pthread" "tests/src/test_pthread.c")
add_integration_binary("test_full_features" "tests/src/test_full_features.c")
add_integration_binary("test_fork_wait" "tests/src/test_fork_wait.c")
add_integration_binary("test_smc_linux" "tests/src/test_smc_linux.c" OPT_LEVEL "-O0")
add_integration_binary("test_rdtsc" "tests/src/test_rdtsc.c")
add_integration_binary("test_sigill" "tests/src/test_sigill.c")
add_integration_binary("test_div0" "tests/src/test_div0.c")
add_integration_binary("test_segv" "tests/src/test_segv.c")
add_integration_binary("test_fetch_fault" "tests/src/test_fetch_fault.c")
add_integration_binary("test_sysv_sem" "tests/src/test_sysv_sem.c")
add_integration_binary("test_posix_timers" "tests/src/test_posix_timers.c")
add_integration_binary("test_virtual_fds" "tests/src/test_virtual_fds.c")
add_integration_binary("test_nanosleep" "tests/src/test_nanosleep.c")
add_integration_binary("test_rt_signals" "tests/src/test_rt_signals.c")
add_integration_binary("test_network" "tests/src/test_network.c")
add_integration_binary("test_ping_socket" "tests/src/test_ping_socket.c")
add_integration_binary("test_low_hanging" "tests/src/test_low_hanging.c")
add_integration_binary("test_fchdir_truncate" "tests/src/test_fchdir_truncate.c")
add_integration_binary("test_job_control" "tests/src/test_job_control.c")
add_integration_binary("hello_test" "tests/src/hello_test.c")
add_integration_binary("test_jc_nolibc" "tests/src/test_jc_nolibc.c" EXTRA_FLAGS "-nostdlib -fno-stack-protector")
add_integration_binary("test_pty_nolibc" "tests/src/test_pty_nolibc.c" EXTRA_FLAGS "-nostdlib -fno-stack-protector")
add_integration_binary("test_low_hanging_nolibc" "tests/src/test_low_hanging_nolibc.c" EXTRA_FLAGS "-nostdlib -fno-stack-protector")
add_integration_binary("test_identity" "tests/src/test_identity.c")
add_integration_binary("test_vfork" "tests/src/test_vfork.c")
add_integration_binary("test_bind_mount" "tests/src/test_bind_mount.c")
add_integration_binary("test_open_tree" "tests/src/test_open_tree.c")
add_integration_binary("test_move_mount" "tests/src/test_move_mount.c")
add_integration_binary("test_bind_mount_ro" "tests/src/test_bind_mount_ro.c")
add_integration_binary("test_robust_futex" "tests/src/test_robust_futex.c")
add_integration_binary("test_futex_fresh_page" "tests/src/test_futex_fresh_page.c")
add_integration_binary("test_futex_after_signal" "tests/src/test_futex_after_signal.c")
add_integration_binary("test_shared_file_futex" "tests/src/test_shared_file_futex.c")
add_integration_binary("test_sysv_shm_futex" "tests/src/test_sysv_shm_futex.c")
add_integration_binary("test_shared_file_yield" "tests/src/test_shared_file_yield.c")
add_integration_binary("test_cow_private_mmap" "tests/src/test_cow_private_mmap.c")
add_integration_binary("test_cow_anonymous" "tests/src/test_cow_anonymous.c")
add_integration_binary("test_shared_futex_wake_unmapped" "tests/src/test_shared_futex_wake_unmapped.c")
add_integration_binary("test_proc_fd_magic" "tests/src/test_proc_fd_magic.c")
add_integration_binary("test_proc_fd_exec" "tests/src/test_proc_fd_exec.c")

add_custom_target(integration-tests-build DEPENDS ${INTEGRATION_TEST_OUTPUTS})

message(STATUS "Integration test assets target: integration-tests-build")
message(STATUS "Integration test assets output: ${INTEGRATION_ASSETS_DIR}")
message(STATUS "Integration test project root: ${FIBERISH_PROJECT_ROOT}")
