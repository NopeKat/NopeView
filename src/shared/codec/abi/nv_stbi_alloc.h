// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * nv_stbi_alloc.h - Optional memory budget for stb_image allocations.
 *
 * Android devices kill the process when a decode balloons past the available
 * heap, so the Android build caps the total bytes stb_image may hold. Windows
 * builds leave NV_CODEC_MEMORY_BUDGET undefined and keep stb's default
 * allocator, so their behavior does not change.
 *
 * The budget is per format library: only one format decodes at a time, and
 * keeping the counter local avoids a cross-library ownership protocol.
 */

#ifndef NV_STBI_ALLOC_H
#define NV_STBI_ALLOC_H

#include <stddef.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>

#ifndef NV_CODEC_MEMORY_BUDGET
#define NV_CODEC_MEMORY_BUDGET ((size_t)0)
#endif

#if NV_CODEC_MEMORY_BUDGET > 0

typedef union nv_stbi_header {
    struct {
        size_t size;
    } info;
    long double align_long_double;
    void *align_pointer;
    size_t align_size;
    max_align_t align_max;
} nv_stbi_header;

static atomic_size_t g_nv_stbi_bytes = 0;

static int nv_stbi_reserve(size_t size)
{
    size_t current = atomic_load_explicit(&g_nv_stbi_bytes, memory_order_relaxed);

    for (;;) {
        size_t next;

        if (current > NV_CODEC_MEMORY_BUDGET) {
            return 0;
        }
        if (size > NV_CODEC_MEMORY_BUDGET - current) {
            return 0;
        }
        next = current + size;
        if (atomic_compare_exchange_weak_explicit(
                &g_nv_stbi_bytes, &current, next,
                memory_order_relaxed, memory_order_relaxed)) {
            return 1;
        }
    }
}

static void nv_stbi_release(size_t size)
{
    atomic_fetch_sub_explicit(&g_nv_stbi_bytes, size, memory_order_relaxed);
}

static void *nv_stbi_malloc(size_t size)
{
    nv_stbi_header *header;

    if (size == 0u) {
        size = 1u;
    }
    if (size > SIZE_MAX - sizeof(nv_stbi_header)) {
        return NULL;
    }
    if (!nv_stbi_reserve(size)) {
        return NULL;
    }

    header = (nv_stbi_header *)malloc(sizeof(nv_stbi_header) + size);
    if (header == NULL) {
        nv_stbi_release(size);
        return NULL;
    }
    header->info.size = size;
    return (void *)(header + 1);
}

static void nv_stbi_free(void *pointer)
{
    nv_stbi_header *header;

    if (pointer == NULL) {
        return;
    }
    header = ((nv_stbi_header *)pointer) - 1;
    nv_stbi_release(header->info.size);
    free(header);
}

static inline void *nv_stbi_realloc(void *pointer, size_t size)
{
    nv_stbi_header *header;
    void *replacement;
    size_t old_size;

    if (pointer == NULL) {
        return nv_stbi_malloc(size);
    }
    if (size == 0u) {
        nv_stbi_free(pointer);
        return NULL;
    }

    header = ((nv_stbi_header *)pointer) - 1;
    old_size = header->info.size;
    replacement = nv_stbi_malloc(size);
    if (replacement == NULL) {
        return NULL;
    }
    memcpy(replacement, pointer, old_size < size ? old_size : size);
    nv_stbi_free(pointer);
    return replacement;
}

#endif /* NV_CODEC_MEMORY_BUDGET > 0 */

#endif /* NV_STBI_ALLOC_H */
