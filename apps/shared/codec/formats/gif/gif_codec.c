/*
 * GIF format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image.
 *
 * GIF can be animated, so the decoder honours nv_format_decode_request.frame_index.
 * A small LRU cache keeps decoded animations, so playback only converts and
 * copies the requested frame instead of decoding the whole GIF again.
 */

#if defined(NV_CODEC_MEMORY_BUDGET)
#include "nv_stbi_alloc.h"
#define STBI_MALLOC nv_stbi_malloc
#define STBI_REALLOC nv_stbi_realloc
#define STBI_FREE nv_stbi_free
#endif

#define NV_DOWNSCALE_ALLOC malloc

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_GIF
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#if defined(_WIN32)
#include <intrin.h>
#include <windows.h>
#else
#include <stdatomic.h>
#endif

/* The format ABI carries UTF-8 paths. Use the wide-char CRT entry point on
 * Windows so non-ASCII file names work without depending on the ANSI code
 * page. stb_image declares the same function when STBI_WINDOWS_UTF8 is set. */
#define NV_UTF8_CODE_PAGE 65001u
#define NV_GIF_CACHE_SLOTS 4

#if defined(_WIN32)
extern __declspec(dllimport) int __stdcall MultiByteToWideChar(
    unsigned int code_page, unsigned long flags, const char *multi_byte,
    int multi_byte_count, wchar_t *wide, int wide_count);
#endif

typedef struct nv_gif_cache {
    char *path;
    int64_t size;
    int64_t mtime;
    int width;
    int height;
    int frames;
    int *delays;
    unsigned char *pixels;
    size_t frame_bytes;
    uint64_t last_used;
} nv_gif_cache;

static nv_gif_cache g_cache[NV_GIF_CACHE_SLOTS];
static uint64_t g_cache_tick;

#if defined(_WIN32)
static volatile long g_cache_lock = 0;

static void cache_lock(void)
{
    while (_InterlockedExchange(&g_cache_lock, 1L) != 0L) {
    }
}

static void cache_unlock(void)
{
    _InterlockedExchange(&g_cache_lock, 0L);
}
#else
static atomic_flag g_cache_lock = ATOMIC_FLAG_INIT;

static void cache_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&g_cache_lock, memory_order_acquire)) {
    }
}

static void cache_unlock(void)
{
    atomic_flag_clear_explicit(&g_cache_lock, memory_order_release);
}
#endif

#if defined(_WIN32)
static wchar_t *utf8_to_wide(const char *path)
{
    int count = MultiByteToWideChar(NV_UTF8_CODE_PAGE, 0u, path, -1, NULL, 0);
    wchar_t *wide;

    if (count <= 0) {
        return NULL;
    }
    wide = (wchar_t *)malloc((size_t)count * sizeof(wchar_t));
    if (wide == NULL) {
        return NULL;
    }
    if (MultiByteToWideChar(NV_UTF8_CODE_PAGE, 0u, path, -1, wide, count) <= 0) {
        free(wide);
        return NULL;
    }
    return wide;
}
#endif

static char *duplicate_path(const char *path)
{
    size_t length = strlen(path) + 1u;
    char *copy = (char *)malloc(length);

    if (copy != NULL) {
        memcpy(copy, path, length);
    }
    return copy;
}

static void clear_cache(nv_gif_cache *cache)
{
    if (cache->pixels != NULL) {
        stbi_image_free(cache->pixels);
    }
    free(cache->delays);
    free(cache->path);
    memset(cache, 0, sizeof(*cache));
}

static uint32_t frame_delay_ms(const nv_gif_cache *cache, uint32_t index)
{
    int delay;

    if (cache->delays == NULL) {
        return 100u;
    }

    delay = cache->delays[index];
    if (delay <= 0) {
        return 100u;
    }
    return (uint32_t)delay;
}

static int get_file_stamp(const char *path, int64_t *out_size, int64_t *out_mtime)
{
#if defined(_WIN32)
    wchar_t *wide = utf8_to_wide(path);
    struct __stat64 info;
    int ok = 0;

    if (wide == NULL) {
        return 0;
    }
    if (_wstat64(wide, &info) == 0) {
        *out_size = (int64_t)info.st_size;
        *out_mtime = (int64_t)info.st_mtime;
        ok = 1;
    }
    free(wide);
    return ok;
#else
    struct stat info;

    if (path == NULL || stat(path, &info) != 0) {
        return 0;
    }
    *out_size = (int64_t)info.st_size;
    *out_mtime = (int64_t)info.st_mtime;
    return 1;
#endif
}

static nv_gif_cache *cache_find(const char *path, int64_t size, int64_t mtime)
{
    int index;

    for (index = 0; index < NV_GIF_CACHE_SLOTS; index++) {
        nv_gif_cache *slot = &g_cache[index];
        if (slot->path != NULL && strcmp(slot->path, path) == 0 &&
            slot->size == size && slot->mtime == mtime) {
            slot->last_used = ++g_cache_tick;
            return slot;
        }
    }
    return NULL;
}

static nv_gif_cache *cache_insert(const nv_gif_cache *fresh)
{
    int index;
    int oldest = 0;

    for (index = 0; index < NV_GIF_CACHE_SLOTS; index++) {
        if (g_cache[index].pixels == NULL) {
            clear_cache(&g_cache[index]);
            g_cache[index] = *fresh;
            g_cache[index].last_used = ++g_cache_tick;
            return &g_cache[index];
        }
    }
    for (index = 1; index < NV_GIF_CACHE_SLOTS; index++) {
        if (g_cache[index].last_used < g_cache[oldest].last_used) {
            oldest = index;
        }
    }
    clear_cache(&g_cache[oldest]);
    g_cache[oldest] = *fresh;
    g_cache[oldest].last_used = ++g_cache_tick;
    return &g_cache[oldest];
}

/* Reads a whole file into memory. The caller frees the buffer with free(). */
static int read_file(const char *path, unsigned char **out_data, size_t *out_size)
{
    FILE *file;
    int64_t size;
    unsigned char *buffer;
    size_t read_count;

    *out_data = NULL;
    *out_size = 0u;

#if defined(_WIN32)
    wchar_t *wide_path;
    errno_t open_result;

    wide_path = utf8_to_wide(path);
    if (wide_path == NULL) {
        return 0;
    }
    open_result = _wfopen_s(&file, wide_path, L"rb");
    free(wide_path);
    if (open_result != 0 || file == NULL) {
        return 0;
    }

    if (_fseeki64(file, 0, SEEK_END) != 0) {
        fclose(file);
        return 0;
    }
    size = _ftelli64(file);
    if (size <= 0 || size > (int64_t)NV_MAX_DECODE_BYTES) {
        fclose(file);
        return 0;
    }
    if (_fseeki64(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return 0;
    }
#else
    file = fopen(path, "rb");
    if (file == NULL) {
        return 0;
    }
    if (fseeko(file, 0, SEEK_END) != 0) {
        fclose(file);
        return 0;
    }
    size = (int64_t)ftello(file);
    if (size <= 0 || size > (int64_t)NV_MAX_DECODE_BYTES) {
        fclose(file);
        return 0;
    }
    if (fseeko(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return 0;
    }
#endif

    buffer = (unsigned char *)malloc((size_t)size);
    if (buffer == NULL) {
        fclose(file);
        return 0;
    }

    read_count = fread(buffer, 1u, (size_t)size, file);
    fclose(file);

    if (read_count != (size_t)size) {
        free(buffer);
        return 0;
    }

    *out_data = buffer;
    *out_size = (size_t)size;
    return 1;
}

const char *nv_format_abi_version(void)
{
    return NV_FORMAT_ABI_VERSION;
}

nv_format_status nv_format_query(nv_format_info *out_info)
{
    if (out_info == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(out_info->struct_size, sizeof(nv_format_info))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    out_info->name = "GIF";
    out_info->format_id = "gif";
    out_info->extensions = "gif";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ANIMATED;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    unsigned char *data = NULL;
    size_t data_size = 0u;
    int owns_data = 0;
    int *delays = NULL;
    int width = 0;
    int height = 0;
    int frames = 0;
    int channels_in_file = 0;
    unsigned char *all_frames = NULL;
    unsigned char *frame_pixels = NULL;
    size_t pixel_count;
    size_t frame_bytes;
    int components;
    nv_format_pixel_format output_format;
    int64_t file_size = 0;
    int64_t file_mtime = 0;
    int have_stamp = 0;
    nv_gif_cache *slot;
    nv_gif_cache fresh;
    int image_width;
    int image_height;
    int frame_stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);
    max_dimension = nv_request_max_dimension(request);

    if (request->path != NULL) {
        have_stamp = get_file_stamp(request->path, &file_size, &file_mtime);
    }

    if (have_stamp != 0) {
        cache_lock();
        slot = cache_find(request->path, file_size, file_mtime);
        if (slot != NULL) {
            if (request->frame_index >= (uint32_t)slot->frames) {
                cache_unlock();
                return NV_FORMAT_ERR_UNSUPPORTED;
            }
            pixel_count = (size_t)slot->width * (size_t)slot->height;
            frame_bytes = pixel_count * (size_t)components;
            frame_pixels = (unsigned char *)malloc(frame_bytes);
            if (frame_pixels == NULL) {
                cache_unlock();
                return NV_FORMAT_ERR_OUT_OF_MEMORY;
            }
            nv_rgba_to_format(
                frame_pixels,
                slot->pixels + (size_t)request->frame_index * slot->frame_bytes,
                pixel_count,
                output_format);
            image_width = slot->width;
            image_height = slot->height;
            frame_stride = image_width * components;
            cache_unlock();

            /* The cached animation stays at full size; only the returned frame
             * is scaled down for a preview request. */
            if (!nv_limit_decoded_size(
                    &frame_pixels, &image_width, &image_height, &frame_stride,
                    components, max_dimension, free, NV_DOWNSCALE_ALLOC,
                    &source_width, &source_height)) {
                free(frame_pixels);
                return NV_FORMAT_ERR_OUT_OF_MEMORY;
            }

            out_image->width = image_width;
            out_image->height = image_height;
            out_image->stride = frame_stride;
            out_image->pixel_format = (nv_format_pixel_format)output_format;
            out_image->pixels = frame_pixels;
            out_image->pixels_size = (size_t)frame_stride * (size_t)image_height;
            out_image->frame_count = (uint32_t)slot->frames;
            out_image->frame_delay_ms = frame_delay_ms(slot, request->frame_index);
            out_image->source_width = source_width;
            out_image->source_height = source_height;
            return NV_FORMAT_OK;
        }
        cache_unlock();
    }

    if (request->data != NULL && request->data_size > 0u) {
        if (request->data_size > NV_MAX_DECODE_BYTES) {
            return NV_FORMAT_ERR_INVALID_ARGUMENT;
        }
        data = (unsigned char *)request->data;
        data_size = request->data_size;
    }
    else if (request->path != NULL) {
        if (read_file(request->path, &data, &data_size) == 0) {
            return NV_FORMAT_ERR_IO;
        }
        owns_data = 1;
    }
    else {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    all_frames = stbi_load_gif_from_memory(
        data,
        (int)data_size,
        &delays,
        &width,
        &height,
        &frames,
        &channels_in_file,
        4);

    if (owns_data != 0) {
        free(data);
    }

    if (all_frames == NULL) {
        free(delays);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (request->frame_index >= (uint32_t)frames) {
        free(delays);
        stbi_image_free(all_frames);
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    pixel_count = (size_t)width * (size_t)height;
    memset(&fresh, 0, sizeof(fresh));
    fresh.pixels = all_frames;
    fresh.width = width;
    fresh.height = height;
    fresh.frames = frames;
    fresh.delays = delays;
    fresh.frame_bytes = pixel_count * 4u;
    if (request->path != NULL) {
        fresh.path = duplicate_path(request->path);
        if (fresh.path == NULL) {
            free(delays);
            stbi_image_free(all_frames);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        fresh.size = have_stamp != 0 ? file_size : 0;
        fresh.mtime = have_stamp != 0 ? file_mtime : 0;
    }

    frame_bytes = pixel_count * (size_t)components;
    frame_pixels = (unsigned char *)malloc(frame_bytes);

    cache_lock();
    slot = cache_insert(&fresh);
    if (frame_pixels != NULL) {
        nv_rgba_to_format(
            frame_pixels,
            slot->pixels + (size_t)request->frame_index * slot->frame_bytes,
            pixel_count,
            output_format);
    }
    cache_unlock();

    if (frame_pixels == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    /* The cached animation stays at full size; only the returned frame is
     * scaled down for a preview request. */
    frame_stride = width * components;
    if (!nv_limit_decoded_size(
            &frame_pixels, &width, &height, &frame_stride, components,
            max_dimension, free, NV_DOWNSCALE_ALLOC, &source_width, &source_height)) {
        free(frame_pixels);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    out_image->width = width;
    out_image->height = height;
    out_image->stride = frame_stride;
    out_image->pixel_format = (nv_format_pixel_format)output_format;
    out_image->pixels = frame_pixels;
    out_image->pixels_size = (size_t)frame_stride * (size_t)height;
    out_image->frame_count = (uint32_t)slot->frames;
    out_image->frame_delay_ms = frame_delay_ms(slot, request->frame_index);
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_encode(const nv_format_encode_request *request, nv_format_buffer *out_buffer)
{
    return nv_encode_unsupported(request, out_buffer);
}

void nv_format_image_free(nv_format_image *image)
{
    if (image == NULL || image->pixels == NULL) {
        return;
    }
    /* Frame pixels are allocated with malloc, not by stb. */
    free(image->pixels);
    image->pixels = NULL;
    image->pixels_size = 0;
    image->width = 0;
    image->height = 0;
    image->stride = 0;
}

void nv_format_buffer_free(nv_format_buffer *buffer)
{
    if (buffer == NULL) {
        return;
    }
    /* Nothing is allocated until encoding is implemented. */
    buffer->data = NULL;
    buffer->size = 0;
}
