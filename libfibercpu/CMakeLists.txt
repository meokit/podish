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

# Compiler warnings and options
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        -Wall -Wextra -Wpedantic
        -Wno-unused-parameter
        -fno-rtti -fno-exceptions
    )
endif()

# Source files
file(GLOB_RECURSE SOURCES "*.cpp" "*.c" "*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")
list(FILTER SOURCES EXCLUDE REGEX "3rdparty/softfloat/.*")

# SoftFloat library
if(USE_SOFTFLOAT)
    add_definitions(-DX86EMU_USE_SOFTFLOAT)
    add_subdirectory(3rdparty/softfloat)
endif()

# Generate library as 'fibercpu'
add_library(fibercpu SHARED ${SOURCES})

target_include_directories(fibercpu PRIVATE . ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)

# Link libraries
# If SoftFloat is used, link it
if(USE_SOFTFLOAT)
    target_link_libraries(fibercpu PRIVATE softfloat)
endif()