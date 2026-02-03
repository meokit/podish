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
    
    add_custom_target(Fiberish.App ALL
        COMMAND dotnet build "${CMAKE_CURRENT_SOURCE_DIR}/Fiberish.App.csproj" -c ${CONFIG_STR} -o "${CMAKE_RUNTIME_OUTPUT_DIRECTORY}"
        COMMAND ${CMAKE_COMMAND} -E copy 
                $<TARGET_FILE:fibercpu> 
                "${CMAKE_RUNTIME_OUTPUT_DIRECTORY}/libfibercpu.dylib"
        WORKING_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}"
        COMMENT "Building Fiberish.App and copying libfibercpu"
    )
endif()

# Dependency on fibercpu
add_dependencies(Fiberish.App fibercpu)