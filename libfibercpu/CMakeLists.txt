cmake_minimum_required(VERSION 3.20)
project(libfibercpu LANGUAGES C CXX)

# Enforce C++23
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

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

# Compiler warnings and options
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        -Wall -Wextra -Wpedantic
        -Wno-unused-parameter
        -fno-rtti -fno-exceptions
    )
endif()

# Source files
file(GLOB_RECURSE SOURCES "*.cpp" "*.h")
list(FILTER SOURCES EXCLUDE REGEX "main.cpp")

# Generate library as 'fibercpu'
add_library(fibercpu SHARED ${SOURCES})

target_include_directories(fibercpu PRIVATE . ${simde_SOURCE_DIR})
target_link_libraries(fibercpu PRIVATE unordered_dense::unordered_dense)