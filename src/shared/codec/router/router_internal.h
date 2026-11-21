/*
 * Codec router internals.
 *
 * The router is one library built from several units, and this header is the
 * only thing they share. router.c owns the ABI surface and the dispatch,
 * router_loader.c finds and loads the format libraries, router_io.c turns paths
 * and descriptors into bytes, router_cache.c keeps the last file read, and
 * router_platform.c holds the few platform calls that more than one unit needs.
 *
 * Nothing here is exported from the library; the public surface is
 * nv_router_abi.h.
 */

#ifndef NV_ROUTER_INTERNAL_H
#define NV_ROUTER_INTERNAL_H

#include "nv_router_abi.h"
#include "nv_format_abi.h"
#include "router_platform.h"

/* Function pointer types resolved from each format library. */
typedef const char *(*fn_abi_version)(void);
typedef nv_format_status (*fn_query)(nv_format_info *);
typedef nv_format_status (*fn_decode)(const nv_format_decode_request *, nv_format_image *);
typedef nv_format_status (*fn_encode)(const nv_format_encode_request *, nv_format_buffer *);
typedef void (*fn_image_free)(nv_format_image *);
typedef void (*fn_buffer_free)(nv_format_buffer *);

typedef struct nv_format_entry {
    nv_module module;
    fn_decode decode;
    fn_encode encode;
    fn_image_free image_free;
    fn_buffer_free buffer_free;
    char format_id[32];
    char extensions[128];
    uint32_t capabilities;
} nv_format_entry;

/* The last file read, kept by one context and released with it. */
typedef struct nv_file_cache {
    char *path;
    uint64_t size;
    int64_t mtime;
    uint8_t *data;
    size_t data_size;
} nv_file_cache;

struct nv_router_context {
    nv_format_entry *entries;
    uint32_t entry_count;
    uint32_t entry_capacity;
    uint32_t abi_mismatch_count;
    nv_file_cache cache;
    nv_rwlock cache_lock;
};

/*
 * Encoded bytes for one decode. data points either at the shared cache or at a
 * private allocation; cached tells nv_io_release how to let go of it.
 */
typedef struct nv_file_bytes {
    const uint8_t *data;
    size_t size;
    int cached;
    int owned;
} nv_file_bytes;

/* Copies into a fixed buffer and always terminates it. Declared here because
 * both the loader and the dispatcher name format ids and extensions. */
void nv_copy_str(char *destination, size_t destination_size, const char *source);

/* router_loader.c: resolves codec_dir or the directory next to the library,
 * then loads every format codec found there. */
nv_router_status nv_loader_start(nv_router_context *context, const char *codec_dir);

/* router_io.c: reads and writes whole files, and releases what it returned. */
int nv_io_read_file(nv_router_context *context, const char *path, nv_file_bytes *out);
int nv_io_read_fd(int fd, nv_file_bytes *out);
int nv_io_write_file(const char *path, const uint8_t *data, size_t size);
void nv_io_release(nv_router_context *context, nv_file_bytes *bytes);

/* router_cache.c: a hit returns the cached bytes with the read lock held. */
int nv_cache_lookup(nv_router_context *context, const char *path,
                    uint64_t file_size, int64_t mtime, nv_file_bytes *out);
void nv_cache_store(nv_router_context *context, const char *path,
                    const uint8_t *data, size_t size, uint64_t file_size,
                    int64_t mtime);
void nv_cache_release(nv_router_context *context);
void nv_cache_clear(nv_router_context *context);

#endif /* NV_ROUTER_INTERNAL_H */
