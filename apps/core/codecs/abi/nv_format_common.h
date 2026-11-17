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

static void nv_swap_red_blue(unsigned char *pixels, size_t pixel_count, int components)
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

static void nv_rgba_to_format(
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
static nv_format_status nv_encode_unsupported(
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

#endif /* NV_FORMAT_COMMON_H */
