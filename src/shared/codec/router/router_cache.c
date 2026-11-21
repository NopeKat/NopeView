/*
 * Single entry cache of the last file read, owned by one router context.
 *
 * A GIF animation reads the same unchanged file once per frame, so the bytes
 * handed to the last decode are kept and reused. A hit returns the bytes with
 * the read lock still held, which keeps them alive without a copy; every hit
 * must be paired with nv_cache_release. Two contexts have separate caches, so
 * browsing in one window does not evict the other, and nv_router_destroy hands
 * the memory back.
 */

#include "router_internal.h"

/*
 * Files above this size are decoded without being cached. Holding a very large
 * image for the whole process lifetime costs more memory than the repeat reads
 * it saves, and still images are normally read once.
 */
#define NV_CACHE_MAX_BYTES ((size_t)32u * 1024u * 1024u)

int nv_cache_lookup(nv_router_context *context, const char *path,
                    uint64_t file_size, int64_t mtime, nv_file_bytes *out)
{
    nv_file_cache *cache;
    int hit = 0;

    if (context == NULL) {
        return 0;
    }
    cache = &context->cache;

    nv_rwlock_read(&context->cache_lock);
    if (cache->data != NULL && cache->path != NULL &&
        strcmp(cache->path, path) == 0 &&
        cache->size == file_size &&
        cache->mtime == mtime) {
        out->data = cache->data;
        out->size = cache->data_size;
        out->cached = 1;
        hit = 1;
    }

    /* A miss lets go at once; a hit keeps the lock until nv_cache_release. */
    if (hit == 0) {
        nv_rwlock_read_unlock(&context->cache_lock);
    }
    return hit;
}

void nv_cache_release(nv_router_context *context)
{
    if (context == NULL) {
        return;
    }
    nv_rwlock_read_unlock(&context->cache_lock);
}

void nv_cache_store(nv_router_context *context, const char *path,
                    const uint8_t *data, size_t size, uint64_t file_size,
                    int64_t mtime)
{
    char *cache_path;
    uint8_t *cache_copy;

    if (context == NULL || size > NV_CACHE_MAX_BYTES) {
        return;
    }

    cache_path = (char *)malloc(strlen(path) + 1u);
    cache_copy = (uint8_t *)malloc(size);
    if (cache_path != NULL && cache_copy != NULL) {
        memcpy(cache_path, path, strlen(path) + 1u);
        memcpy(cache_copy, data, size);
        nv_rwlock_write(&context->cache_lock);
        free(context->cache.path);
        free(context->cache.data);
        context->cache.path = cache_path;
        context->cache.size = file_size;
        context->cache.mtime = mtime;
        context->cache.data = cache_copy;
        context->cache.data_size = size;
        nv_rwlock_write_unlock(&context->cache_lock);
    }
    else {
        free(cache_path);
        free(cache_copy);
    }
}

/* Drops the cached bytes. The caller must not hold the cache lock. */
void nv_cache_clear(nv_router_context *context)
{
    nv_file_cache released;

    if (context == NULL) {
        return;
    }

    nv_rwlock_write(&context->cache_lock);
    released = context->cache;
    context->cache.path = NULL;
    context->cache.size = 0u;
    context->cache.mtime = 0;
    context->cache.data = NULL;
    context->cache.data_size = 0u;
    nv_rwlock_write_unlock(&context->cache_lock);

    free(released.path);
    free(released.data);
}
