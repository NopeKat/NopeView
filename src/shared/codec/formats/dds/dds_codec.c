// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * DDS format codec.
 * Reads Microsoft DirectDraw Surface containers and decompresses the first
 * surface with bcdec: BC1, BC2, BC3, BC4, BC5, BC6H and BC7 blocks, plus plain
 * 24 and 32 bit surfaces and 8 bit luminance surfaces.
 * Mip maps, cube faces and array slices after the first one are ignored.
 * Decoding only.
 */

#define BCDEC_IMPLEMENTATION
#include "bcdec.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#define DDS_MAGIC 0x20534444u /* "DDS " */
#define DDS_PIXELFORMAT_RGB 0x00000040u
#define DDS_PIXELFORMAT_LUMINANCE 0x00020000u
#define DDS_PIXELFORMAT_FOURCC 0x00000004u

typedef struct dds_header {
    uint32_t size;
    uint32_t flags;
    uint32_t height;
    uint32_t width;
    uint32_t pitch_or_linear_size;
    uint32_t depth;
    uint32_t mip_map_count;
    uint32_t reserved[11];
    uint32_t pixel_format_size;
    uint32_t pixel_format_flags;
    uint32_t four_cc;
    uint32_t rgb_bit_count;
    uint32_t red_mask;
    uint32_t green_mask;
    uint32_t blue_mask;
    uint32_t alpha_mask;
    uint32_t caps;
    uint32_t caps2;
    uint32_t caps3;
    uint32_t caps4;
    uint32_t reserved2;
} dds_header;

/* One decoded block, at most four pixels wide and four rows tall. */
typedef struct dds_block {
    int width;
    int height;
    unsigned char rgba[4 * 4 * 4];
} dds_block;

static uint32_t dds_read_u32(const unsigned char *data, size_t offset)
{
    return (uint32_t)data[offset] |
           ((uint32_t)data[offset + 1u] << 8) |
           ((uint32_t)data[offset + 2u] << 16) |
           ((uint32_t)data[offset + 3u] << 24);
}

static void dds_parse_header(const unsigned char *data, dds_header *header)
{
    header->size = dds_read_u32(data, 4u);
    header->flags = dds_read_u32(data, 8u);
    header->height = dds_read_u32(data, 12u);
    header->width = dds_read_u32(data, 16u);
    header->pitch_or_linear_size = dds_read_u32(data, 20u);
    header->depth = dds_read_u32(data, 24u);
    header->mip_map_count = dds_read_u32(data, 28u);
    header->pixel_format_size = dds_read_u32(data, 76u);
    header->pixel_format_flags = dds_read_u32(data, 80u);
    header->four_cc = dds_read_u32(data, 84u);
    header->rgb_bit_count = dds_read_u32(data, 88u);
    header->red_mask = dds_read_u32(data, 92u);
    header->green_mask = dds_read_u32(data, 96u);
    header->blue_mask = dds_read_u32(data, 100u);
    header->alpha_mask = dds_read_u32(data, 104u);
    header->caps = dds_read_u32(data, 108u);
    header->caps2 = dds_read_u32(data, 112u);
}

/* Returns the byte shift and width of a colour mask; width 0 means unused. */
static void dds_mask_info(uint32_t mask, unsigned int *shift, unsigned int *bits)
{
    unsigned int index = 0u;
    unsigned int count = 0u;
    uint32_t value;

    if (mask == 0u) {
        *shift = 0u;
        *bits = 0u;
        return;
    }
    for (value = mask; (value & 1u) == 0u; value >>= 1) {
        index++;
    }
    for (value = mask >> index; (value & 1u) != 0u; value >>= 1) {
        count++;
    }
    *shift = index;
    *bits = count;
}

static unsigned char dds_scale(unsigned int value, unsigned int bits)
{
    unsigned int maximum;

    if (bits == 0u) {
        return 255;
    }
    maximum = (1u << bits) - 1u;
    if (value > maximum) {
        value = maximum;
    }
    return (unsigned char)((value * 255u + maximum / 2u) / maximum);
}

static unsigned char dds_to_byte(float value)
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

/* Chooses a block decoder from the four character code or the DX10 format. */
static int dds_block_kind(const dds_header *header, uint32_t dxgi_format)
{
    if (header->pixel_format_flags & DDS_PIXELFORMAT_FOURCC) {
        switch (header->four_cc) {
        case 0x31545844u: /* DXT1 */
            return 1;
        case 0x32545844u: /* DXT2 */
        case 0x33545844u: /* DXT3 */
            return 2;
        case 0x34545844u: /* DXT4 */
        case 0x35545844u: /* DXT5 */
            return 3;
        case 0x31495441u: /* ATI1 */
        case 0x55344342u: /* BC4U */
        case 0x53344342u: /* BC4S */
            return 4;
        case 0x32495441u: /* ATI2 */
        case 0x55354342u: /* BC5U */
        case 0x53354342u: /* BC5S */
            return 5;
        case 0x30315844u: /* DX10 */
            return dds_block_kind(header, dxgi_format);
        default:
            return 0;
        }
    }

    switch (dxgi_format) {
    case 71u:
    case 72u:
        return 1;
    case 74u:
    case 75u:
        return 2;
    case 77u:
    case 78u:
        return 3;
    case 80u:
    case 81u:
        return 4;
    case 83u:
    case 84u:
        return 5;
    case 95u:
    case 96u:
        return 6;
    case 98u:
    case 99u:
        return 7;
    default:
        return 0;
    }
}

static void dds_decode_block(int kind, const unsigned char *source, dds_block *block)
{
    unsigned char rgba[4 * 4 * 4];
    unsigned char byte_pair[4 * 4 * 2];
    float floats[4 * 4 * 3];
    int index;

    block->width = 4;
    block->height = 4;
    memset(rgba, 0, sizeof(rgba));

    switch (kind) {
    case 1:
        bcdec_bc1(source, rgba, 4 * 4);
        break;
    case 2:
        bcdec_bc2(source, rgba, 4 * 4);
        break;
    case 3:
        bcdec_bc3(source, rgba, 4 * 4);
        break;
    case 4:
        /* One channel, shown as a grey image. */
        bcdec_bc4(source, byte_pair, 4);
        for (index = 0; index < 16; index++) {
            rgba[index * 4] = byte_pair[index];
            rgba[index * 4 + 1] = byte_pair[index];
            rgba[index * 4 + 2] = byte_pair[index];
            rgba[index * 4 + 3] = 255;
        }
        break;
    case 5:
        /* Two channels, shown as red and green like a normal map. */
        bcdec_bc5(source, byte_pair, 4 * 2);
        for (index = 0; index < 16; index++) {
            rgba[index * 4] = byte_pair[index * 2];
            rgba[index * 4 + 1] = byte_pair[index * 2 + 1];
            rgba[index * 4 + 2] = 128;
            rgba[index * 4 + 3] = 255;
        }
        break;
    case 6:
        /* BC6H stores half floats; the float variant hands over RGB triples. */
        bcdec_bc6h_float(source, floats, 4 * 4 * 3, 0);
        for (index = 0; index < 16; index++) {
            rgba[index * 4] = dds_to_byte(floats[index * 3]);
            rgba[index * 4 + 1] = dds_to_byte(floats[index * 3 + 1]);
            rgba[index * 4 + 2] = dds_to_byte(floats[index * 3 + 2]);
            rgba[index * 4 + 3] = 255;
        }
        break;
    case 7:
        bcdec_bc7(source, rgba, 4 * 4);
        break;
    default:
        block->width = 0;
        block->height = 0;
        return;
    }

    memcpy(block->rgba, rgba, sizeof(block->rgba));
}


/* Plain surfaces are copied pixel by pixel through the channel masks. */
static int dds_decode_plain(
    const dds_header *header,
    const unsigned char *source,
    size_t source_size,
    int width,
    int height,
    unsigned char *canvas)
{
    unsigned int red_shift;
    unsigned int red_bits;
    unsigned int green_shift;
    unsigned int green_bits;
    unsigned int blue_shift;
    unsigned int blue_bits;
    unsigned int alpha_shift;
    unsigned int alpha_bits;
    unsigned int bytes_per_pixel;
    int luminance;
    int row;
    size_t stride;

    bytes_per_pixel = header->rgb_bit_count / 8u;
    if (bytes_per_pixel != 1u && bytes_per_pixel != 3u && bytes_per_pixel != 4u) {
        return 0;
    }
    stride = (size_t)width * bytes_per_pixel;
    if (source_size < stride * (size_t)height) {
        return 0;
    }

    dds_mask_info(header->red_mask, &red_shift, &red_bits);
    dds_mask_info(header->green_mask, &green_shift, &green_bits);
    dds_mask_info(header->blue_mask, &blue_shift, &blue_bits);
    dds_mask_info(header->alpha_mask, &alpha_shift, &alpha_bits);
    luminance = (header->red_mask == 0u && header->green_mask == 0u &&
                 header->blue_mask == 0u) ||
                (header->pixel_format_flags & DDS_PIXELFORMAT_LUMINANCE) != 0u;

    for (row = 0; row < height; row++) {
        const unsigned char *line = source + (size_t)row * stride;
        unsigned char *target = canvas + (size_t)row * (size_t)width * 4u;
        int column;

        for (column = 0; column < width; column++) {
            const unsigned char *pixel = line + (size_t)column * bytes_per_pixel;
            uint32_t value = 0u;
            unsigned char *out = target + (size_t)column * 4u;
            int index;

            for (index = 0; index < (int)bytes_per_pixel; index++) {
                value |= (uint32_t)pixel[index] << (8 * index);
            }

            if (luminance) {
                unsigned char grey = dds_scale(value, (unsigned int)(bytes_per_pixel * 8u));
                out[0] = grey;
                out[1] = grey;
                out[2] = grey;
                out[3] = 255;
            }
            else {
                out[0] = dds_scale((value >> red_shift) & ((1u << red_bits) - 1u), red_bits);
                out[1] = dds_scale((value >> green_shift) & ((1u << green_bits) - 1u), green_bits);
                out[2] = dds_scale((value >> blue_shift) & ((1u << blue_bits) - 1u), blue_bits);
                out[3] = alpha_bits == 0u
                    ? 255
                    : dds_scale((value >> alpha_shift) & ((1u << alpha_bits) - 1u), alpha_bits);
            }
        }
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

    out_info->name = "DirectDraw Surface";
    out_info->format_id = "dds";
    out_info->extensions = "dds";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    dds_header header;
    const unsigned char *source;
    size_t source_size;
    uint32_t dxgi_format = 0u;
    size_t data_offset;
    int kind;
    int width;
    int height;

    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    int components;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    unsigned char *canvas;
    unsigned char *pixels;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* One image per file, so only frame 0 is valid. */
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (request->data == NULL || request->data_size == 0u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES || request->data_size < 128u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (dds_read_u32(request->data, 0u) != DDS_MAGIC) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    dds_parse_header(request->data, &header);
    if (header.size != 124u || header.pixel_format_size != 32u ||
        header.width == 0u || header.height == 0u ||
        header.width > 32768u || header.height > 32768u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    /* A DX10 header follows the classic one and carries the real format, so
     * the surface data starts 20 bytes later. */
    data_offset = 128u;
    if (header.four_cc == 0x30315844u) {
        if (request->data_size < 148u) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        dxgi_format = dds_read_u32(request->data, 128u);
        data_offset = 148u;
    }

    kind = dds_block_kind(&header, dxgi_format);
    source = request->data + data_offset;
    source_size = request->data_size - data_offset;

    width = (int)header.width;
    height = (int)header.height;
    pixel_count = (size_t)width * (size_t)height;
    canvas = (unsigned char *)malloc(pixel_count * 4u);
    if (canvas == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memset(canvas, 0, pixel_count * 4u);

    if (kind == 0) {
        if (!dds_decode_plain(&header, source, source_size, width, height, canvas)) {
            free(canvas);
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
    }
    else {
        int block_bytes = (kind == 1 || kind == 4) ? 8 : 16;
        int blocks_x = (width + 3) / 4;
        int blocks_y = (height + 3) / 4;
        size_t needed = (size_t)blocks_x * (size_t)blocks_y * (size_t)block_bytes;
        int block_x;
        int block_y;

        if (source_size < needed) {
            free(canvas);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }

        for (block_y = 0; block_y < blocks_y; block_y++) {
            for (block_x = 0; block_x < blocks_x; block_x++) {
                dds_block block;
                size_t block_index = (size_t)block_y * (size_t)blocks_x + (size_t)block_x;
                int x;
                int y;

                dds_decode_block(kind, source + block_index * (size_t)block_bytes, &block);
                if (block.width == 0) {
                    free(canvas);
                    return NV_FORMAT_ERR_DECODE_FAILED;
                }

                for (y = 0; y < block.height; y++) {
                    int target_y = block_y * 4 + y;
                    if (target_y >= height) {
                        break;
                    }
                    for (x = 0; x < block.width; x++) {
                        int target_x = block_x * 4 + x;
                        if (target_x >= width) {
                            break;
                        }
                        memcpy(
                            canvas + ((size_t)target_y * (size_t)width + (size_t)target_x) * 4u,
                            block.rgba + ((size_t)y * (size_t)block.width + (size_t)x) * 4u,
                            4u);
                    }
                }
            }
        }
    }

    pixels = canvas;
    canvas = NULL;
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
