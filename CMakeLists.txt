cmake_minimum_required(VERSION 3.20)
project(Fiberish LANGUAGES C CXX)

# Unified output directory
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

add_subdirectory(libfibercpu)
# add_subdirectory(Fiberish.App) # Removed - Project refactored