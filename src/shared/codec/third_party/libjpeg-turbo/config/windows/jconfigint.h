/* jconfigint.h for the Windows build of the vendored decompressor.
 * Written by hand because this repository compiles the sources directly
 * instead of running libjpeg-turbo's CMake configure step.
 */

#define BUILD  "20261127"

#define HIDDEN

#undef inline
#define INLINE  __inline
#define THREAD_LOCAL  __declspec(thread)

#define PACKAGE_NAME  "libjpeg-turbo"
#define VERSION  "3.1.0"

#define SIZEOF_SIZE_T  8

#define HAVE_INTRIN_H  1

#if defined(_MSC_VER) && defined(HAVE_INTRIN_H)
#if (SIZEOF_SIZE_T == 8)
#define HAVE_BITSCANFORWARD64
#elif (SIZEOF_SIZE_T == 4)
#define HAVE_BITSCANFORWARD
#endif
#endif

#define FALLTHROUGH

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
