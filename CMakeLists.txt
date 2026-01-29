cmake_minimum_required(VERSION 3.20)
project(x86emu VERSION 0.1 LANGUAGES C CXX)

# Enforce C++23
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# CPM Integration
include(cmake/CPM.cmake)

CPMAddPackage(
    NAME simde
    GITHUB_REPOSITORY simd-everywhere/simde
    VERSION 0.8.2
    OPTIONS "SIMDE_ENABLE_NATIVE_ALIASES ON"
)

# Compiler warnings and options
if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    add_compile_options(
        -Wall -Wextra -Wpedantic
        -Wno-unused-parameter
        # Optimization flags for simulator
        -fno-rtti -fno-exceptions
    )
else()
    message(WARNING "This project is designed for Clang (supporting preserve_none and musttail). You are using ${CMAKE_CXX_COMPILER_ID}.")
endif()

# Source files
file(GLOB_RECURSE SOURCES "src/*.cpp")
list(FILTER SOURCES EXCLUDE REGEX "src/main.cpp")


add_library(x86emu SHARED ${SOURCES})

target_include_directories(x86emu PRIVATE src ${simde_SOURCE_DIR})

