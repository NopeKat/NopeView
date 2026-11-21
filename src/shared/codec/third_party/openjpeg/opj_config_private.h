#ifndef OPJ_CONFIG_PRIVATE_H_INCLUDED
#define OPJ_CONFIG_PRIVATE_H_INCLUDED

/* Hand written stand in for the file the CMake build generates. Only the
 * settings MSVC needs are kept. */

#define OPJ_PACKAGE_VERSION "2.5.2"

/* MSVC ships malloc.h and _aligned_malloc, and has no fseeko. */
#define OPJ_HAVE_MALLOC_H 1
#define OPJ_HAVE__ALIGNED_MALLOC 1

#endif
