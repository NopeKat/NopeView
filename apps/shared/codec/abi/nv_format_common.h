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

/*
 * Box-average downscale for tightly packed pixels with 1 to 4 components.
 * The longest edge becomes max_dimension and the aspect ratio is preserved.
 * Returns a malloc'ed buffer whose rows are new_width * components bytes.
 */
static inline unsigned char *nv_downscale_pixels(
    const unsigned char *source,
    int width,
    int height,
    int stride,
    int components,
    int32_t max_dimension,
    int *out_width,
    int *out_height,
    int *out_stride)
{
    int target_width;
    int target_height;
    size_t row_bytes;
    size_t total;
    unsigned char *result;
    int y;

    if (source == NULL || width <= 0 || height <= 0 || stride <= 0 ||
        components < 1 || components > 4 || max_dimension <= 0) {
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
    result = (unsigned char *)malloc(total);
    if (result == NULL) {
        return NULL;
    }

    for (y = 0; y < target_height; y++) {
        int source_y0 = (int)(((int64_t)y * height) / target_height);
        int source_y1 = (int)(((int64_t)(y + 1) * height) / target_height);
        int x;

        if (source_y1 <= source_y0) {
            source_y1 = source_y0 + 1;
        }
        if (source_y1 > height) {
            source_y1 = height;
        }

        for (x = 0; x < target_width; x++) {
            int source_x0 = (int)(((int64_t)x * width) / target_width);
            int source_x1 = (int)(((int64_t)(x + 1) * width) / target_width);
            int64_t sum[4] = {0, 0, 0, 0};
            int64_t count = 0;
            int channel;
            int source_y;

            if (source_x1 <= source_x0) {
                source_x1 = source_x0 + 1;
            }
            if (source_x1 > width) {
                source_x1 = width;
            }

            for (source_y = source_y0; source_y < source_y1; source_y++) {
                const unsigned char *row = source + (size_t)source_y * (size_t)stride;
                int source_x;

                for (source_x = source_x0; source_x < source_x1; source_x++) {
                    const unsigned char *pixel = row + (size_t)source_x * (size_t)components;

                    for (channel = 0; channel < components; channel++) {
                        sum[channel] += pixel[channel];
                    }
                    count++;
                }
            }

            for (channel = 0; channel < components; channel++) {
                result[(size_t)y * row_bytes + (size_t)x * (size_t)components + (size_t)channel] =
                    (unsigned char)(sum[channel] / count);
            }
        }
    }

    *out_width = target_width;
    *out_height = target_height;
    *out_stride = (int)row_bytes;
    return result;
}

/*
 * Replaces decoded pixels with a smaller box-averaged copy when the request
 * asks for one. The original buffer is released with release. Returns 1 on
 * success, including when no scaling was needed, and 0 on allocation failure.
 */
static inline int nv_limit_decoded_size(
    unsigned char **pixels,
    int *width,
    int *height,
    int *stride,
    int components,
    int32_t max_dimension,
    void (*release)(void *),
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
        &scaled_width, &scaled_height, &scaled_stride);
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
