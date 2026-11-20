cmake_minimum_required(VERSION 3.22.1)
project(nopeview_codec C)

set(SHARED_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../shared")
set(CODEC_ABI "${SHARED_ROOT}/codec/abi")
set(STB_ROOT "${SHARED_ROOT}/codec/third_party/stb")

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

find_library(LOG_LIB log)
find_library(DL_LIB dl)
find_library(JNIGRAPHICS_LIB jnigraphics)

# The router and JNI facade share one library. Format codecs stay separate so
# the router can discover them exactly as it does on Windows.
add_library(nopeview SHARED
    nopeview_jni.c
    "${SHARED_ROOT}/codec/router/router.c"
)
target_include_directories(nopeview PRIVATE "${CODEC_ABI}")
target_compile_definitions(nopeview PRIVATE NV_ROUTER_MAX_INPUT_BYTES=268435456)
target_compile_options(nopeview PRIVATE -Wall -Wextra)
target_link_libraries(nopeview PRIVATE "${LOG_LIB}" "${DL_LIB}" "${JNIGRAPHICS_LIB}")

foreach(FORMAT png jpg gif bmp)
    add_library(${FORMAT}_codec SHARED
        "${SHARED_ROOT}/codec/formats/${FORMAT}/${FORMAT}_codec.c"
    )
    target_include_directories(${FORMAT}_codec PRIVATE "${CODEC_ABI}" "${STB_ROOT}")
    target_compile_definitions(${FORMAT}_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
    target_compile_options(${FORMAT}_codec PRIVATE -Wall -Wextra)
    target_link_libraries(${FORMAT}_codec PRIVATE "${LOG_LIB}")
endforeach()
