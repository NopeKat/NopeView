/*
 * Codec router platform layer.
 *
 * The three primitives that differ between Windows and the other targets live
 * here and nowhere else: a read-write lock, the debug log line, and the
 * dynamic library handle used to load a format codec.
 */

#ifndef NV_ROUTER_PLATFORM_H
#define NV_ROUTER_PLATFORM_H

#if !defined(_WIN32) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#if defined(_WIN32)
#include <windows.h>
#include <wchar.h>
#else
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#if defined(__ANDROID__)
#include <android/log.h>
#endif
#endif

#include <ctype.h>
#include <limits.h>
#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
typedef HMODULE nv_module;
typedef const wchar_t *nv_module_path;
typedef SRWLOCK nv_rwlock;
#define NV_RWLOCK_INITIALIZER SRWLOCK_INIT
#else
typedef void *nv_module;
typedef const char *nv_module_path;
typedef pthread_rwlock_t nv_rwlock;
#define NV_RWLOCK_INITIALIZER PTHREAD_RWLOCK_INITIALIZER
#endif

void nv_rwlock_read(nv_rwlock *lock);
void nv_rwlock_write(nv_rwlock *lock);
void nv_rwlock_read_unlock(nv_rwlock *lock);
void nv_rwlock_write_unlock(nv_rwlock *lock);

/* Writes one line to whatever log the target provides; a no-op where none does. */
void nv_log(const char *message);

#if defined(__ANDROID__)
#define nv_log_format(...) \
    __android_log_print(ANDROID_LOG_INFO, "NopeViewNative", __VA_ARGS__)
#else
#define nv_log_format(...) ((void)0)
#endif

nv_module nv_module_open(nv_module_path path);
void *nv_module_symbol(nv_module module, const char *name);
void nv_module_close(nv_module module);

#endif /* NV_ROUTER_PLATFORM_H */
