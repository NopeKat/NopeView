/* jconfig.h for the Android build of the vendored decompressor.
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
