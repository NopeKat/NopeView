// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * DNG format codec.
 * DNG files are TIFF containers written by cameras and converters. The codec
 * shows the largest JPEG preview stored in the file, which is what viewers
 * normally do, and develops the colour filter array data with a bilinear
 * demosaic when the file carries no preview.
 * Only the first page is looked at, and raw data compressed as lossless JPEG
 * is not supported yet.
 * Decoding only.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_JPEG
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#define DNG_TAG_IMAGE_WIDTH 256u
#define DNG_TAG_IMAGE_LENGTH 257u
#define DNG_TAG_BITS_PER_SAMPLE 258u
#define DNG_TAG_COMPRESSION 259u
#define DNG_TAG_PHOTOMETRIC 262u
#define DNG_TAG_STRIP_OFFSETS 273u
#define DNG_TAG_ROWS_PER_STRIP 278u
#define DNG_TAG_STRIP_BYTE_COUNTS 279u
#define DNG_TAG_SUB_IFDS 330u
#define DNG_TAG_CFA_PATTERN 33422u
#define DNG_TAG_BLACK_LEVEL 50714u
#define DNG_TAG_WHITE_LEVEL 50717u
#define DNG_TAG_AS_SHOT_NEUTRAL 50729u

#define DNG_PHOTOMETRIC_RGB 2u
#define DNG_PHOTOMETRIC_YCBCR 6u
#define DNG_PHOTOMETRIC_CFA 32803u

#define DNG_MAX_VALUES 8

typedef struct dng_file {
    const unsigned char *data;
    size_t size;
    int big_endian;
} dng_file;

typedef struct dng_ifd {
    size_t offset;
    unsigned int entries;
} dng_ifd;

typedef struct dng_tag {
    unsigned int type;
    unsigned long count;
    size_t value_field;
} dng_tag;

static unsigned int dng_u16(const dng_file *file, size_t offset)
{
    if (offset + 2u > file->size) {
        return 0u;
    }
    if (file->big_endian) {
        return ((unsigned int)file->data[offset] << 8) | file->data[offset + 1u];
    }
    return ((unsigned int)file->data[offset + 1u] << 8) | file->data[offset];
}

static unsigned long dng_u32(const dng_file *file, size_t offset)
{
    if (offset + 4u > file->size) {
        return 0u;
    }
    if (file->big_endian) {
        return ((unsigned long)file->data[offset] << 24) |
               ((unsigned long)file->data[offset + 1u] << 16) |
               ((unsigned long)file->data[offset + 2u] << 8) |
               (unsigned long)file->data[offset + 3u];
    }
    return ((unsigned long)file->data[offset + 3u] << 24) |
           ((unsigned long)file->data[offset + 2u] << 16) |
           ((unsigned long)file->data[offset + 1u] << 8) |
           (unsigned long)file->data[offset];
}

static int dng_open_ifd(const dng_file *file, size_t offset, dng_ifd *ifd)
{
    if (offset + 2u > file->size) {
        return 0;
    }
    ifd->offset = offset;
    ifd->entries = dng_u16(file, offset);
    if (ifd->offset + 2u + (size_t)ifd->entries * 12u + 4u > file->size) {
        return 0;
    }
    return 1;
}

static int dng_find_tag(const dng_file *file, const dng_ifd *ifd, unsigned int tag, dng_tag *out)
{
    unsigned int index;

    for (index = 0u; index < ifd->entries; index++) {
        size_t entry = ifd->offset + 2u + (size_t)index * 12u;
        if (dng_u16(file, entry) != tag) {
            continue;
        }
        out->type = dng_u16(file, entry + 2u);
        out->count = dng_u32(file, entry + 4u);
        out->value_field = entry + 8u;
        return 1;
    }
    return 0;
}

/* Reads a tag that holds a few numbers, inline while they fit in the entry. */
static int dng_read_values(const dng_file *file, const dng_tag *tag,
                           unsigned long *values, int maximum)
{
    unsigned int item_size;
    size_t base;
    int count;
    int index;

    if (tag->type == 1u) {
        item_size = 1u;
    }
    else if (tag->type == 3u) {
        item_size = 2u;
    }
    else if (tag->type == 4u) {
        item_size = 4u;
    }
    else {
        return 0;
    }

    count = (int)tag->count;
    if (count > maximum) {
        count = maximum;
    }
    base = tag->count * item_size <= 4u ? tag->value_field : (size_t)dng_u32(file, tag->value_field);
    if (base + (size_t)tag->count * item_size > file->size) {
        return 0;
    }

    for (index = 0; index < count; index++) {
        size_t at = base + (size_t)index * item_size;
        if (item_size == 1u) {
            values[index] = file->data[at];
        }
        else if (item_size == 2u) {
            values[index] = dng_u16(file, at);
        }
        else {
            values[index] = dng_u32(file, at);
        }
    }
    return count;
}

static unsigned long dng_value(const dng_file *file, const dng_ifd *ifd,
                                unsigned int tag, unsigned long fallback)
{
    dng_tag found;
    unsigned long values[DNG_MAX_VALUES];

    if (!dng_find_tag(file, ifd, tag, &found)) {
        return fallback;
    }
    if (dng_read_values(file, &found, values, DNG_MAX_VALUES) < 1) {
        return fallback;
    }
    return values[0];
}

static int dng_strip_table(const dng_file *file, const dng_ifd *ifd,
                           unsigned long *offsets, unsigned long *counts, int maximum)
{
    dng_tag offset_tag;
    dng_tag count_tag;
    int strip_count;

    if (!dng_find_tag(file, ifd, DNG_TAG_STRIP_OFFSETS, &offset_tag) ||
        !dng_find_tag(file, ifd, DNG_TAG_STRIP_BYTE_COUNTS, &count_tag)) {
        return 0;
    }
    strip_count = (int)offset_tag.count;
    if (strip_count < 1 || strip_count > maximum || (int)count_tag.count < strip_count) {
        return 0;
    }
    if (dng_read_values(file, &offset_tag, offsets, maximum) < strip_count) {
        return 0;
    }
    if (dng_read_values(file, &count_tag, counts, maximum) < strip_count) {
        return 0;
    }
    return strip_count;
}

/* One JPEG preview candidate, kept while it is the largest seen so far. */
typedef struct dng_preview {
    int found;
    dng_ifd ifd;
    unsigned long offsets[DNG_MAX_VALUES];
    unsigned long counts[DNG_MAX_VALUES];
    int strips;
    size_t total;
    size_t pixels;
} dng_preview;

/*
 * JPEG previews sit in the strip table of the first page, of a sub page, or of
 * the next IFD. Every page is measured and the biggest picture wins.
 */
static void dng_consider_preview(const dng_file *file, size_t ifd_offset, dng_preview *best)
{
    dng_ifd ifd;
    unsigned long offsets[DNG_MAX_VALUES];
    unsigned long counts[DNG_MAX_VALUES];
    unsigned long width;
    unsigned long height;
    unsigned long photometric;
    int strips;
    int index;
    size_t total = 0u;
    size_t pixels;

    if (ifd_offset == 0u || !dng_open_ifd(file, ifd_offset, &ifd)) {
        return;
    }
    if (dng_value(file, &ifd, DNG_TAG_COMPRESSION, 0u) != 7u) {
        return;
    }
    photometric = dng_value(file, &ifd, DNG_TAG_PHOTOMETRIC, 0u);
    if (photometric != DNG_PHOTOMETRIC_YCBCR && photometric != DNG_PHOTOMETRIC_RGB) {
        return;
    }

    width = dng_value(file, &ifd, DNG_TAG_IMAGE_WIDTH, 0u);
    height = dng_value(file, &ifd, DNG_TAG_IMAGE_LENGTH, 0u);
    if (width == 0u || height == 0u) {
        return;
    }
    strips = dng_strip_table(file, &ifd, offsets, counts, DNG_MAX_VALUES);
    if (strips < 1) {
        return;
    }
    for (index = 0; index < strips; index++) {
        if ((size_t)offsets[index] + (size_t)counts[index] > file->size) {
            return;
        }
        total += (size_t)counts[index];
    }
    if (total == 0u) {
        return;
    }

    pixels = (size_t)width * (size_t)height;
    if (pixels <= best->pixels) {
        return;
    }
    best->found = 1;
    best->ifd = ifd;
    best->strips = strips;
    best->total = total;
    best->pixels = pixels;
    for (index = 0; index < strips; index++) {
        best->offsets[index] = offsets[index];
        best->counts[index] = counts[index];
    }
}

/** Copies the strips of a preview into one contiguous buffer. */
static unsigned char *dng_read_preview(const dng_file *file,
                                       const unsigned long *offsets,
                                       const unsigned long *counts,
                                       int strips, size_t total)
{
    unsigned char *buffer = (unsigned char *)malloc(total);
    size_t written = 0u;
    int index;

    if (buffer == NULL) {
        return NULL;
    }
    for (index = 0; index < strips; index++) {
        if (written + counts[index] > total) {
            free(buffer);
            return NULL;
        }
        memcpy(buffer + written, file->data + offsets[index], (size_t)counts[index]);
        written += (size_t)counts[index];
    }
    if (written != total) {
        free(buffer);
        return NULL;
    }
    return buffer;
}

/*
 * Bilinear demosaic: every output channel averages the near by samples that
 * carry that channel, with the pixel itself weighted highest. It is soft but
 * shows the picture without inventing colour.
 */
static void dng_demosaic(const float *samples, int width, int height,
                         const unsigned char *pattern, unsigned char *rgba)
{
    static const unsigned int weight[3][3] = {
        { 1u, 2u, 1u },
        { 2u, 4u, 2u },
        { 1u, 2u, 1u }
    };
    int y;
    int x;

    for (y = 0; y < height; y++) {
        for (x = 0; x < width; x++) {
            double sum[3] = { 0.0, 0.0, 0.0 };
            double total[3] = { 0.0, 0.0, 0.0 };
            unsigned char *out = rgba + ((size_t)y * (size_t)width + (size_t)x) * 4u;
            int dy;
            int dx;
            int channel;

            for (dy = -1; dy <= 1; dy++) {
                for (dx = -1; dx <= 1; dx++) {
                    int sy = y + dy;
                    int sx = x + dx;
                    unsigned int color;
                    double sample;
                    double base = (double)weight[dy + 1][dx + 1];

                    if (sy < 0 || sx < 0 || sy >= height || sx >= width) {
                        continue;
                    }
                    color = pattern[(sy & 1) * 2 + (sx & 1)];
                    if (color > 2u) {
                        continue;
                    }
                    sample = (double)samples[(size_t)sy * (size_t)width + (size_t)sx];
                    sum[color] += sample * base;
                    total[color] += base;
                }
            }

            for (channel = 0; channel < 3; channel++) {
                double value = total[channel] > 0.0 ? sum[channel] / total[channel] : 0.0;
                value = value <= 0.0 ? 0.0 : (value >= 1.0 ? 1.0 : pow(value, 1.0 / 2.2));
                out[channel] = (unsigned char)(value * 255.0 + 0.5);
            }
            out[3] = 255u;
        }
    }
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

    out_info->name = "Adobe DNG";
    out_info->format_id = "dng";
    out_info->extensions = "dng";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    dng_file file;
    dng_ifd first;
    dng_preview best;
    unsigned char *pixels = NULL;
    int width = 0;
    int height = 0;
    int components;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* One developed image per file, so only frame 0 is valid. */
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (request->data == NULL || request->data_size < 8u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    file.data = request->data;
    file.size = request->data_size;
    if (memcmp(file.data, "II\x2a\x00", 4u) == 0) {
        file.big_endian = 0;
    }
    else if (memcmp(file.data, "MM\x00\x2a", 4u) == 0) {
        file.big_endian = 1;
    }
    else {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    if (!dng_open_ifd(&file, (size_t)dng_u32(&file, 4u), &first)) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* A preview can live in the first page, in a sub page, or in the next IFD. */
    memset(&best, 0, sizeof(best));
    dng_consider_preview(&file, first.offset, &best);
    {
        dng_tag sub_ifds;
        unsigned long values[DNG_MAX_VALUES];
        int count;
        int index;
        size_t next;

        if (dng_find_tag(&file, &first, DNG_TAG_SUB_IFDS, &sub_ifds)) {
            count = dng_read_values(&file, &sub_ifds, values, DNG_MAX_VALUES);
            for (index = 0; index < count; index++) {
                dng_consider_preview(&file, (size_t)values[index], &best);
            }
        }
        next = (size_t)dng_u32(&file, first.offset + 2u + (size_t)first.entries * 12u);
        if (next != 0u) {
            dng_consider_preview(&file, next, &best);
        }
    }

    if (best.found) {
        unsigned char *jpeg = dng_read_preview(
            &file, best.offsets, best.counts, best.strips, best.total);
        int stub = 0;

        if (jpeg == NULL) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        pixels = stbi_load_from_memory(jpeg, (int)best.total, &width, &height, &stub, 4);
        free(jpeg);
        if (pixels == NULL) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        pixel_count = (size_t)width * (size_t)height;
        /* Repack so the preview limiter can reallocate the buffer. */
        {
            unsigned char *packed = (unsigned char *)malloc(pixel_count * 4u);
            if (packed == NULL) {
                stbi_image_free(pixels);
                return NV_FORMAT_ERR_OUT_OF_MEMORY;
            }
            memcpy(packed, pixels, pixel_count * 4u);
            stbi_image_free(pixels);
            pixels = packed;
        }
    }
    else {
        unsigned long image_width = dng_value(&file, &first, DNG_TAG_IMAGE_WIDTH, 0u);
        unsigned long image_height = dng_value(&file, &first, DNG_TAG_IMAGE_LENGTH, 0u);
        unsigned long bits = dng_value(&file, &first, DNG_TAG_BITS_PER_SAMPLE, 8u);
        unsigned long photometric = dng_value(&file, &first, DNG_TAG_PHOTOMETRIC, 0u);
        unsigned long compression = dng_value(&file, &first, DNG_TAG_COMPRESSION, 0u);
        unsigned long offsets[DNG_MAX_VALUES];
        unsigned long counts[DNG_MAX_VALUES];
        unsigned long black_level = dng_value(&file, &first, DNG_TAG_BLACK_LEVEL, 0u);
        unsigned long white_level = dng_value(&file, &first, DNG_TAG_WHITE_LEVEL, 0u);
        unsigned long rows_per_strip;
        unsigned char pattern[4] = { 0u, 1u, 1u, 2u };
        float *samples;
        int strips;
        int y;

        if (photometric != DNG_PHOTOMETRIC_CFA || compression != 1u ||
            (bits != 8u && bits != 16u) || image_width == 0u || image_height == 0u) {
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
        if (image_width > 65535u || image_height > 65535u ||
            (size_t)image_width * (size_t)image_height > NV_MAX_DECODE_BYTES / 8u) {
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
        strips = dng_strip_table(&file, &first, offsets, counts, DNG_MAX_VALUES);
        if (strips < 1) {
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
        if (white_level == 0u) {
            white_level = bits == 16u ? 65535u : 255u;
        }
        if (white_level <= black_level) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }

        /* The 2x2 pattern says which colour each position carries. */
        {
            dng_tag pattern_tag;
            unsigned long values[DNG_MAX_VALUES];
            if (dng_find_tag(&file, &first, DNG_TAG_CFA_PATTERN, &pattern_tag) &&
                dng_read_values(&file, &pattern_tag, values, DNG_MAX_VALUES) >= 4) {
                pattern[0] = (unsigned char)values[0];
                pattern[1] = (unsigned char)values[1];
                pattern[2] = (unsigned char)values[2];
                pattern[3] = (unsigned char)values[3];
            }
        }

        pixel_count = (size_t)image_width * (size_t)image_height;
        samples = (float *)malloc(pixel_count * sizeof(float));
        if (samples == NULL) {
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }

        /* Gains from AsShotNeutral; a missing tag leaves white balanced files
         * untouched. */
        {
            double neutral[3] = { 1.0, 1.0, 1.0 };
            dng_tag neutral_tag;

            if (dng_find_tag(&file, &first, DNG_TAG_AS_SHOT_NEUTRAL, &neutral_tag) &&
                neutral_tag.type == 5u && neutral_tag.count >= 3u) {
                size_t base = (size_t)dng_u32(&file, neutral_tag.value_field);
                int channel;
                if (base + 24u <= file.size) {
                    for (channel = 0; channel < 3; channel++) {
                        unsigned long numerator = dng_u32(&file, base + (size_t)channel * 8u);
                        unsigned long denominator = dng_u32(&file, base + (size_t)channel * 8u + 4u);
                        if (numerator != 0u && denominator != 0u) {
                            neutral[channel] = (double)denominator / (double)numerator;
                        }
                    }
                }
            }

            rows_per_strip = dng_value(&file, &first, DNG_TAG_ROWS_PER_STRIP, image_height);
            if (rows_per_strip == 0u) {
                rows_per_strip = image_height;
            }
            {
                double scale = 1.0 / (double)(white_level - black_level);
                for (y = 0; y < (int)image_height; y++) {
                    int strip_index = (int)((unsigned long)y / rows_per_strip);
                    size_t row_base;
                    size_t line;
                    int x;

                    if (strip_index >= strips) {
                        strip_index = strips - 1;
                    }
                    row_base = (size_t)offsets[strip_index] +
                               (size_t)((unsigned long)y % rows_per_strip) *
                               (size_t)image_width * (size_t)(bits / 8u);
                    if (row_base + (size_t)image_width * (size_t)(bits / 8u) > file.size) {
                        free(samples);
                        return NV_FORMAT_ERR_DECODE_FAILED;
                    }
                    line = row_base;
                    for (x = 0; x < (int)image_width; x++) {
                        unsigned int raw;
                        unsigned int color = pattern[(y & 1) * 2 + (x & 1)];
                        double value;

                        if (bits == 16u) {
                            raw = (unsigned int)dng_u16(&file, line);
                            line += 2u;
                        }
                        else {
                            raw = file.data[line];
                            line += 1u;
                        }
                        value = raw < black_level ? 0.0 : ((double)raw - (double)black_level) * scale;
                        if (color < 3u) {
                            value *= neutral[color];
                        }
                        samples[(size_t)y * (size_t)image_width + (size_t)x] =
                            (float)(value > 1.0 ? 1.0 : value);
                    }
                }
            }
        }

        width = (int)image_width;
        height = (int)image_height;
        pixels = (unsigned char *)malloc(pixel_count * 4u);
        if (pixels == NULL) {
            free(samples);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        dng_demosaic(samples, width, height, pattern, pixels);
        free(samples);
    }

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    stride = width * 4;
    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride, 4, max_dimension,
            free, malloc, &source_width, &source_height)) {
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

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * components;
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
