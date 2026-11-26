// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * Codec router platform layer.
 *
 * Declarations are in router_platform.h; this file holds the two
 * implementations, one for Windows and one for everything else.
 */

#include "router_platform.h"

#if defined(_WIN32)

void nv_rwlock_init(nv_rwlock *lock)
{
    InitializeSRWLock(lock);
}

void nv_rwlock_dispose(nv_rwlock *lock)
{
    (void)lock;
}

void nv_rwlock_read(nv_rwlock *lock)
{
    AcquireSRWLockShared(lock);
}

void nv_rwlock_write(nv_rwlock *lock)
{
    AcquireSRWLockExclusive(lock);
}

void nv_rwlock_read_unlock(nv_rwlock *lock)
{
    ReleaseSRWLockShared(lock);
}

void nv_rwlock_write_unlock(nv_rwlock *lock)
{
    ReleaseSRWLockExclusive(lock);
}

void nv_log(const char *message)
{
    OutputDebugStringA(message);
}

nv_module nv_module_open(nv_module_path path)
{
    return LoadLibraryW(path);
}

void *nv_module_symbol(nv_module module, const char *name)
{
    return (void *)GetProcAddress(module, name);
}

void nv_module_close(nv_module module)
{
    FreeLibrary(module);
}

#else

void nv_rwlock_init(nv_rwlock *lock)
{
    pthread_rwlock_init(lock, NULL);
}

void nv_rwlock_dispose(nv_rwlock *lock)
{
    pthread_rwlock_destroy(lock);
}

void nv_rwlock_read(nv_rwlock *lock)
{
    pthread_rwlock_rdlock(lock);
}

void nv_rwlock_write(nv_rwlock *lock)
{
    pthread_rwlock_wrlock(lock);
}

void nv_rwlock_read_unlock(nv_rwlock *lock)
{
    pthread_rwlock_unlock(lock);
}

void nv_rwlock_write_unlock(nv_rwlock *lock)
{
    pthread_rwlock_unlock(lock);
}

void nv_log(const char *message)
{
    (void)message;
}

nv_module nv_module_open(nv_module_path path)
{
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
}

void *nv_module_symbol(nv_module module, const char *name)
{
    return dlsym(module, name);
}

void nv_module_close(nv_module module)
{
    dlclose(module);
}

#endif
