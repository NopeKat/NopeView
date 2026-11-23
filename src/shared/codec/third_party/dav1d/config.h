/*
 * dav1d build configuration.
 * Written by hand because Meson normally generates this file. The values
 * describe a Windows x64 build with assembly and logging disabled, so only
 * the portable C paths are compiled.
 */

#ifndef DAV1D_CONFIG_H
#define DAV1D_CONFIG_H

/* MSVC has no __has_include_next; the bundled atomics shim tests that macro
 * even though the test is behind __clang__. */
#ifndef __has_include_next
#define __has_include_next(x) 0
#endif

/* Both bit depths are needed: 10 and 12 bit AVIF stills are common. */
#define CONFIG_8BPC 1
#define CONFIG_16BPC 1

/* No NASM step in this build, so every SIMD path stays compiled out. */
#define HAVE_ASM 0

#define CONFIG_LOG 0
#define CONFIG_MACOS_KPERF 0
#define TRIM_DSP_FUNCTIONS 0
#define ENDIANNESS_BIG 0

/* Target is x64, but the portable code is selected on purpose. */
#define ARCH_AARCH64 0
#define ARCH_ARM 0
#define ARCH_LOONGARCH 0
#define ARCH_LOONGARCH32 0
#define ARCH_LOONGARCH64 0
#define ARCH_PPC64LE 0
#define ARCH_RISCV 0
#define ARCH_RV32 0
#define ARCH_RV64 0
#define ARCH_X86 0
#define ARCH_X86_32 0
#define ARCH_X86_64 0

#define PIC 3

/* Windows CRT settings that Meson derives from the host. */
#define _WIN32_WINNT 0x0601
#define UNICODE 1
#define _UNICODE 1
#define _CRT_DECLARE_NONSTDC_NAMES 1
#define _FILE_OFFSET_BITS 64
#define fseeko _fseeki64
#define ftello _ftelli64

#endif
