/*
 * Single entry cache of the last file read.
 *
 * A GIF animation reads the same unchanged file once per frame, so the bytes
 * handed to the last decode are kept and reused. A hit returns the bytes with
 * the read lock still held, which keeps them alive without a copy; every hit
 * must be paired with nv_cache_release.
 */

#include "router_internal.h"

typedef struct nv_file_cache {
    char *path;
    uint64_t size;
    int64_t mtime;
    uint8_t *data;
    size_t data_size;
} nv_file_cache;

/*
 * Files above this size are decoded without being cached. Holding a very large
 * image for the whole process lifetime costs more memory than the repeat reads
 * it saves, and still images are normally read once.
 */
#define NV_CACHE_MAX_BYTES ((size_t)32u * 1024u * 1024u)

static nv_file_cache g_cache;
static nv_rwlock g_cache_lock = NV_RWLOCK_INITIALIZER;

int nv_cache_lookup(const char *path, uint64_t file_size, int64_t mtime,
                    nv_file_bytes *out)
{
    int hit = 0;

    nv_rwlock_read(&g_cache_lock);
    if (g_cache.data != NULL && g_cache.path != NULL &&
        strcmp(g_cache.path, path) == 0 &&
        g_cache.size == file_size &&
        g_cache.mtime == mtime) {
        out->data = g_cache.data;
        out->size = g_cache.data_size;
        out->cached = 1;
        hit = 1;
    }

    /* A miss lets go at once; a hit keeps the lock until nv_cache_release. */
    if (hit == 0) {
        nv_rwlock_read_unlock(&g_cache_lock);
    }
    return hit;
}

void nv_cache_release(void)
{
    nv_rwlock_read_unlock(&g_cache_lock);
}

void nv_cache_store(const char *path, const uint8_t *data, size_t size,
                    uint64_t file_size, int64_t mtime)
{
    char *cache_path;
    uint8_t *cache_copy;

    if (size > NV_CACHE_MAX_BYTES) {
        return;
    }

    cache_path = (char *)malloc(strlen(path) + 1u);
    cache_copy = (uint8_t *)malloc(size);
    if (cache_path != NULL && cache_copy != NULL) {
        memcpy(cache_path, path, strlen(path) + 1u);
        memcpy(cache_copy, data, size);
        nv_rwlock_write(&g_cache_lock);
        free(g_cache.path);
        free(g_cache.data);
        g_cache.path = cache_path;
        g_cache.size = file_size;
        g_cache.mtime = mtime;
        g_cache.data = cache_copy;
        g_cache.data_size = size;
        nv_rwlock_write_unlock(&g_cache_lock);
    }
    else {
        free(cache_path);
        free(cache_copy);
    }
}
