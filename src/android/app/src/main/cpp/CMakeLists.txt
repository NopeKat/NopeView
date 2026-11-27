# Copyright (c) 2026 NopeKat. All rights reserved.
# Licensed under the GNU Affero General Public License v3.0; see LICENSE.

cmake_minimum_required(VERSION 3.22.1)
project(nopeview_codec C CXX)

set(SHARED_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../shared")
set(CODEC_ABI "${SHARED_ROOT}/codec/abi")
set(STB_ROOT "${SHARED_ROOT}/codec/third_party/stb")
set(MINIZ_ROOT "${SHARED_ROOT}/codec/third_party/miniz")
set(LIBWEBP_ROOT "${SHARED_ROOT}/codec/third_party/libwebp")
set(WEBP_CONFIG_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/webp_config")
set(BCDEC_ROOT "${SHARED_ROOT}/codec/third_party/bcdec")
set(NANOSVG_ROOT "${SHARED_ROOT}/codec/third_party/nanosvg")
set(TINYEXR_ROOT "${SHARED_ROOT}/codec/third_party/tinyexr")
set(OPENJPEG_ROOT "${SHARED_ROOT}/codec/third_party/openjpeg")
set(LIBJPEG_TURBO_ROOT "${SHARED_ROOT}/codec/third_party/libjpeg-turbo")

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_library(LOG_LIB log)
find_library(DL_LIB dl)
find_library(JNIGRAPHICS_LIB jnigraphics)
find_library(M_LIB m REQUIRED)

# The router and JNI facade share one library. Format codecs stay separate so
# the router can discover them exactly as it does on Windows. The router is
# split into dispatch, loader, io, cache and platform units, and all of them
# are compiled here.
add_library(nopeview SHARED
    nopeview_jni.c
    "${SHARED_ROOT}/codec/router/router.c"
    "${SHARED_ROOT}/codec/router/router_loader.c"
    "${SHARED_ROOT}/codec/router/router_io.c"
    "${SHARED_ROOT}/codec/router/router_cache.c"
    "${SHARED_ROOT}/codec/router/router_platform.c"
)
target_include_directories(nopeview PRIVATE "${CODEC_ABI}")
target_compile_definitions(nopeview PRIVATE NV_ROUTER_MAX_INPUT_BYTES=268435456)
target_compile_options(nopeview PRIVATE -Wall -Wextra)
target_link_libraries(nopeview PRIVATE "${LOG_LIB}" "${DL_LIB}" "${JNIGRAPHICS_LIB}")

# The stb-backed formats need no extra vendored sources. HDR and DNG tone
# mapping also uses libm, which is linked only by those two targets. JPEG is
# built separately because its decoder is libjpeg-turbo.
foreach(FORMAT png gif bmp ico tga hdr dng)
    add_library(${FORMAT}_codec SHARED
        "${SHARED_ROOT}/codec/formats/${FORMAT}/${FORMAT}_codec.c"
    )
    target_include_directories(${FORMAT}_codec PRIVATE "${CODEC_ABI}" "${STB_ROOT}")
    target_compile_definitions(${FORMAT}_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
    target_compile_options(${FORMAT}_codec PRIVATE -Wall -Wextra)
    target_link_libraries(${FORMAT}_codec PRIVATE "${LOG_LIB}")
    if(FORMAT STREQUAL "hdr" OR FORMAT STREQUAL "dng")
        target_link_libraries(${FORMAT}_codec PRIVATE "${M_LIB}")
    endif()
endforeach()

# JPEG decodes through libjpeg-turbo, which also gives the preview path a
# scaled IDCT. The vendored warning baseline is relaxed for those sources.
file(GLOB LIBJPEG_TURBO_SOURCES "${LIBJPEG_TURBO_ROOT}/src/*.c")
# Included by jdcolor, jdmerge and jdhuff rather than compiled on their own.
list(REMOVE_ITEM LIBJPEG_TURBO_SOURCES
    "${LIBJPEG_TURBO_ROOT}/src/jdcol565.c"
    "${LIBJPEG_TURBO_ROOT}/src/jdcolext.c"
    "${LIBJPEG_TURBO_ROOT}/src/jdmrg565.c"
    "${LIBJPEG_TURBO_ROOT}/src/jdmrgext.c"
    "${LIBJPEG_TURBO_ROOT}/src/jstdhuff.c"
)
set_source_files_properties(${LIBJPEG_TURBO_SOURCES} PROPERTIES COMPILE_OPTIONS "-w")
add_library(jpg_codec SHARED
    "${SHARED_ROOT}/codec/formats/jpg/jpg_codec.c"
    ${LIBJPEG_TURBO_SOURCES}
)
target_include_directories(jpg_codec PRIVATE
    "${CODEC_ABI}"
    "${STB_ROOT}"
    "${LIBJPEG_TURBO_ROOT}/src"
    "${LIBJPEG_TURBO_ROOT}/config/android"
)
target_compile_definitions(jpg_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
target_compile_options(jpg_codec PRIVATE -Wall -Wextra)
target_link_libraries(jpg_codec PRIVATE "${LOG_LIB}")

# TIFF and PSD vendor miniz per shared library, matching the Windows build.
# miniz keeps its upstream warning baseline instead of the codec warning set.
set_source_files_properties("${MINIZ_ROOT}/miniz.c" PROPERTIES COMPILE_OPTIONS "-w")
foreach(FORMAT tiff psd)
    add_library(${FORMAT}_codec SHARED
        "${SHARED_ROOT}/codec/formats/${FORMAT}/${FORMAT}_codec.c"
        "${MINIZ_ROOT}/miniz.c"
    )
    target_include_directories(${FORMAT}_codec PRIVATE "${CODEC_ABI}" "${MINIZ_ROOT}")
    target_compile_definitions(${FORMAT}_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
    target_compile_options(${FORMAT}_codec PRIVATE -Wall -Wextra)
    target_link_libraries(${FORMAT}_codec PRIVATE "${LOG_LIB}")
    if(FORMAT STREQUAL "psd")
        target_link_libraries(${FORMAT}_codec PRIVATE "${M_LIB}")
    endif()
endforeach()

# WebP vendors the generic libwebp decoder and demux sources. The x86 SIMD
# sources are excluded because Android only builds arm64 and the vendored tree
# has no NEON implementations; the C fallback is selected by webp_config.
file(GLOB WEBP_SOURCES
    "${LIBWEBP_ROOT}/src/dec/*.c"
    "${LIBWEBP_ROOT}/src/demux/*.c"
    "${LIBWEBP_ROOT}/src/dsp/*.c"
    "${LIBWEBP_ROOT}/src/utils/*.c"
)
list(FILTER WEBP_SOURCES EXCLUDE REGEX ".*_(sse2|sse41)\\.c$")
set_source_files_properties(${WEBP_SOURCES} PROPERTIES COMPILE_OPTIONS "-w")
add_library(webp_codec SHARED
    "${SHARED_ROOT}/codec/formats/webp/webp_codec.c"
    ${WEBP_SOURCES}
)
target_include_directories(webp_codec PRIVATE
    "${CODEC_ABI}"
    "${LIBWEBP_ROOT}/src"
    "${LIBWEBP_ROOT}"
    "${WEBP_CONFIG_ROOT}"
)
target_compile_definitions(webp_codec PRIVATE
    NV_CODEC_MEMORY_BUDGET=268435456
    HAVE_CONFIG_H
    NDEBUG
)
target_compile_options(webp_codec PRIVATE -Wall -Wextra)
target_link_libraries(webp_codec PRIVATE "${LOG_LIB}")

# DDS uses the header-only bcdec implementation included by its codec source.
add_library(dds_codec SHARED
    "${SHARED_ROOT}/codec/formats/dds/dds_codec.c"
)
target_include_directories(dds_codec PRIVATE "${CODEC_ABI}" "${BCDEC_ROOT}")
target_compile_definitions(dds_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
target_compile_options(dds_codec PRIVATE -Wall -Wextra)
target_link_libraries(dds_codec PRIVATE "${LOG_LIB}" "${M_LIB}")

# SVG and EXR are single C++ translation units that embed their vendor
# implementations. miniz is shared with the TIFF and PSD targets above.
add_library(svg_codec SHARED
    "${SHARED_ROOT}/codec/formats/svg/svg_codec.cpp"
    "${MINIZ_ROOT}/miniz.c"
)
target_include_directories(svg_codec PRIVATE "${CODEC_ABI}" "${NANOSVG_ROOT}" "${MINIZ_ROOT}")
target_compile_definitions(svg_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
# nanosvg compares a size_t with -1L in one upstream line.
target_compile_options(svg_codec PRIVATE -Wall -Wextra -Wno-sign-compare)
target_link_libraries(svg_codec PRIVATE "${LOG_LIB}" "${M_LIB}")

add_library(exr_codec SHARED
    "${SHARED_ROOT}/codec/formats/exr/exr_codec.cpp"
    "${MINIZ_ROOT}/miniz.c"
)
target_include_directories(exr_codec PRIVATE "${CODEC_ABI}" "${TINYEXR_ROOT}" "${MINIZ_ROOT}")
target_compile_definitions(exr_codec PRIVATE NV_CODEC_MEMORY_BUDGET=268435456)
target_compile_options(exr_codec PRIVATE -Wall -Wextra)
target_link_libraries(exr_codec PRIVATE "${LOG_LIB}" "${M_LIB}")

# OpenJPEG is vendored as a set of C files and keeps its upstream warning
# baseline. OPJ_STATIC matches the Windows build.
file(GLOB OPENJPEG_SOURCES "${OPENJPEG_ROOT}/*.c")
set_source_files_properties(${OPENJPEG_SOURCES} PROPERTIES COMPILE_OPTIONS "-w")
add_library(jp2_codec SHARED
    "${SHARED_ROOT}/codec/formats/jp2/jp2_codec.c"
    ${OPENJPEG_SOURCES}
)
target_include_directories(jp2_codec PRIVATE "${CODEC_ABI}" "${OPENJPEG_ROOT}")
# The vendored opj_config_private.h is MSVC-only; Android uses posix_memalign.
target_compile_definitions(jp2_codec PRIVATE
    NV_CODEC_MEMORY_BUDGET=268435456
    OPJ_STATIC
    OPJ_HAVE_POSIX_MEMALIGN=1
)
target_compile_options(jp2_codec PRIVATE -Wall -Wextra)
target_link_libraries(jp2_codec PRIVATE "${LOG_LIB}" "${M_LIB}")
