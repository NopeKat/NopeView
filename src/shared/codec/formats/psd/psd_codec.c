// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * PSD format codec.
 * Implements the format ABI (nv_format_abi.h) for the merged image of Photoshop
 * documents, both PSD (version 1) and PSB (version 2). Layers, masks and extra
 * channels are skipped, so only the flattened composite is shown.
 * Handles 8 and 16 bit depth, raw, RLE, ZIP and ZIP with prediction data, and
 * the RGB, grayscale, indexed, CMYK and Lab colour modes.
 * Decoding only.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "miniz.h"

#define PSD_MAX_DIMENSION 300000u
#define PSD_MAX_CHANNELS 24u
#define PSD_MODE_BITMAP 0u
#define PSD_MODE_GRAYSCALE 1u
#define PSD_MODE_INDEXED 2u
#define PSD_MODE_RGB 3u
#define PSD_MODE_CMYK 4u
#define PSD_MODE_LAB 9u

typedef struct psd_cursor {
    const unsigned char *data;
    size_t size;
    size_t offset;
} psd_cursor;

/* Photoshop writes every header field most significant byte first. */
static uint16_t psd_u16(const psd_cursor *cursor, size_t offset)
{
    return (uint16_t)(((uint32_t)cursor->data[offset] << 8) | cursor->data[offset + 1u]);
}

static uint32_t psd_u32(const psd_cursor *cursor, size_t offset)
{
    return ((uint32_t)cursor->data[offset] << 24) |
           ((uint32_t)cursor->data[offset + 1u] << 16) |
           ((uint32_t)cursor->data[offset + 2u] << 8) |
           (uint32_t)cursor->data[offset + 3u];
}

static uint64_t psd_u64(const psd_cursor *cursor, size_t offset)
{
    uint64_t high = psd_u32(cursor, offset);
    uint64_t low = psd_u32(cursor, offset + 4u);
    return (high << 32) | low;
}

/* Moves the cursor past a section length field and its payload. */
static int psd_skip_section(psd_cursor *cursor, int wide)
{
    size_t length_size = wide ? 8u : 4u;
    uint64_t length;

    if (cursor->offset + length_size > cursor->size) {
        return 0;
    }
    length = wide ? psd_u64(cursor, cursor->offset) : psd_u32(cursor, cursor->offset);
    cursor->offset += length_size;
    if (length > (uint64_t)(cursor->size - cursor->offset)) {
        return 0;
    }
    cursor->offset += (size_t)length;
    return 1;
}

/* Rows are packed with the classic PackBits byte runs. */
static int psd_packbits_decode(const unsigned char *source, size_t source_size,
                               unsigned char *dest, size_t dest_size)
{
    size_t read_offset = 0u;
    size_t written = 0u;

    while (read_offset < source_size && written < dest_size) {
        int control = (int)(signed char)source[read_offset++];
        size_t count;

        if (control >= 0) {
            count = (size_t)control + 1u;
            if (read_offset + count > source_size) {
                count = source_size - read_offset;
            }
            if (count > dest_size - written) {
                count = dest_size - written;
            }
            memcpy(dest + written, source + read_offset, count);
            read_offset += count;
            written += count;
        }
        else if (control != -128) {
            unsigned char value;
            count = (size_t)(1 - control);
            if (read_offset >= source_size) {
                break;
            }
            value = source[read_offset++];
            if (count > dest_size - written) {
                count = dest_size - written;
            }
            memset(dest + written, value, count);
            written += count;
        }
    }

    return written == dest_size;
}

/* ZIP with prediction stores row differences, so they are added back here. */
static void psd_unpredict(unsigned char *plane, size_t width, size_t row_bytes,
                          size_t height, int depth)
{
    size_t row;

    for (row = 0u; row < height; row++) {
        unsigned char *line = plane + row * row_bytes;
        size_t index;

        if (depth == 16) {
            for (index = 1u; index < width; index++) {
                unsigned int previous = (unsigned int)line[(index - 1u) * 2u] << 8 |
                                        line[(index - 1u) * 2u + 1u];
                unsigned int value = (unsigned int)line[index * 2u] << 8 |
                                     line[index * 2u + 1u];
                value = (value + previous) & 0xffffu;
                line[index * 2u] = (unsigned char)(value >> 8);
                line[index * 2u + 1u] = (unsigned char)(value & 0xffu);
            }
        }
        else {
            for (index = 1u; index < row_bytes; index++) {
                line[index] = (unsigned char)(line[index] + line[index - 1u]);
            }
        }
    }
}

/* Reads one byte from a plane row, reducing 16 bit samples to their top byte. */
static unsigned char psd_sample(const unsigned char *line, size_t x, int depth)
{
    if (depth == 16) {
        return line[x * 2u];
    }
    return line[x];
}

/* Lab is stored against the D50 white point that Photoshop documents assume. */
static unsigned char psd_lab_to_byte(double lightness, double a, double b, int channel)
{
    double fy = (lightness + 16.0) / 116.0;
    double fx = fy + a / 500.0;
    double fz = fy - b / 200.0;
    double xr = fx > 0.206897 ? fx * fx * fx : (116.0 * fx - 16.0) / 903.3;
    double yr = lightness > 8.0 ? fy * fy * fy : lightness / 903.3;
    double zr = fz > 0.206897 ? fz * fz * fz : (116.0 * fz - 16.0) / 903.3;
    double x = xr * 0.9642;
    double y = yr;
    double z = zr * 0.8249;
    double linear;
    double value;

    switch (channel) {
    case 0:
        linear = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        break;
    case 1:
        linear = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        break;
    default:
        linear = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        break;
    }

    if (!(linear > 0.0)) {
        return 0;
    }
    if (linear >= 1.0) {
        return 255;
    }
    value = linear <= 0.0031308 ? 12.92 * linear : 1.055 * pow(linear, 1.0 / 2.4) - 0.055;
    if (!(value > 0.0)) {
        return 0;
    }
    if (value >= 1.0) {
        return 255;
    }
    return (unsigned char)(value * 255.0 + 0.5);
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

    out_info->name = "Photoshop";
    out_info->format_id = "psd";
    out_info->extensions = "psd,psb";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    psd_cursor cursor;
    unsigned int channels;
    unsigned int width;
    unsigned int height;
    unsigned int depth;
    unsigned int mode;
    unsigned int plane_channels;
    unsigned int compression;
    size_t row_bytes;
    size_t plane_bytes;
    size_t counts_offset = 0u;
    size_t counts_stride = 2u;
    unsigned char palette[768];
    int have_palette = 0;
    unsigned char *canvas = NULL;
    unsigned char *inflated = NULL;
    unsigned char *pixels = NULL;
    unsigned char *row_buffer = NULL;
    int components;
    int stride;
    int output_width;
    int output_height;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    unsigned int channel;
    int narrow;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* One flattened image per file, so only frame 0 is valid. */
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (request->data == NULL || request->data_size == 0u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES || request->data_size < 26u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    cursor.data = request->data;
    cursor.size = request->data_size;
    cursor.offset = 26u;

    if (memcmp(cursor.data, "8BPS", 4u) != 0) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (psd_u16(&cursor, 4u) == 1u) {
        narrow = 1;
    }
    else if (psd_u16(&cursor, 4u) == 2u) {
        narrow = 0;
    }
    else {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    channels = psd_u16(&cursor, 12u);
    height = psd_u32(&cursor, 14u);
    width = psd_u32(&cursor, 18u);
    depth = psd_u16(&cursor, 22u);
    mode = psd_u16(&cursor, 24u);

    if (channels == 0u || channels > PSD_MAX_CHANNELS ||
        width == 0u || height == 0u ||
        width > PSD_MAX_DIMENSION || height > PSD_MAX_DIMENSION ||
        (depth != 8u && depth != 16u)) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (mode != PSD_MODE_GRAYSCALE && mode != PSD_MODE_INDEXED &&
        mode != PSD_MODE_RGB && mode != PSD_MODE_CMYK && mode != PSD_MODE_LAB) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if ((size_t)width * (size_t)height > NV_MAX_DECODE_BYTES / 4u) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    /* Colour mode data sits first; the palette of indexed files lives inside. */
    if (cursor.offset + 4u > cursor.size) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    {
        uint32_t color_length = psd_u32(&cursor, cursor.offset);
        cursor.offset += 4u;
        if (color_length > cursor.size - cursor.offset) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        if (mode == PSD_MODE_INDEXED) {
            if (color_length < sizeof(palette)) {
                return NV_FORMAT_ERR_DECODE_FAILED;
            }
            memcpy(palette, cursor.data + cursor.offset, sizeof(palette));
            have_palette = 1;
        }
        cursor.offset += color_length;
    }

    /* Image resources and the layer stack are not needed for the composite. */
    if (!psd_skip_section(&cursor, 0) || !psd_skip_section(&cursor, narrow ? 0 : 1)) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    if (cursor.offset + 2u > cursor.size) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    compression = psd_u16(&cursor, cursor.offset);
    cursor.offset += 2u;

    row_bytes = (size_t)width * (size_t)(depth / 8u);
    plane_bytes = row_bytes * (size_t)height;
    pixel_count = (size_t)width * (size_t)height;

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    canvas = (unsigned char *)malloc(pixel_count * 4u);
    if (canvas == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memset(canvas, 0, pixel_count * 4u);

    if (compression == 1u) {
        counts_stride = narrow ? 2u : 4u;
        counts_offset = cursor.offset;
        if (cursor.size - counts_offset < (size_t)channels * (size_t)height * counts_stride) {
            free(canvas);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        cursor.offset += (size_t)channels * (size_t)height * counts_stride;
    }
    else if (compression == 2u || compression == 3u) {
        size_t total = plane_bytes * (size_t)channels;
        size_t unpacked;
        inflated = (unsigned char *)malloc(total);
        if (inflated == NULL) {
            free(canvas);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        unpacked = tinfl_decompress_mem_to_mem(
            inflated, total, cursor.data + cursor.offset,
            cursor.size - cursor.offset, TINFL_FLAG_PARSE_ZLIB_HEADER);
        if (unpacked != total) {
            free(inflated);
            free(canvas);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        if (compression == 3u) {
            unsigned int plane;
            for (plane = 0u; plane < channels; plane++) {
                psd_unpredict(inflated + (size_t)plane * plane_bytes,
                              (size_t)width, row_bytes, (size_t)height, (int)depth);
            }
        }
    }
    else if (compression != 0u) {
        free(canvas);
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    if (compression == 1u) {
        row_buffer = (unsigned char *)malloc(row_bytes);
        if (row_buffer == NULL) {
            free(canvas);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
    }

    /* Channels are stored one plane after another; up to four are used. */
    plane_channels = channels > 4u ? 4u : channels;
    for (channel = 0u; channel < plane_channels; channel++) {
        unsigned int row;

        for (row = 0u; row < height; row++) {
            const unsigned char *line = NULL;
            unsigned int column;

            if (compression == 0u) {
                size_t offset = cursor.offset + (size_t)channel * plane_bytes +
                                (size_t)row * row_bytes;
                if (offset + row_bytes > cursor.size) {
                    free(row_buffer);
                    free(inflated);
                    free(canvas);
                    return NV_FORMAT_ERR_DECODE_FAILED;
                }
                line = cursor.data + offset;
            }
            else if (compression == 1u) {
                size_t count_offset = counts_offset +
                    ((size_t)channel * (size_t)height + (size_t)row) * counts_stride;
                size_t count = narrow
                    ? (size_t)psd_u16(&cursor, count_offset)
                    : (size_t)psd_u32(&cursor, count_offset);
                if (count > cursor.size - cursor.offset) {
                    free(row_buffer);
                    free(canvas);
                    return NV_FORMAT_ERR_DECODE_FAILED;
                }
                if (!psd_packbits_decode(cursor.data + cursor.offset, count,
                                         row_buffer, row_bytes)) {
                    free(row_buffer);
                    free(canvas);
                    return NV_FORMAT_ERR_DECODE_FAILED;
                }
                cursor.offset += count;
                line = row_buffer;
            }
            else {
                line = inflated + (size_t)channel * plane_bytes + (size_t)row * row_bytes;
            }

            for (column = 0u; column < width; column++) {
                unsigned char sample = psd_sample(line, (size_t)column, (int)depth);
                unsigned char *target = canvas +
                    ((size_t)row * (size_t)width + (size_t)column) * 4u;

                target[channel] = sample;
            }
        }
    }

    free(row_buffer);
    row_buffer = NULL;
    free(inflated);
    inflated = NULL;

    /* Plane bytes are already in place; mode conversions come last. */
    if (mode == PSD_MODE_RGB) {
        size_t index;
        for (index = 0u; index < pixel_count; index++) {
            canvas[index * 4u + 3u] = channels >= 4u ? canvas[index * 4u + 3u] : 255u;
        }
    }
    else if (mode == PSD_MODE_GRAYSCALE) {
        size_t index;
        for (index = 0u; index < pixel_count; index++) {
            unsigned char grey = canvas[index * 4u];
            canvas[index * 4u] = grey;
            canvas[index * 4u + 1u] = grey;
            canvas[index * 4u + 2u] = grey;
            canvas[index * 4u + 3u] = channels >= 2u ? canvas[index * 4u + 1u] : 255u;
        }
    }
    else if (mode == PSD_MODE_INDEXED) {
        size_t index;
        if (!have_palette) {
            free(canvas);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        for (index = 0u; index < pixel_count; index++) {
            unsigned int entry = canvas[index * 4u];
            canvas[index * 4u] = palette[entry];
            canvas[index * 4u + 1u] = palette[256u + entry];
            canvas[index * 4u + 2u] = palette[512u + entry];
            canvas[index * 4u + 3u] = 255u;
        }
    }
    else if (mode == PSD_MODE_CMYK) {
        size_t index;
        for (index = 0u; index < pixel_count; index++) {
            unsigned int cyan = canvas[index * 4u];
            unsigned int magenta = canvas[index * 4u + 1u];
            unsigned int yellow = canvas[index * 4u + 2u];
            unsigned int black = canvas[index * 4u + 3u];
            canvas[index * 4u] = (unsigned char)((255u - cyan) * (255u - black) / 255u);
            canvas[index * 4u + 1u] = (unsigned char)((255u - magenta) * (255u - black) / 255u);
            canvas[index * 4u + 2u] = (unsigned char)((255u - yellow) * (255u - black) / 255u);
            canvas[index * 4u + 3u] = 255u;
        }
    }
    else {
        size_t index;
        for (index = 0u; index < pixel_count; index++) {
            double lightness = (double)canvas[index * 4u] * 100.0 / 255.0;
            double a = (double)canvas[index * 4u + 1u] - 128.0;
            double b = (double)canvas[index * 4u + 2u] - 128.0;
            unsigned char red = psd_lab_to_byte(lightness, a, b, 0);
            unsigned char green = psd_lab_to_byte(lightness, a, b, 1);
            unsigned char blue = psd_lab_to_byte(lightness, a, b, 2);
            canvas[index * 4u] = red;
            canvas[index * 4u + 1u] = green;
            canvas[index * 4u + 2u] = blue;
            canvas[index * 4u + 3u] = 255u;
        }
    }

    pixels = canvas;
    canvas = NULL;
    stride = (int)width * 4;
    output_width = (int)width;
    output_height = (int)height;

    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &output_width, &output_height, &stride, 4, max_dimension,
            free, malloc, &source_width, &source_height)) {
        free(pixels);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    pixel_count = (size_t)output_width * (size_t)output_height;
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

    out_image->width = output_width;
    out_image->height = output_height;
    out_image->stride = output_width * components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * (size_t)components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = 1u;
    out_image->frame_delay_ms = 0u;
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
