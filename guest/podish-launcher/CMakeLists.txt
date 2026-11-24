cmake_minimum_required(VERSION 3.16)
project(podish-launcher LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_AUTOMOC ON)

find_package(Qt6 REQUIRED COMPONENTS Core Gui Widgets OpenGL)

add_executable(podish-launcher
    main.cpp
)

target_link_libraries(podish-launcher PRIVATE
    Qt6::Core
    Qt6::Gui
    Qt6::Widgets
    Qt6::OpenGL
)

add_library(podish-gl4es-sdl-shim SHARED
    gl4es_sdl_shim.c
)
set_target_properties(podish-gl4es-sdl-shim PROPERTIES
    PREFIX ""
    OUTPUT_NAME "podish-gl4es-sdl-shim"
)
target_link_libraries(podish-gl4es-sdl-shim PRIVATE dl)

install(TARGETS podish-launcher RUNTIME DESTINATION bin)
install(TARGETS podish-gl4es-sdl-shim LIBRARY DESTINATION lib)
