/*
 * PNG format codec.
 * Implements the format ABI (nv_format_abi.h).
 * Encoding uses stb_image_write. Decoding walks the chunk stream itself so
 * animated PNG files play frame by frame: every frame is rebuilt into a
 * standalone PNG and handed to stb_image, then composited on the canvas with
 * the dispose and blend operations the file asks for.
 */

#if defined(NV_CODEC_MEMORY_BUDGET)
#include "nv_stbi_alloc.h"
#define STBI_MALLOC nv_stbi_malloc
#define STBI_REALLOC nv_stbi_realloc
#define STBI_FREE nv_stbi_free
#endif

#if defined(NV_CODEC_MEMORY_BUDGET)
#define NV_DOWNSCALE_ALLOC nv_stbi_malloc
#else
#define NV_DOWNSCALE_ALLOC malloc
#endif

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#include "stb_image.h"

#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STBI_WRITE_NO_STDIO
#include "stb_image_write.h"

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

/* A file that announces more frames than this is treated as malformed. */
#define NV_PNG_MAX_FRAMES 4096u

typedef struct nv_png_chunk_reader {
    const unsigned char *data;
    size_t size;
    size_t offset;
} nv_png_chunk_reader;

typedef struct nv_png_frame {
    int has_control;
    uint32_t width;
    uint32_t height;
    uint32_t x_offset;
    uint32_t y_offset;
    uint32_t delay_num;
    uint32_t delay_den;
    unsigned char dispose_op;
    unsigned char blend_op;
    unsigned char *data;
    size_t data_size;
    size_t data_capacity;
} nv_png_frame;

typedef struct nv_png_header {
    uint32_t width;
    uint32_t height;
    unsigned char bit_depth;
    unsigned char color_type;
    unsigned char compression;
    unsigned char filter;
    unsigned char interlace;
    const unsigned char *palette;
    size_t palette_size;
    const unsigned char *transparency;
    size_t transparency_size;
} nv_png_header;

/* Decoded animation frames are kept per file. Playback asks for one frame after
 * another, and without the cache every request walks the run from the first
 * frame again, so the last frame of a long animation costs the whole run. One
 * animation is cached at a time, bounded by a byte budget. */
#define NV_PNG_CACHE_SLOTS 1
#define NV_PNG_CACHE_BUDGET_BYTES ((size_t)64u * 1024u * 1024u)

typedef struct nv_png_cache {
    char *path;
    int64_t size;
    int64_t mtime;
    uint32_t width;
    uint32_t height;
    uint32_t frames;
    uint32_t *delays;
    unsigned char *pixels;
    size_t frame_bytes;
    uint64_t last_used;
} nv_png_cache;

static nv_png_cache g_cache[NV_PNG_CACHE_SLOTS];
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

/* Decode stages are timed when NOPEVIEW_UI_PERF is set, the same switch the
 * managed side uses, so a slow open can be attributed to a stage instead of
 * the whole call. Lines are appended next to the managed probe log. */
static int png_perf_enabled(void)
{
    static int cached = -1;

    if (cached < 0) {
        const char *flag = getenv("NOPEVIEW_UI_PERF");
        cached = (flag != NULL && flag[0] != '\0' && flag[0] != '0') ? 1 : 0;
    }
    return cached;
}

static double png_perf_now_ms(void)
{
#if defined(_WIN32)
    LARGE_INTEGER frequency;
    LARGE_INTEGER counter;

    QueryPerformanceFrequency(&frequency);
    QueryPerformanceCounter(&counter);
    return (double)counter.QuadPart * 1000.0 / (double)frequency.QuadPart;
#else
    return 0.0;
#endif
}

static void png_perf_write(int width, int height, double total, double scan,
    double inflate, double compose, double scale)
{
#if defined(_WIN32)
    const char *local = getenv("LOCALAPPDATA");
    char path[512];
    FILE *handle;

    if (local == NULL || local[0] == '\0') {
        return;
    }
    snprintf(path, sizeof(path), "%s\\NopeView\\native-perf.log", local);
    handle = fopen(path, "a");
    if (handle == NULL) {
        return;
    }
    fprintf(handle,
        "png-decode %.1f ms scan=%.1f inflate=%.1f compose=%.1f scale=%.1f px=%dx%d\n",
        total, scan, inflate, compose, scale, width, height);
    fclose(handle);
#else
    (void)width;
    (void)height;
    (void)total;
    (void)scan;
    (void)inflate;
    (void)compose;
    (void)scale;
#endif
}

#if defined(_WIN32)
extern __declspec(dllimport) int __stdcall MultiByteToWideChar(
    unsigned int code_page, unsigned long flags, const char *multi_byte,
    int multi_byte_count, wchar_t *wide, int wide_count);

#define NV_PNG_UTF8_CODE_PAGE 65001u

static wchar_t *png_utf8_to_wide(const char *path)
{
    int count = MultiByteToWideChar(NV_PNG_UTF8_CODE_PAGE, 0u, path, -1, NULL, 0);
    wchar_t *wide;

    if (count <= 0) {
        return NULL;
    }
    wide = (wchar_t *)malloc((size_t)count * sizeof(wchar_t));
    if (wide == NULL) {
        return NULL;
    }
    if (MultiByteToWideChar(NV_PNG_UTF8_CODE_PAGE, 0u, path, -1, wide, count) <= 0) {
        free(wide);
        return NULL;
    }
    return wide;
}
#endif

/* The slot is only reused when the file on disk is still the one that was
 * decoded, so an edited animation is never served from stale pixels. */
static int png_file_stamp(const char *path, int64_t *out_size, int64_t *out_mtime)
{
#if defined(_WIN32)
    wchar_t *wide = png_utf8_to_wide(path);
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

static void cache_clear(nv_png_cache *slot)
{
    free(slot->path);
    free(slot->delays);
    free(slot->pixels);
    memset(slot, 0, sizeof(*slot));
}

/* Caller holds the cache lock. */
static nv_png_cache *cache_find(const char *path, int64_t size, int64_t mtime)
{
    int index;

    for (index = 0; index < NV_PNG_CACHE_SLOTS; index++) {
        nv_png_cache *slot = &g_cache[index];

        if (slot->path != NULL && strcmp(slot->path, path) == 0 &&
            slot->size == size && slot->mtime == mtime) {
            slot->last_used = ++g_cache_tick;
            return slot;
        }
    }
    return NULL;
}

/* Caller holds the cache lock and hands over pixels and delays; on success the
 * slot owns them and the caller must not free them again. */
static int cache_store(const char *path, int64_t size, int64_t mtime,
    uint32_t width, uint32_t height, uint32_t frames,
    unsigned char *pixels, uint32_t *delays)
{
    size_t path_bytes = strlen(path) + 1u;
    char *path_copy = (char *)malloc(path_bytes);
    nv_png_cache *slot;

    if (path_copy == NULL) {
        return 0;
    }
    memcpy(path_copy, path, path_bytes);

    /* The least recently used slot goes first, which with one slot means the
     * previous animation. */
    slot = &g_cache[0];
    if (slot->path != NULL) {
        cache_clear(slot);
    }

    slot->path = path_copy;
    slot->size = size;
    slot->mtime = mtime;
    slot->width = width;
    slot->height = height;
    slot->frames = frames;
    slot->delays = delays;
    slot->pixels = pixels;
    slot->frame_bytes = (size_t)width * (size_t)height * 4u;
    slot->last_used = ++g_cache_tick;
    return 1;
}

static uint32_t nv_png_crc_table[256];
static int nv_png_crc_ready = 0;

static uint32_t nv_png_crc(const unsigned char *data, size_t size, uint32_t crc)
{
    size_t index;

    if (!nv_png_crc_ready) {
        uint32_t entry;
        int bit;
        for (entry = 0u; entry < 256u; entry++) {
            uint32_t value = entry;
            for (bit = 0; bit < 8; bit++) {
                value = (value & 1u) ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
            }
            nv_png_crc_table[entry] = value;
        }
        nv_png_crc_ready = 1;
    }

    for (index = 0u; index < size; index++) {
        crc = nv_png_crc_table[(crc ^ data[index]) & 0xFFu] ^ (crc >> 8);
    }
    return crc;
}

static uint32_t nv_png_read_be32(const unsigned char *data)
{
    return ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
        ((uint32_t)data[2] << 8) | (uint32_t)data[3];
}

static uint16_t nv_png_read_be16(const unsigned char *data)
{
    return (uint16_t)(((uint32_t)data[0] << 8) | (uint32_t)data[1]);
}

static void nv_png_write_be32(unsigned char *data, uint32_t value)
{
    data[0] = (unsigned char)(value >> 24);
    data[1] = (unsigned char)(value >> 16);
    data[2] = (unsigned char)(value >> 8);
    data[3] = (unsigned char)value;
}

static int nv_png_frame_append(nv_png_frame *frame, const unsigned char *data, size_t size)
{
    size_t needed = frame->data_size + size;
    unsigned char *grown;

    if (size == 0u) {
        return 1;
    }
    if (needed > frame->data_capacity) {
        size_t capacity = frame->data_capacity == 0u ? 4096u : frame->data_capacity;
        while (capacity < needed) {
            capacity *= 2u;
        }
        grown = (unsigned char *)realloc(frame->data, capacity);
        if (grown == NULL) {
            return 0;
        }
        frame->data = grown;
        frame->data_capacity = capacity;
    }
    memcpy(frame->data + frame->data_size, data, size);
    frame->data_size = needed;
    return 1;
}

static int nv_png_next_chunk(nv_png_chunk_reader *reader, const unsigned char **type,
                             const unsigned char **data, size_t *size)
{
    uint32_t length;

    if (reader->offset + 12u > reader->size) {
        return 0;
    }
    length = nv_png_read_be32(reader->data + reader->offset);
    if ((size_t)length > reader->size - reader->offset - 12u) {
        return 0;
    }
    *type = reader->data + reader->offset + 4u;
    *data = reader->data + reader->offset + 8u;
    *size = (size_t)length;
    reader->offset += 12u + (size_t)length;
    return 1;
}

static void nv_png_free_frames(nv_png_frame *frames, uint32_t count)
{
    uint32_t index;

    if (frames == NULL) {
        return;
    }
    for (index = 0u; index < count; index++) {
        free(frames[index].data);
    }
    free(frames);
}

/*
 * A frame is turned back into a standalone PNG so stb_image can read it: the
 * header keeps the file's pixel layout while the dimensions become the frame's
 * own, and only the palette and transparency chunks are carried along.
 */
static unsigned char *nv_png_build_frame_image(
    const nv_png_header *header,
    const nv_png_frame *frame,
    size_t *out_size)
{
    static const char signature[8] = { 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A };
    unsigned char *buffer;
    unsigned char *cursor;
    size_t size;
    uint32_t crc;

    size = 8u + 25u;
    if (header->palette != NULL) {
        size += 12u + header->palette_size;
    }
    if (header->transparency != NULL) {
        size += 12u + header->transparency_size;
    }
    size += 12u + frame->data_size + 12u;

    buffer = (unsigned char *)malloc(size);
    if (buffer == NULL) {
        return NULL;
    }

    memcpy(buffer, signature, 8u);
    cursor = buffer + 8u;

    nv_png_write_be32(cursor, 13u);
    memcpy(cursor + 4u, "IHDR", 4u);
    nv_png_write_be32(cursor + 8u, frame->width);
    nv_png_write_be32(cursor + 12u, frame->height);
    cursor[16] = header->bit_depth;
    cursor[17] = header->color_type;
    cursor[18] = header->compression;
    cursor[19] = header->filter;
    cursor[20] = header->interlace;
    crc = nv_png_crc(cursor + 4u, 17u, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
    nv_png_write_be32(cursor + 21u, crc);
    cursor += 25u;

    if (header->palette != NULL) {
        nv_png_write_be32(cursor, (uint32_t)header->palette_size);
        memcpy(cursor + 4u, "PLTE", 4u);
        memcpy(cursor + 8u, header->palette, header->palette_size);
        crc = nv_png_crc(cursor + 4u, 4u + header->palette_size, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
        nv_png_write_be32(cursor + 8u + header->palette_size, crc);
        cursor += 12u + header->palette_size;
    }

    if (header->transparency != NULL) {
        nv_png_write_be32(cursor, (uint32_t)header->transparency_size);
        memcpy(cursor + 4u, "tRNS", 4u);
        memcpy(cursor + 8u, header->transparency, header->transparency_size);
        crc = nv_png_crc(cursor + 4u, 4u + header->transparency_size, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
        nv_png_write_be32(cursor + 8u + header->transparency_size, crc);
        cursor += 12u + header->transparency_size;
    }

    nv_png_write_be32(cursor, (uint32_t)frame->data_size);
    memcpy(cursor + 4u, "IDAT", 4u);
    memcpy(cursor + 8u, frame->data, frame->data_size);
    crc = nv_png_crc(cursor + 4u, 4u + frame->data_size, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
    nv_png_write_be32(cursor + 8u + frame->data_size, crc);
    cursor += 12u + frame->data_size;

    nv_png_write_be32(cursor, 0u);
    memcpy(cursor + 4u, "IEND", 4u);
    crc = nv_png_crc(cursor + 4u, 4u, 0xFFFFFFFFu) ^ 0xFFFFFFFFu;
    nv_png_write_be32(cursor + 8u, crc);

    *out_size = size;
    return buffer;
}

/* Clears a region of the canvas, which is how the background dispose op is
 * expressed in RGBA terms. */
static void nv_png_clear_region(unsigned char *canvas, uint32_t canvas_width,
                                const nv_png_frame *frame)
{
    uint32_t row;
    for (row = 0u; row < frame->height; row++) {
        size_t offset = (size_t)(frame->y_offset + row) * canvas_width * 4u +
            (size_t)frame->x_offset * 4u;
        memset(canvas + offset, 0, (size_t)frame->width * 4u);
    }
}

static unsigned char *nv_png_copy_region(const unsigned char *canvas, uint32_t canvas_width,
                                         const nv_png_frame *frame)
{
    unsigned char *copy;
    uint32_t row;

    copy = (unsigned char *)malloc((size_t)frame->width * (size_t)frame->height * 4u);
    if (copy == NULL) {
        return NULL;
    }
    for (row = 0u; row < frame->height; row++) {
        size_t source = (size_t)(frame->y_offset + row) * canvas_width * 4u +
            (size_t)frame->x_offset * 4u;
        memcpy(copy + (size_t)row * frame->width * 4u, canvas + source, (size_t)frame->width * 4u);
    }
    return copy;
}

static void nv_png_restore_region(unsigned char *canvas, uint32_t canvas_width,
                                  const nv_png_frame *frame, const unsigned char *copy)
{
    uint32_t row;
    for (row = 0u; row < frame->height; row++) {
        size_t target = (size_t)(frame->y_offset + row) * canvas_width * 4u +
            (size_t)frame->x_offset * 4u;
        memcpy(canvas + target, copy + (size_t)row * frame->width * 4u, (size_t)frame->width * 4u);
    }
}

/* Draws one decoded frame on the canvas, either overwriting the region or
 * blending it over what is already there. */
static void nv_png_draw_frame(unsigned char *canvas, uint32_t canvas_width,
                              const nv_png_frame *frame, const unsigned char *pixels)
{
    uint32_t column;
    uint32_t row;
    int blend = frame->blend_op != 0u;

    for (row = 0u; row < frame->height; row++) {
        size_t source = (size_t)row * frame->width * 4u;
        size_t target = (size_t)(frame->y_offset + row) * canvas_width * 4u +
            (size_t)frame->x_offset * 4u;

        if (!blend) {
            memcpy(canvas + target, pixels + source, (size_t)frame->width * 4u);
            continue;
        }
        for (column = 0u; column < frame->width; column++) {
            const unsigned char *input = pixels + source + (size_t)column * 4u;
            unsigned char *output = canvas + target + (size_t)column * 4u;
            unsigned int alpha = input[3];

            /* Source-over compositing with straight alpha. */
            output[0] = (unsigned char)((input[0] * alpha + output[0] * (255u - alpha)) / 255u);
            output[1] = (unsigned char)((input[1] * alpha + output[1] * (255u - alpha)) / 255u);
            output[2] = (unsigned char)((input[2] * alpha + output[2] * (255u - alpha)) / 255u);
            output[3] = (unsigned char)(alpha + output[3] * (255u - alpha) / 255u);
        }
    }
}

/* Milliseconds a frame stays on screen; a zero denominator means 1/100 s per
 * the APNG specification. */
static uint32_t nv_png_frame_delay(const nv_png_frame *frame)
{
    uint32_t denominator = frame->delay_den == 0u ? 100u : frame->delay_den;
    return (uint32_t)(((uint64_t)frame->delay_num * 1000u) / denominator);
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

    out_info->name = "PNG";
    out_info->format_id = "png";
    out_info->extensions = "png,apng";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ENCODE | NV_FORMAT_CAP_ANIMATED;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    static const char signature[8] = { 0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A };
    nv_png_chunk_reader reader;
    nv_png_header header;
    nv_png_frame *frames = NULL;
    nv_png_frame *current = NULL;
    nv_png_frame still;
    uint32_t frame_count = 0;
    uint32_t canvas_width = 0;
    uint32_t canvas_height = 0;
    uint32_t target;
    uint32_t index;
    unsigned char *canvas = NULL;
    unsigned char *snapshot = NULL;
    unsigned char *frame_pixels = NULL;
    unsigned char *result = NULL;
    unsigned char *pixels;
    unsigned char *built = NULL;
    size_t built_size = 0u;
    int frame_width = 0;
    int frame_height = 0;
    int channels = 0;
    int width;
    int height;
    int components;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    uint32_t frame_delay_ms = 0u;
    int idat_to_frame = -1;
    size_t pixel_count;
    size_t canvas_bytes;
    size_t frame_bytes = 0u;
    uint32_t last = 0u;
    int cache_all = 0;
    int64_t file_size = 0;
    int64_t file_mtime = 0;
    int perf_on = 0;
    double perf_start = 0.0;
    double perf_stage = 0.0;
    double perf_scan = 0.0;
    double perf_inflate = 0.0;
    double perf_compose = 0.0;
    double perf_scale = 0.0;
    nv_png_cache *slot = NULL;
    unsigned char *cache_pixels = NULL;
    uint32_t *cache_delays = NULL;
    nv_format_status status = NV_FORMAT_OK;

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
    if (request->data_size < 8u || memcmp(request->data, signature, 8u) != 0) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);
    memset(&header, 0, sizeof(header));
    memset(&still, 0, sizeof(still));

    perf_on = png_perf_enabled();
    if (perf_on) {
        perf_start = png_perf_now_ms();
        perf_stage = perf_start;
    }

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    /* A cached animation answers a frame without walking the run again. The
     * pixels are copied out while the lock is held, so the slot cannot be
     * replaced under the copy. */
    if (request->path != NULL && request->path[0] != '\0' &&
        png_file_stamp(request->path, &file_size, &file_mtime)) {
        cache_lock();
        slot = cache_find(request->path, file_size, file_mtime);
        if (slot != NULL) {
            if (request->frame_index >= slot->frames) {
                status = NV_FORMAT_ERR_UNSUPPORTED;
                cache_unlock();
                goto cleanup;
            }
            frame_bytes = slot->frame_bytes;
            result = (unsigned char *)malloc(frame_bytes);
            if (result != NULL) {
                memcpy(result, slot->pixels +
                    (size_t)request->frame_index * frame_bytes, frame_bytes);
                canvas_width = slot->width;
                canvas_height = slot->height;
                frame_count = slot->frames;
                frame_delay_ms = slot->delays[request->frame_index];
            }
            cache_unlock();
            if (result == NULL) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                goto cleanup;
            }
            goto have_frame;
        }
        cache_unlock();
    }

    reader.data = request->data;
    reader.size = request->data_size;
    reader.offset = 8u;

    /* One pass over the chunk stream records the pixel layout, the animation
     * controls and the compressed bytes of every frame. */
    for (;;) {
        const unsigned char *type;
        const unsigned char *data;
        size_t size;

        if (!nv_png_next_chunk(&reader, &type, &data, &size)) {
            break;
        }
        if (memcmp(type, "IHDR", 4u) == 0 && size >= 13u) {
            header.width = nv_png_read_be32(data);
            header.height = nv_png_read_be32(data + 4u);
            header.bit_depth = data[8];
            header.color_type = data[9];
            header.compression = data[10];
            header.filter = data[11];
            header.interlace = data[12];
        }
        else if (memcmp(type, "PLTE", 4u) == 0) {
            header.palette = data;
            header.palette_size = size;
        }
        else if (memcmp(type, "tRNS", 4u) == 0) {
            header.transparency = data;
            header.transparency_size = size;
        }
        else if (memcmp(type, "fcTL", 4u) == 0 && size >= 26u) {
            nv_png_frame *grown;

            if (frame_count >= NV_PNG_MAX_FRAMES) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                break;
            }
            grown = (nv_png_frame *)realloc(frames, ((size_t)frame_count + 1u) * sizeof(nv_png_frame));
            if (grown == NULL) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                break;
            }
            frames = grown;
            current = &frames[frame_count];
            memset(current, 0, sizeof(*current));
            current->has_control = 1;
            current->width = nv_png_read_be32(data + 4u);
            current->height = nv_png_read_be32(data + 8u);
            current->x_offset = nv_png_read_be32(data + 12u);
            current->y_offset = nv_png_read_be32(data + 16u);
            current->delay_num = nv_png_read_be16(data + 20u);
            current->delay_den = nv_png_read_be16(data + 22u);
            current->dispose_op = data[24];
            current->blend_op = data[25];
            frame_count++;
        }
        else if (memcmp(type, "fdAT", 4u) == 0 && size >= 4u) {
            if (current == NULL) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                break;
            }
            if (!nv_png_frame_append(current, data + 4u, size - 4u)) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                break;
            }
        }
        else if (memcmp(type, "IDAT", 4u) == 0) {
            /* The default image supplies the first frame when the animation
             * control chunk came before it; otherwise it is not part of the
             * animation at all. The choice holds for every IDAT chunk in the
             * file, because a large image is split across several of them. */
            if (idat_to_frame < 0) {
                idat_to_frame = (frame_count == 1u && frames[0].has_control &&
                    frames[0].data_size == 0u) ? 0 : 1;
            }
            if (idat_to_frame == 0) {
                if (!nv_png_frame_append(&frames[0], data, size)) {
                    status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                    break;
                }
            }
            else if (!nv_png_frame_append(&still, data, size)) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                break;
            }
        }
        else if (memcmp(type, "IEND", 4u) == 0) {
            break;
        }
    }

    if (status != NV_FORMAT_OK || header.width == 0u || header.height == 0u) {
        status = status != NV_FORMAT_OK ? status : NV_FORMAT_ERR_DECODE_FAILED;
        goto cleanup;
    }

    if (perf_on) {
        double now = png_perf_now_ms();
        perf_scan = now - perf_stage;
        perf_stage = now;
    }

    canvas_width = header.width;
    canvas_height = header.height;
    if (canvas_width > 32768u || canvas_height > 32768u) {
        status = NV_FORMAT_ERR_DECODE_FAILED;
        goto cleanup;
    }

    if (frame_count == 0u) {
        /* A still image: the whole canvas is one frame with no controls. */
        still.width = canvas_width;
        still.height = canvas_height;
        still.blend_op = 0u;
        still.dispose_op = 0u;
        if (still.data_size == 0u) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }
        frame_count = 1u;
    }

    target = request->frame_index;
    if (target >= frame_count) {
        status = NV_FORMAT_ERR_UNSUPPORTED;
        goto cleanup;
    }

    canvas_bytes = (size_t)canvas_width * (size_t)canvas_height * 4u;
    canvas = (unsigned char *)calloc(canvas_bytes, 1u);
    if (canvas == NULL) {
        status = NV_FORMAT_ERR_OUT_OF_MEMORY;
        goto cleanup;
    }

    /* Caching needs every frame, so the first request for an animation inside
     * the budget walks once to the end, keeps the result, and every later
     * request is a copy. An animation over the budget keeps the old path. */
    cache_all = request->path != NULL && request->path[0] != '\0' &&
        frame_count > 1u && file_size != 0 &&
        canvas_bytes <= NV_PNG_CACHE_BUDGET_BYTES / (size_t)frame_count;
    if (cache_all) {
        cache_pixels = (unsigned char *)calloc(canvas_bytes, (size_t)frame_count);
        cache_delays = (uint32_t *)calloc((size_t)frame_count, sizeof(uint32_t));
        if (cache_pixels == NULL || cache_delays == NULL) {
            /* The cache is an optimization only; fall back to the walk. */
            cache_all = 0;
        }
    }

    last = cache_all ? frame_count - 1u : target;

    for (index = 0u; index <= last; index++) {
        nv_png_frame *frame = frame_count == 1u && frames == NULL ? &still : &frames[index];
        double stage_start = perf_on ? png_perf_now_ms() : 0.0;

        if (frame->width == 0u || frame->height == 0u ||
            frame->x_offset + frame->width > canvas_width ||
            frame->y_offset + frame->height > canvas_height) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }
        if (frame->data_size == 0u) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }

        built = nv_png_build_frame_image(&header, frame, &built_size);
        if (built == NULL) {
            status = NV_FORMAT_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }
        frame_pixels = stbi_load_from_memory(
            built, (int)built_size, &frame_width, &frame_height, &channels, 4);
        free(built);
        built = NULL;
        if (frame_pixels == NULL) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }
        if ((uint32_t)frame_width != frame->width || (uint32_t)frame_height != frame->height) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }

        if (perf_on) {
            double now = png_perf_now_ms();
            perf_inflate += now - stage_start;
            stage_start = now;
        }

        /* A frame that asks to be rolled back needs the region saved before it
         * is drawn. */
        if (frame->dispose_op == 2u) {
            snapshot = nv_png_copy_region(canvas, canvas_width, frame);
            if (snapshot == NULL) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                goto cleanup;
            }
        }

        nv_png_draw_frame(canvas, canvas_width, frame, frame_pixels);
        free(frame_pixels);
        frame_pixels = NULL;

        if (cache_all) {
            memcpy(cache_pixels + (size_t)index * canvas_bytes, canvas, canvas_bytes);
            cache_delays[index] = nv_png_frame_delay(frame);
        }
        if (index == target) {
            result = (unsigned char *)malloc(canvas_bytes);
            if (result == NULL) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                goto cleanup;
            }
            memcpy(result, canvas, canvas_bytes);
            frame_delay_ms = nv_png_frame_delay(frame);
        }
        /* The last frame needs no cleanup: nothing is drawn after it. */
        if (index != last) {
            if (frame->dispose_op == 1u) {
                nv_png_clear_region(canvas, canvas_width, frame);
            }
            else if (frame->dispose_op == 2u && snapshot != NULL) {
                nv_png_restore_region(canvas, canvas_width, frame, snapshot);
            }
        }
        free(snapshot);
        snapshot = NULL;

        if (perf_on) {
            perf_compose += png_perf_now_ms() - stage_start;
        }
    }

    if (cache_all) {
        cache_lock();
        if (cache_store(request->path, file_size, file_mtime,
                canvas_width, canvas_height, frame_count,
                cache_pixels, cache_delays)) {
            cache_pixels = NULL;
            cache_delays = NULL;
        }
        cache_unlock();
        cache_all = 0;
    }

    free(canvas);
    canvas = NULL;

have_frame:
    if (perf_on) {
        perf_stage = png_perf_now_ms();
    }
    pixels = result;
    result = NULL;
    width = (int)canvas_width;
    height = (int)canvas_height;
    stride = (int)canvas_width * 4;
    pixel_count = (size_t)canvas_width * (size_t)canvas_height;

    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride, 4, max_dimension,
            free, NV_DOWNSCALE_ALLOC, &source_width, &source_height)) {
        free(pixels);
        status = NV_FORMAT_ERR_OUT_OF_MEMORY;
        goto cleanup;
    }

    pixel_count = (size_t)width * (size_t)height;
    if (output_format == NV_FORMAT_PIXEL_RGBA8) {
        out_image->pixels = pixels;
    }
    else {
        out_image->pixels = (unsigned char *)malloc(pixel_count * (size_t)components);
        if (out_image->pixels == NULL) {
            free(pixels);
            status = NV_FORMAT_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }
        nv_rgba_to_format(out_image->pixels, pixels, pixel_count, output_format);
        free(pixels);
    }

    if (perf_on) {
        perf_scale = png_perf_now_ms() - perf_stage;
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

cleanup:
    free(built);
    free(frame_pixels);
    free(snapshot);
    free(canvas);
    free(result);
    free(cache_pixels);
    free(cache_delays);
    free(still.data);
    if (frames != NULL) {
        nv_png_free_frames(frames, frame_count);
    }
    if (status == NV_FORMAT_OK && perf_on) {
        png_perf_write((int)canvas_width, (int)canvas_height,
            png_perf_now_ms() - perf_start, perf_scan, perf_inflate,
            perf_compose, perf_scale);
    }
    return status;
}

nv_format_status nv_format_encode(const nv_format_encode_request *request, nv_format_buffer *out_buffer)
{
    const nv_format_image *image;
    nv_encode_sink sink;
    unsigned char *packed;
    size_t packed_size = 0u;
    int encoded;
    nv_format_status status;

    status = nv_validate_encode(request, out_buffer, &image);
    if (status != NV_FORMAT_OK) {
        return status;
    }

    /* PNG keeps the alpha channel, so four channels are always written. */
    packed = nv_image_to_rgb(image, 4, &packed_size);
    if (packed == NULL || packed_size == 0u) {
        free(packed);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    memset(&sink, 0, sizeof(sink));
    encoded = stbi_write_png_to_func(
        nv_encode_sink_write,
        &sink,
        image->width,
        image->height,
        4,
        packed,
        image->width * 4);
    free(packed);

    if (encoded == 0 || sink.failed != 0 || sink.data == NULL || sink.size == 0u) {
        free(sink.data);
        return NV_FORMAT_ERR_ENCODE_FAILED;
    }

    out_buffer->data = sink.data;
    out_buffer->size = sink.size;
    return NV_FORMAT_OK;
}

void nv_format_image_free(nv_format_image *image)
{
    if (image == NULL || image->pixels == NULL) {
        return;
    }
    stbi_image_free(image->pixels);
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
