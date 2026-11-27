/* jconfigint.h for the Android build of the vendored decompressor.
 * Written by hand because this repository compiles the sources directly
 * instead of running libjpeg-turbo's CMake configure step.
 */

#define BUILD  "20261127"

#define HIDDEN  __attribute__((visibility("hidden")))

#undef inline
#define INLINE  inline
#define THREAD_LOCAL  __thread

#define PACKAGE_NAME  "libjpeg-turbo"
#define VERSION  "3.1.0"

#define SIZEOF_SIZE_T  8

#define HAVE_BUILTIN_CTZL  1

#if defined(__has_attribute)
#if __has_attribute(fallthrough)
#define FALLTHROUGH  __attribute__((fallthrough));
#else
#define FALLTHROUGH
#endif
#else
#define FALLTHROUGH
#endif

#ifndef BITS_IN_JSAMPLE
#define BITS_IN_JSAMPLE  8
#endif

#undef C_ARITH_CODING_SUPPORTED
#undef D_ARITH_CODING_SUPPORTED
#undef WITH_SIMD

#if BITS_IN_JSAMPLE == 8
#define C_ARITH_CODING_SUPPORTED 1
#define D_ARITH_CODING_SUPPORTED 1
#endif
