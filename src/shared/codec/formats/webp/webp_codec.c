// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * WebP format codec.
 * Implements the format ABI (nv_format_abi.h) on top of libwebp's demuxer and
 * anim decoder, which cover still images and animated files with one path.
 * Decoding is supported; encoding is not.
 *
 * An animation stays open per file, so playback steps one frame per request
 * instead of walking from the first frame every time. libwebp hands out one
 * canvas buffer that it overwrites in place, so the requested frame is copied
 * out before the decoder moves on.
 */

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

#include "webp/decode.h"
#include "webp/demux.h"

/* Three files are enough to cover the viewer plus a second context, and the
 * two caps keep a cached animation from holding an unreasonable amount of
 * memory: encoded bytes and one decoded canvas. */
#define NV_WEBP_SLOTS 3
#define NV_WEBP_MAX_ENCODED_BYTES ((size_t)24u * 1024u * 1024u)
#define NV_WEBP_MAX_CANVAS_BYTES ((size_t)64u * 1024u * 1024u)
#define NV_WEBP_NO_FRAME UINT32_C(0xFFFFFFFF)
#define NV_WEBP_UTF8_CODE_PAGE 65001u

typedef struct nv_webp_slot {
    char *path;
    int64_t size;
    int64_t mtime;
    unsigned char *data;
    size_t data_size;
    WebPAnimDecoder *decoder;
    uint8_t *canvas;
    int canvas_width;
    int canvas_height;
    uint32_t frame_count;
    uint32_t ready;
    int *timestamps;
    uint32_t timestamp_count;
    uint64_t last_used;
} nv_webp_slot;

static nv_webp_slot g_slots[NV_WEBP_SLOTS];
static uint64_t g_slot_tick;

#if defined(_WIN32)
static volatile long g_codec_lock = 0;

static void codec_lock(void)
{
    while (_InterlockedExchange(&g_codec_lock, 1L) != 0L) {
    }
}

static void codec_unlock(void)
{
    _InterlockedExchange(&g_codec_lock, 0L);
}
#else
static atomic_flag g_codec_lock = ATOMIC_FLAG_INIT;

static void codec_lock(void)
{
    while (atomic_flag_test_and_set_explicit(&g_codec_lock, memory_order_acquire)) {
    }
}

static void codec_unlock(void)
{
    atomic_flag_clear_explicit(&g_codec_lock, memory_order_release);
}
#endif

#if defined(_WIN32)
extern __declspec(dllimport) int __stdcall MultiByteToWideChar(
    unsigned int code_page, unsigned long flags, const char *multi_byte,
    int multi_byte_count, wchar_t *wide, int wide_count);

static wchar_t *utf8_to_wide(const char *path)
{
    int count = MultiByteToWideChar(NV_WEBP_UTF8_CODE_PAGE, 0u, path, -1, NULL, 0);
    wchar_t *wide;

    if (count <= 0) {
        return NULL;
    }
    wide = (wchar_t *)malloc((size_t)count * sizeof(wchar_t));
    if (wide == NULL) {
        return NULL;
    }
    if (MultiByteToWideChar(NV_WEBP_UTF8_CODE_PAGE, 0u, path, -1, wide, count) <= 0) {
        free(wide);
        return NULL;
    }
    return wide;
}
#endif

/* Mirrors the GIF codec: the slot is only reused when the file on disk is the
 * one that was decoded, so an edited animation is not served from stale pixels. */
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

static char *duplicate_path(const char *path)
{
    size_t length = strlen(path) + 1u;
    char *copy = (char *)malloc(length);

    if (copy != NULL) {
        memcpy(copy, path, length);
    }
    return copy;
}

static void slot_clear(nv_webp_slot *slot)
{
    if (slot->decoder != NULL) {
        WebPAnimDecoderDelete(slot->decoder);
    }
    free(slot->timestamps);
    free(slot->data);
    free(slot->path);
    memset(slot, 0, sizeof(*slot));
    slot->ready = NV_WEBP_NO_FRAME;
}

/* Returns the slot for this file, opening it on first use. Called with the
 * codec lock held. A NULL result means the request is served the slow way. */
static nv_webp_slot *slot_open(const char *path, const uint8_t *data, size_t data_size)
{
    nv_webp_slot *slot;
    WebPAnimDecoderOptions options;
    WebPAnimInfo info;
    WebPData webp_data;
    int64_t size = 0;
    int64_t mtime = 0;
    int index;
    int oldest;

    if (path == NULL || data == NULL || data_size == 0u ||
        data_size > NV_WEBP_MAX_ENCODED_BYTES) {
        return NULL;
    }
    if (!get_file_stamp(path, &size, &mtime)) {
        return NULL;
    }

    for (index = 0; index < NV_WEBP_SLOTS; index++) {
        slot = &g_slots[index];
        if (slot->path != NULL && slot->size == size && slot->mtime == mtime &&
            strcmp(slot->path, path) == 0) {
            slot->last_used = ++g_slot_tick;
            return slot;
        }
    }

    oldest = 0;
    for (index = 0; index < NV_WEBP_SLOTS; index++) {
        if (g_slots[index].path == NULL) {
            oldest = index;
            break;
        }
        if (g_slots[index].last_used < g_slots[oldest].last_used) {
            oldest = index;
        }
    }

    slot = &g_slots[oldest];
    slot_clear(slot);

    slot->data = (unsigned char *)malloc(data_size);
    if (slot->data == NULL) {
        return NULL;
    }
    memcpy(slot->data, data, data_size);
    slot->data_size = data_size;

    slot->path = duplicate_path(path);
    if (slot->path == NULL) {
        slot_clear(slot);
        return NULL;
    }

    if (!WebPAnimDecoderOptionsInit(&options)) {
        slot_clear(slot);
        return NULL;
    }
    /* The RGBA mode keeps transparency; decoding stays single threaded so the
     * library needs no threading layer. */
    options.color_mode = MODE_RGBA;
    options.use_threads = 0;

    webp_data.bytes = slot->data;
    webp_data.size = slot->data_size;
    slot->decoder = WebPAnimDecoderNew(&webp_data, &options);
    if (slot->decoder == NULL) {
        slot_clear(slot);
        return NULL;
    }

    if (!WebPAnimDecoderGetInfo(slot->decoder, &info) ||
        info.canvas_width == 0u || info.canvas_height == 0u || info.frame_count == 0u) {
        slot_clear(slot);
        return NULL;
    }
    if ((size_t)info.canvas_width * (size_t)info.canvas_height * 4u > NV_WEBP_MAX_CANVAS_BYTES) {
        slot_clear(slot);
        return NULL;
    }

    slot->timestamps = (int *)malloc(sizeof(int) * (size_t)info.frame_count);
    if (slot->timestamps == NULL) {
        slot_clear(slot);
        return NULL;
    }
    slot->frame_count = info.frame_count;
    slot->canvas_width = (int)info.canvas_width;
    slot->canvas_height = (int)info.canvas_height;
    slot->size = size;
    slot->mtime = mtime;
    slot->last_used = ++g_slot_tick;

    /* A fresh decoder has not produced any frame yet; the first seek starts
     * the walk from the first frame. */
    slot->ready = NV_WEBP_NO_FRAME;
    slot->timestamp_count = 0u;
    return slot;
}

/* Steps the decoder until its canvas holds the requested frame. A request that
 * moves backwards, which happens when playback loops, restarts the walk. */
static int slot_seek(nv_webp_slot *slot, uint32_t target)
{
    uint8_t *canvas;
    int timestamp;
    uint32_t next;

    if (slot->ready != NV_WEBP_NO_FRAME && slot->ready > target) {
        WebPAnimDecoderReset(slot->decoder);
        slot->ready = NV_WEBP_NO_FRAME;
        slot->timestamp_count = 0u;
    }

    while (slot->ready == NV_WEBP_NO_FRAME || slot->ready < target) {
        next = slot->ready == NV_WEBP_NO_FRAME ? 0u : slot->ready + 1u;
        if (!WebPAnimDecoderGetNext(slot->decoder, &canvas, &timestamp)) {
            return 0;
        }
        slot->timestamps[next] = timestamp;
        slot->timestamp_count = next + 1u;
        slot->canvas = canvas;
        slot->ready = next;
    }

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

    out_info->name = "WebP";
    out_info->format_id = "webp";
    out_info->extensions = "webp";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ANIMATED;
    return NV_FORMAT_OK;
}

/*
 * Walks the frames up to frame_index. The anim decoder reports the timestamp
 * of every frame it hands out, so the delay of the frame one step earlier is
 * known once the next frame is pulled.
 */
nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    WebPAnimDecoderOptions options;
    WebPAnimDecoder *decoder = NULL;
    WebPAnimInfo info;
    WebPData webp_data;
    nv_format_pixel_format output_format;
    int components;
    int width = 0;
    int height = 0;
    int stride;
    int source_width;
    int source_height;
    int32_t max_dimension;
    uint32_t index;
    uint32_t frame_delay_ms = 0u;
    uint32_t frame_count = 0u;
    int timestamp = 0;
    int previous_timestamp = 0;
    unsigned char *pixels = NULL;
    uint8_t *canvas = NULL;
    size_t pixel_count;
    size_t bytes;
    nv_webp_slot *slot;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }
    if (request->data == NULL || request->data_size == 0u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    output_format = nv_output_format(request->desired_pixel_format);
    components = nv_components_for(request->desired_pixel_format);

    webp_data.bytes = request->data;
    webp_data.size = request->data_size;

    /* The slot copies the canvas while the lock is held, so the pixels do not
     * depend on the decoder staying where it was once the lock is released. */
    codec_lock();
    slot = slot_open(request->path, request->data, request->data_size);
    if (slot != NULL && request->frame_index < slot->frame_count &&
        slot_seek(slot, request->frame_index)) {
        bytes = (size_t)slot->canvas_width * (size_t)slot->canvas_height * 4u;
        pixels = (unsigned char *)malloc(bytes);
        if (pixels != NULL) {
            memcpy(pixels, slot->canvas, bytes);
            width = slot->canvas_width;
            height = slot->canvas_height;
            frame_count = slot->frame_count;
            frame_delay_ms = (uint32_t)(
                slot->timestamps[request->frame_index] -
                (request->frame_index > 0u ? slot->timestamps[request->frame_index - 1u] : 0));
        }
    }
    codec_unlock();

    if (pixels == NULL) {
        /* No open slot, or one that could not be stepped: decode the file from
         * the first frame for this request alone. */
        if (!WebPAnimDecoderOptionsInit(&options)) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        options.color_mode = MODE_RGBA;
        options.use_threads = 0;

        decoder = WebPAnimDecoderNew(&webp_data, &options);
        if (decoder == NULL) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }

        if (!WebPAnimDecoderGetInfo(decoder, &info) ||
            info.canvas_width == 0u || info.canvas_height == 0u) {
            WebPAnimDecoderDelete(decoder);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        if (request->frame_index >= info.frame_count) {
            WebPAnimDecoderDelete(decoder);
            return NV_FORMAT_ERR_UNSUPPORTED;
        }

        /* The timestamp of a frame is the sum of the durations before it, so
         * the duration of every frame falls out of the walk. */
        for (index = 0u; index <= request->frame_index; index++) {
            if (!WebPAnimDecoderGetNext(decoder, &canvas, &timestamp)) {
                WebPAnimDecoderDelete(decoder);
                return NV_FORMAT_ERR_DECODE_FAILED;
            }
            frame_delay_ms = index == 0u
                ? (uint32_t)timestamp
                : (uint32_t)(timestamp - previous_timestamp);
            previous_timestamp = timestamp;
        }

        bytes = (size_t)info.canvas_width * (size_t)info.canvas_height * 4u;
        pixels = (unsigned char *)malloc(bytes);
        if (pixels == NULL) {
            WebPAnimDecoderDelete(decoder);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        memcpy(pixels, canvas, bytes);

        WebPAnimDecoderDelete(decoder);

        width = (int)info.canvas_width;
        height = (int)info.canvas_height;
        frame_count = info.frame_count;
    }

    stride = width * 4;
    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride,
            components, max_dimension, free, malloc, &source_width, &source_height)) {
        free(pixels);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    pixel_count = (size_t)width * (size_t)height;

    if (output_format == NV_FORMAT_PIXEL_RGBA8) {
        out_image->pixels = pixels;
    }
    else {
        out_image->pixels = (unsigned char *)malloc(pixel_count * (size_t)components);
        if (out_image->pixels == NULL) {
            free(pixels);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        nv_rgba_to_format(out_image->pixels, pixels, pixel_count, output_format);
        free(pixels);
    }

    out_image->width = (int32_t)width;
    out_image->height = (int32_t)height;
    out_image->stride = (int32_t)width * components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * (size_t)components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = frame_count;
    out_image->frame_delay_ms = frame_delay_ms;
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
    free(buffer->data);
    buffer->data = NULL;
    buffer->size = 0;
}
