cmake_minimum_required(VERSION 3.20)
project(Fiberish LANGUAGES C CXX)

# Unified output directory
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")

add_subdirectory(libfibercpu)

# Build Fiberish.Cli with NativeAOT
add_custom_target(FiberishCli
    COMMAND dotnet publish ${CMAKE_SOURCE_DIR}/Fiberish.Cli/Fiberish.Cli.csproj -c Release -p:PublishAot=true
    COMMENT "Building Fiberish.Cli with NativeAOT"
    VERBATIM
)