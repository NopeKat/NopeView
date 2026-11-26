/*
 * nv_format_common.h - Shared helpers for format codecs.
 *
 * Every format DLL stays an independent library; this header only removes the
 * identical struct-size checks and "encode unsupported" boilerplate that used
 * to be copied into each codec source file.
 */

#ifndef NV_FORMAT_COMMON_H
#define NV_FORMAT_COMMON_H

#include "nv_format_abi.h"

#include <limits.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

/* stb_image takes a signed int byte count; make that limit explicit. */
#define NV_MAX_DECODE_BYTES ((size_t)INT_MAX)

/* Accept a zero size for callers that predate the struct_size convention. */
static int nv_size_ok(uint32_t struct_size, size_t required)
{
    return struct_size == 0u || (size_t)struct_size >= required;
}

static int nv_components_for(nv_format_pixel_format format)
{
    switch (format) {
    case NV_FORMAT_PIXEL_GRAY8:
        return 1;
    case NV_FORMAT_PIXEL_RGB8:
    case NV_FORMAT_PIXEL_BGR8:
        return 3;
    default:
        return 4;
    }
}

static nv_format_pixel_format nv_output_format(nv_format_pixel_format desired)
{
    switch (desired) {
    case NV_FORMAT_PIXEL_GRAY8:
    case NV_FORMAT_PIXEL_RGB8:
    case NV_FORMAT_PIXEL_BGR8:
    case NV_FORMAT_PIXEL_BGRA8:
        return desired;
    default:
        return NV_FORMAT_PIXEL_RGBA8;
    }
}

static inline void nv_swap_red_blue(unsigned char *pixels, size_t pixel_count, int components)
{
    size_t index;

    if (components != 3 && components != 4) {
        return;
    }
    for (index = 0u; index < pixel_count; index++) {
        size_t offset = index * (size_t)components;
        unsigned char red = pixels[offset];
        pixels[offset] = pixels[offset + 2u];
        pixels[offset + 2u] = red;
    }
}

static inline void nv_rgba_to_format(
    unsigned char *destination,
    const unsigned char *source,
    size_t pixel_count,
    nv_format_pixel_format format)
{
    size_t index;

    switch (format) {
    case NV_FORMAT_PIXEL_BGRA8:
        for (index = 0u; index < pixel_count; index++) {
            size_t offset = index * 4u;
            destination[offset] = source[offset + 2u];
            destination[offset + 1u] = source[offset + 1u];
            destination[offset + 2u] = source[offset];
            destination[offset + 3u] = source[offset + 3u];
        }
        break;
    case NV_FORMAT_PIXEL_RGB8:
        for (index = 0u; index < pixel_count; index++) {
            memcpy(destination + index * 3u, source + index * 4u, 3u);
        }
        break;
    case NV_FORMAT_PIXEL_BGR8:
        for (index = 0u; index < pixel_count; index++) {
            destination[index * 3u] = source[index * 4u + 2u];
            destination[index * 3u + 1u] = source[index * 4u + 1u];
            destination[index * 3u + 2u] = source[index * 4u];
        }
        break;
    case NV_FORMAT_PIXEL_GRAY8:
        for (index = 0u; index < pixel_count; index++) {
            unsigned int red = source[index * 4u];
            unsigned int green = source[index * 4u + 1u];
            unsigned int blue = source[index * 4u + 2u];
            destination[index] = (unsigned char)((red * 77u + green * 150u + blue * 29u) >> 8);
        }
        break;
    default:
        memcpy(destination, source, pixel_count * 4u);
        break;
    }
}

/*
 * One channel of an unbounded HDR image, tone mapped to eight bits: a Reinhard
 * curve first, then sRGB gamma. Alpha is a plain fraction and skips the curve.
 * The EXR and the Radiance codec both display through this.
 */
static inline unsigned char nv_float_channel_to_byte(float value)
{
    float mapped;

    if (!(value > 0.0f)) {
        return 0;
    }
    mapped = value / (1.0f + value);
    mapped = powf(mapped, 1.0f / 2.2f);
    if (mapped >= 1.0f) {
        return 255;
    }
    return (unsigned char)(mapped * 255.0f + 0.5f);
}

static inline unsigned char nv_float_alpha_to_byte(float value)
{
    if (!(value > 0.0f)) {
        return 0;
    }
    if (value >= 1.0f) {
        return 255;
    }
    return (unsigned char)(value * 255.0f + 0.5f);
}

/* Shared validation for formats that do not implement encoding yet. */
static inline nv_format_status nv_encode_unsupported(
    const nv_format_encode_request *request,
    nv_format_buffer *out_buffer)
{
    if (request == NULL || out_buffer == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(*request)) ||
        !nv_size_ok(out_buffer->struct_size, sizeof(*out_buffer))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }
    return NV_FORMAT_ERR_UNSUPPORTED;
}

/* Shared validation for encoders. */
static inline nv_format_status nv_validate_encode(
    const nv_format_encode_request *request,
    nv_format_buffer *out_buffer,
    const nv_format_image **out_image)
{
    const nv_format_image *image;

    if (request == NULL || out_buffer == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(*request)) ||
        !nv_size_ok(out_buffer->struct_size, sizeof(*out_buffer))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    image = request->image;
    if (image == NULL || image->pixels == NULL ||
        image->width <= 0 || image->height <= 0) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(image->struct_size, sizeof(*image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    *out_image = image;
    return NV_FORMAT_OK;
}

/* Reads one pixel as RGBA regardless of the stored layout. */
static inline int nv_read_pixel_rgba(
    const unsigned char *pixel,
    nv_format_pixel_format format,
    unsigned char rgba[4])
{
    switch (format) {
    case NV_FORMAT_PIXEL_RGBA8:
        memcpy(rgba, pixel, 4u);
        return 1;
    case NV_FORMAT_PIXEL_BGRA8:
        rgba[0] = pixel[2];
        rgba[1] = pixel[1];
        rgba[2] = pixel[0];
        rgba[3] = pixel[3];
        return 1;
    case NV_FORMAT_PIXEL_RGB8:
        rgba[0] = pixel[0];
        rgba[1] = pixel[1];
        rgba[2] = pixel[2];
        rgba[3] = 255u;
        return 1;
    case NV_FORMAT_PIXEL_BGR8:
        rgba[0] = pixel[2];
        rgba[1] = pixel[1];
        rgba[2] = pixel[0];
        rgba[3] = 255u;
        return 1;
    case NV_FORMAT_PIXEL_GRAY8:
        rgba[0] = pixel[0];
        rgba[1] = pixel[0];
        rgba[2] = pixel[0];
        rgba[3] = 255u;
        return 1;
    default:
        return 0;
    }
}

/*
 * Returns the max_dimension a caller asked for. The field was appended in ABI
 * 0.0.0.2, so older callers with a smaller struct_size report 0 (full size).
 */
static inline int32_t nv_request_max_dimension(const nv_format_decode_request *request)
{
    if (request == NULL || request->struct_size == 0u) {
        return 0;
    }
    if ((size_t)request->struct_size <
        offsetof(nv_format_decode_request, max_dimension) + sizeof(int32_t)) {
        return 0;
    }
    return request->max_dimension > 0 ? request->max_dimension : 0;
}

/* Reciprocal of a box-filter divisor: floor(2^31 / divisor). The product with
 * a sum below 2^31 lands at the exact quotient or one below it, so a single
 * borrow check restores integer-division rounding. The 31-bit form keeps the
 * reciprocal of 1 representable. */
static inline uint32_t nv_box_magic(uint32_t divisor)
{
    return (uint32_t)(((uint64_t)1u << 31) / (uint64_t)divisor);
}

static inline uint64_t nv_box_divide(uint64_t sum, uint32_t magic, uint32_t divisor)
{
    uint64_t quotient = (sum * (uint64_t)magic) >> 31;

    if (sum - quotient * (uint64_t)divisor >= (uint64_t)divisor) {
        quotient++;
    }
    return quotient;
}

/* Box-average downscale for tightly packed pixels with 1 to 4 components.
 * The longest edge becomes max_dimension and the aspect ratio is preserved.
 * Returns a malloc'ed buffer whose rows are new_width * components bytes. */
static inline unsigned char *nv_downscale_pixels(
    const unsigned char *source,
    int width,
    int height,
    int stride,
    int components,
    int32_t max_dimension,
    int *out_width,
    int *out_height,
    int *out_stride,
    void *(*allocate)(size_t))
{
    int target_width;
    int target_height;
    size_t row_bytes;
    size_t total;
    unsigned char *result;
    int *columns;
    int span_a;
    int x;
    int y;

    if (source == NULL || width <= 0 || height <= 0 || stride <= 0 ||
        components < 1 || components > 4 || max_dimension <= 0 ||
        allocate == NULL) {
        return NULL;
    }
    if (width <= max_dimension && height <= max_dimension) {
        return NULL;
    }

    if (width >= height) {
        target_width = max_dimension;
        target_height = (int)(((int64_t)height * max_dimension + width / 2) / width);
    }
    else {
        target_height = max_dimension;
        target_width = (int)(((int64_t)width * max_dimension + height / 2) / height);
    }
    if (target_width < 1) {
        target_width = 1;
    }
    if (target_height < 1) {
        target_height = 1;
    }

    row_bytes = (size_t)target_width * (size_t)components;
    total = row_bytes * (size_t)target_height;

    /* The source column ranges repeat on every output row, and finding them
     * needs two 64-bit divides per pixel, so they are resolved once here. */
    columns = (int *)malloc((size_t)target_width * 2u * sizeof(int));
    if (columns == NULL) {
        return NULL;
    }
    for (x = 0; x < target_width; x++) {
        int start = (int)(((int64_t)x * width) / target_width);
        int end = (int)(((int64_t)(x + 1) * width) / target_width);

        if (end <= start) {
            end = start + 1;
        }
        if (end > width) {
            end = width;
        }
        columns[x * 2] = start;
        columns[x * 2 + 1] = end;
    }

    /* Columns are a fixed width or one wider, so a row only ever needs two
     * divisors. Their reciprocals are built once per row and the per-channel
     * divide becomes a multiply plus a borrow fix-up. */
    span_a = width / target_width;
    if (span_a < 1) {
        span_a = 1;
    }

    result = (unsigned char *)allocate(total);
    if (result == NULL) {
        free(columns);
        return NULL;
    }

    for (y = 0; y < target_height; y++) {
        int source_y0 = (int)(((int64_t)y * height) / target_height);
        int source_y1 = (int)(((int64_t)(y + 1) * height) / target_height);
        int span_y;
        int count_a;
        int count_b;
        uint32_t magic_a;
        uint32_t magic_b;
        int row_fast;

        if (source_y1 <= source_y0) {
            source_y1 = source_y0 + 1;
        }
        if (source_y1 > height) {
            source_y1 = height;
        }
        span_y = source_y1 - source_y0;
        count_a = span_a * span_y;
        count_b = (span_a + 1) * span_y;
        magic_a = nv_box_magic(count_a);
        magic_b = nv_box_magic(count_b);
        /* The reciprocal stays exact only while the sums fit a 32-bit
         * magnitude; past that the row keeps plain division. */
        row_fast = (int64_t)255 * count_b <= (int64_t)INT_MAX;

        for (x = 0; x < target_width; x++) {
            int source_x0 = columns[x * 2];
            int source_x1 = columns[x * 2 + 1];
            int count;
            int use_magic;
            uint32_t magic;
            uint64_t sum[4] = {0u, 0u, 0u, 0u};
            int channel;
            int source_y;

            count = (source_x1 - source_x0) * span_y;
            if (row_fast && count == count_a) {
                magic = magic_a;
                use_magic = 1;
            }
            else if (row_fast && count == count_b) {
                magic = magic_b;
                use_magic = 1;
            }
            else {
                magic = 0u;
                use_magic = 0;
            }

            for (source_y = source_y0; source_y < source_y1; source_y++) {
                const unsigned char *row = source + (size_t)source_y * (size_t)stride;
                int source_x;

                for (source_x = source_x0; source_x < source_x1; source_x++) {
                    const unsigned char *pixel = row + (size_t)source_x * (size_t)components;

                    for (channel = 0; channel < components; channel++) {
                        sum[channel] += pixel[channel];
                    }
                }
            }

            for (channel = 0; channel < components; channel++) {
                result[(size_t)y * row_bytes + (size_t)x * (size_t)components + (size_t)channel] =
                    (unsigned char)(use_magic
                        ? nv_box_divide(sum[channel], magic, (uint32_t)count)
                        : sum[channel] / (uint64_t)count);
            }
        }
    }

    free(columns);

    *out_width = target_width;
    *out_height = target_height;
    *out_stride = (int)row_bytes;
    return result;
}

/*
 * Replaces decoded pixels with a smaller box-averaged copy when the request
 * asks for one. The original buffer is released with release. The replacement
 * uses allocate, which must match the allocator that release expects. Returns
 * 1 on success, including when no scaling was needed, and 0 on failure.
 */
static inline int nv_limit_decoded_size(
    unsigned char **pixels,
    int *width,
    int *height,
    int *stride,
    int components,
    int32_t max_dimension,
    void (*release)(void *),
    void *(*allocate)(size_t),
    int *source_width,
    int *source_height)
{
    unsigned char *scaled;
    int scaled_width = 0;
    int scaled_height = 0;
    int scaled_stride = 0;

    *source_width = *width;
    *source_height = *height;

    if (max_dimension <= 0 || (*width <= max_dimension && *height <= max_dimension)) {
        return 1;
    }

    scaled = nv_downscale_pixels(
        *pixels, *width, *height, *stride, components, max_dimension,
        &scaled_width, &scaled_height, &scaled_stride, allocate);
    if (scaled == NULL) {
        return 0;
    }

    release(*pixels);
    *pixels = scaled;
    *width = scaled_width;
    *height = scaled_height;
    *stride = scaled_stride;
    return 1;
}

/*
 * Packs an image into tightly packed RGB (components 3) or RGBA (components 4)
 * rows, which is the layout stb_image_write expects.
 */
static inline unsigned char *nv_image_to_rgb(
    const nv_format_image *image,
    int components,
    size_t *out_size)
{
    size_t width;
    size_t height;
    size_t row_bytes;
    size_t total;
    int source_components;
    unsigned char *result;
    size_t y;

    if (image == NULL || image->pixels == NULL ||
        image->width <= 0 || image->height <= 0) {
        return NULL;
    }
    if (components != 3 && components != 4) {
        return NULL;
    }

    width = (size_t)image->width;
    height = (size_t)image->height;
    source_components = nv_components_for(image->pixel_format);

    if (image->stride <= 0) {
        return NULL;
    }
    if ((size_t)image->stride < width * (size_t)source_components) {
        return NULL;
    }
    if (image->pixels_size < (size_t)image->stride * height) {
        return NULL;
    }
    if (width > SIZE_MAX / (size_t)components) {
        return NULL;
    }
    row_bytes = width * (size_t)components;
    if (height > SIZE_MAX / row_bytes) {
        return NULL;
    }
    total = row_bytes * height;

    result = (unsigned char *)malloc(total);
    if (result == NULL) {
        return NULL;
    }

    for (y = 0u; y < height; y++) {
        const unsigned char *row = image->pixels + y * (size_t)image->stride;
        unsigned char *destination = result + y * row_bytes;
        size_t x;

        for (x = 0u; x < width; x++) {
            unsigned char rgba[4];
            unsigned char *target = destination + x * (size_t)components;

            if (!nv_read_pixel_rgba(
                    row + x * (size_t)source_components,
                    image->pixel_format,
                    rgba)) {
                free(result);
                return NULL;
            }

            target[0] = rgba[0];
            target[1] = rgba[1];
            target[2] = rgba[2];
            if (components == 4) {
                target[3] = rgba[3];
            }
        }
    }

    *out_size = total;
    return result;
}

/* Growable memory sink for the stb_image_write write callbacks. */
typedef struct nv_encode_sink {
    unsigned char *data;
    size_t size;
    size_t capacity;
    int failed;
} nv_encode_sink;

static inline void nv_encode_sink_write(void *context, void *data, int size)
{
    nv_encode_sink *sink = (nv_encode_sink *)context;
    size_t needed;
    size_t capacity;
    unsigned char *grown;

    if (sink == NULL || sink->failed != 0) {
        return;
    }
    if (size < 0) {
        sink->failed = 1;
        return;
    }
    if (data == NULL || size == 0) {
        return;
    }

    needed = sink->size + (size_t)size;
    if (needed < sink->size) {
        sink->failed = 1;
        return;
    }

    if (needed > sink->capacity) {
        capacity = (sink->capacity == 0u) ? 4096u : sink->capacity;
        while (capacity < needed) {
            if (capacity > SIZE_MAX / 2u) {
                sink->failed = 1;
                return;
            }
            capacity *= 2u;
        }

        grown = (unsigned char *)realloc(sink->data, capacity);
        if (grown == NULL) {
            sink->failed = 1;
            return;
        }
        sink->data = grown;
        sink->capacity = capacity;
    }

    memcpy(sink->data + sink->size, data, (size_t)size);
    sink->size = needed;
}

#endif /* NV_FORMAT_COMMON_H */
