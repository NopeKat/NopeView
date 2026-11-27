/* jconfig.h for the Windows build of the vendored decompressor.
 * Written by hand because this repository compiles the sources directly
 * instead of running libjpeg-turbo's CMake configure step.
 */

/* Version ID for the JPEG library. */
#define JPEG_LIB_VERSION  62

/* libjpeg-turbo version. */
#define LIBJPEG_TURBO_VERSION  3.1.0

/* libjpeg-turbo version in integer form. */
#define LIBJPEG_TURBO_VERSION_NUMBER  30100

/* Support arithmetic encoding when using 8-bit samples. */
#define C_ARITH_CODING_SUPPORTED  1

/* Support arithmetic decoding when using 8-bit samples. */
#define D_ARITH_CODING_SUPPORTED  1

/* Support in-memory source/destination managers. */
#define MEM_SRCDST_SUPPORTED  1

/* Accelerated SIMD routines are not compiled in this build. */

/* Some downstream software expects the macro to be defined. */
#ifndef BITS_IN_JSAMPLE
#define BITS_IN_JSAMPLE  8
#endif

#ifdef _WIN32

#undef RIGHT_SHIFT_IS_UNSIGNED

/* Define "boolean" as unsigned char, not int, per Windows custom. */
#ifndef __RPCNDR_H__
typedef unsigned char boolean;
#endif
#define HAVE_BOOLEAN

/* Define "INT32" as int, not long, per Windows custom. */
#if !(defined(_BASETSD_H_) || defined(_BASETSD_H))
typedef short INT16;
typedef signed int INT32;
#endif
#define XMD_H

#endif
