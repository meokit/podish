file(GLOB SOURCES "*.c")
list(FILTER SOURCES EXCLUDE REGEX ".*M(\\.c|To.*\\.c)")

add_library(softfloat STATIC ${SOURCES})

target_include_directories(softfloat PUBLIC .)
target_compile_definitions(softfloat PRIVATE SOFTFLOAT_FAST_INT64)

if(CMAKE_CXX_COMPILER_ID MATCHES "Clang")
    target_compile_options(softfloat PRIVATE -Wno-all -Wno-pedantic)
endif()
