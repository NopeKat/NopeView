/*
 * GIF format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image.
 *
 * GIF can be animated, so the decoder honours nv_format_decode_request.frame_index.
 * stb exposes every frame at once. The decoded animation is cached for one file
 * at a time, so playback only copies the requested frame instead of decoding
 * the whole GIF again for every frame.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_GIF
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

/* The format ABI carries UTF-8 paths. Use the wide-char CRT entry point on
 * Windows so non-ASCII file names work without depending on the ANSI code
 * page. stb_image declares the same function when STBI_WINDOWS_UTF8 is set. */
#define NV_UTF8_CODE_PAGE 65001u

#if defined(_WIN32)
extern __declspec(dllimport) int __stdcall MultiByteToWideChar(
    unsigned int code_page, unsigned long flags, const char *multi_byte,
    int multi_byte_count, wchar_t *wide, int wide_count);
#endif

typedef struct nv_gif_cache {
    char *path;
    __int64 size;
    __time64_t mtime;
    int width;
    int height;
    int frames;
    unsigned char *pixels;
    size_t frame_bytes;
} nv_gif_cache;

static nv_gif_cache g_cache;

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
    free(cache->path);
    memset(cache, 0, sizeof(*cache));
}

static int get_file_stamp(const char *path, __int64 *out_size, __time64_t *out_mtime)
{
    wchar_t *wide = utf8_to_wide(path);
    struct __stat64 info;
    int ok = 0;

    if (wide == NULL) {
        return 0;
    }
    if (_wstat64(wide, &info) == 0) {
        *out_size = info.st_size;
        *out_mtime = info.st_mtime;
        ok = 1;
    }
    free(wide);
    return ok;
}

/* Reads a whole file into memory. The caller frees the buffer with free(). */
static int read_file(const char *path, unsigned char **out_data, size_t *out_size)
{
    FILE *file;
    __int64 size;
    unsigned char *buffer;
    size_t read_count;
    wchar_t *wide_path;
    errno_t open_result;

    *out_data = NULL;
    *out_size = 0u;

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
    if (size <= 0 || size > (__int64)0x7fffffff) {
        fclose(file);
        return 0;
    }
    if (_fseeki64(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return 0;
    }

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
    size_t frame_bytes;
    __int64 file_size = 0;
    __time64_t file_mtime = 0;
    int have_stamp = 0;
    int cache_hit = 0;
    nv_gif_cache fresh;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    if (request->path != NULL) {
        have_stamp = get_file_stamp(request->path, &file_size, &file_mtime);
        if (have_stamp && g_cache.path != NULL &&
            strcmp(g_cache.path, request->path) == 0 &&
            g_cache.size == file_size && g_cache.mtime == file_mtime) {
            cache_hit = 1;
        }
    }

    if (cache_hit != 0) {
        if (request->frame_index >= (uint32_t)g_cache.frames) {
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
        frame_bytes = g_cache.frame_bytes;
        frame_pixels = (unsigned char *)malloc(frame_bytes);
        if (frame_pixels == NULL) {
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        memcpy(frame_pixels,
               g_cache.pixels + (size_t)request->frame_index * frame_bytes,
               frame_bytes);

        out_image->width = g_cache.width;
        out_image->height = g_cache.height;
        out_image->stride = g_cache.width * 4;
        out_image->pixel_format = NV_FORMAT_PIXEL_RGBA8;
        out_image->pixels = frame_pixels;
        out_image->pixels_size = frame_bytes;
        return NV_FORMAT_OK;
    }

    if (request->data != NULL && request->data_size > 0u) {
        if (request->data_size > (size_t)0x7fffffff) {
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

    /* Four channels so the output is always RGBA. */
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
    free(delays);

    if (all_frames == NULL) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (request->frame_index >= (uint32_t)frames) {
        stbi_image_free(all_frames);
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    frame_bytes = (size_t)width * (size_t)height * 4u;
    memset(&fresh, 0, sizeof(fresh));
    fresh.pixels = all_frames;
    fresh.width = width;
    fresh.height = height;
    fresh.frames = frames;
    fresh.frame_bytes = frame_bytes;
    if (request->path != NULL) {
        fresh.path = duplicate_path(request->path);
        if (fresh.path == NULL) {
            stbi_image_free(all_frames);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        fresh.size = have_stamp != 0 ? file_size : 0;
        fresh.mtime = have_stamp != 0 ? file_mtime : 0;
    }

    clear_cache(&g_cache);
    g_cache = fresh;

    frame_pixels = (unsigned char *)malloc(frame_bytes);
    if (frame_pixels == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memcpy(frame_pixels,
           g_cache.pixels + (size_t)request->frame_index * frame_bytes,
           frame_bytes);

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * 4;
    out_image->pixel_format = NV_FORMAT_PIXEL_RGBA8;
    out_image->pixels = frame_pixels;
    out_image->pixels_size = frame_bytes;
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
