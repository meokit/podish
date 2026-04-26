file(GLOB SOURCES "*.c")
list(FILTER SOURCES EXCLUDE REGEX ".*M(\\.c|To.*\\.c)")

# Build as OBJECT + STATIC:
# - OBJECT lets parent targets embed softfloat objects directly into another static archive.
# - STATIC keeps standalone usage for non-embedded configurations.
add_library(softfloat_obj OBJECT ${SOURCES})
add_library(softfloat STATIC $<TARGET_OBJECTS:softfloat_obj>)
set_target_properties(softfloat_obj PROPERTIES POSITION_INDEPENDENT_CODE ON)
set_target_properties(softfloat PROPERTIES POSITION_INDEPENDENT_CODE ON)

target_include_directories(softfloat_obj PUBLIC .)
target_compile_definitions(softfloat_obj PRIVATE SOFTFLOAT_FAST_INT64)

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(softfloat_obj PRIVATE -Wno-all -Wno-pedantic)
endif()
