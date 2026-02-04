# Fiberish C# Project Build Logic

if(CMAKE_GENERATOR MATCHES "Visual Studio")
    file(GLOB_RECURSE CS_SOURCES "*.cs")
    add_executable(Fiberish.App ${CS_SOURCES})
    set_target_properties(Fiberish.App PROPERTIES
        DOTNET_SDK "Microsoft.NET.Sdk"
        DOTNET_TARGET_FRAMEWORK "net8.0"
        VS_DOTNET_REFERENCE_Fiberish_App "${CMAKE_CURRENT_SOURCE_DIR}/Fiberish.App.csproj"
    )
    
    add_custom_command(TARGET Fiberish.App POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy 
        $<TARGET_FILE:fibercpu> 
        $<TARGET_FILE_DIR:Fiberish.App>/fibercpu.dll
    )
else()
    set(CONFIG_STR "$<IF:$<CONFIG:Release>,Release,Debug>")
    # RID used for native AOT publish. Change as needed (linux-x64, osx-x64, win-x64, etc.)
    # Default RID is osx-x64; if building on Apple Silicon, switch to osx-arm64 automatically.
    set(DOTNET_RID "osx-x64" CACHE STRING "Runtime Identifier for dotnet publish (override if needed)")

    if(APPLE)
        # Detect architecture of the host running CMake. Prefer native arm64 on Apple Silicon.
        execute_process(COMMAND uname -m OUTPUT_VARIABLE _uname_arch OUTPUT_STRIP_TRAILING_WHITESPACE)
        if(_uname_arch STREQUAL "arm64")
            set(DOTNET_RID "osx-arm64" CACHE STRING "Runtime Identifier for dotnet publish (override if needed)" FORCE)
            message(STATUS "Detected Apple Silicon (arm64) host; setting DOTNET_RID=osx-arm64 for native publish")
        endif()
    endif()

    # Use dotnet publish with PublishAot to produce a Native AOT binary. 'dotnet build' does not AOT.
    add_custom_target(Fiberish.App ALL
        COMMAND dotnet publish "${CMAKE_CURRENT_SOURCE_DIR}/Fiberish.App.csproj" -c ${CONFIG_STR} -r ${DOTNET_RID} -p:PublishAot=true --self-contained false -o "${CMAKE_RUNTIME_OUTPUT_DIRECTORY}"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Publishing Fiberish.App (Native AOT)"
    )

    # Ensure fibercpu is built before publishing and then copy the built shared library
    add_dependencies(Fiberish.App fibercpu)

    # Copy the actual target file name for the current platform into the publish output.
    # $<TARGET_FILE:fibercpu> is the full path to the produced library; $<TARGET_FILE_NAME:fibercpu>
    # yields the filename with the platform extension (.so/.dylib/.dll).
    add_custom_command(TARGET Fiberish.App POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:fibercpu>" "${CMAKE_RUNTIME_OUTPUT_DIRECTORY}/$<TARGET_FILE_NAME:fibercpu>"
        COMMENT "Copying fibercpu into publish output with platform-appropriate filename"
    )
endif()

# Dependency on fibercpu
add_dependencies(Fiberish.App fibercpu)
