/*
 * Windows icon format codec.
 * Implements the format ABI (nv_format_abi.h) for .ico files: a container of
 * images, one per size, that Windows uses for application icons.
 *
 * Each entry is either a PNG-compressed image or a device independent bitmap.
 * Sizes are exposed as frames, so frame_index selects one of them, the same
 * way an animated format selects a frame. Decoding only: writing a valid
 * multi-size icon is a separate concern and nothing in the app needs it.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#define STBI_NO_STDIO
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <stdint.h>

/* The container headers are little endian and packed exactly as stored. */
#pragma pack(push, 1)
typedef struct ico_header {
    uint16_t reserved;
    uint16_t type;
    uint16_t count;
} ico_header;

typedef struct ico_entry {
    uint8_t width;
    uint8_t height;
    uint8_t color_count;
    uint8_t reserved;
    uint16_t planes;
    uint16_t bit_count;
    uint32_t bytes_in_resource;
    uint32_t image_offset;
} ico_entry;
#pragma pack(pop)

#define ICO_TYPE_ICON 1u
#define ICO_MAX_ENTRIES 256u
#define ICO_HEADER_SIZE 6u
#define ICO_ENTRY_SIZE 16u
#define ICO_EDGE_LIMIT 1024

#define DIB_HEADER_MIN_SIZE 40u
#define DIB_BI_RGB 0u
#define DIB_BI_BITFIELDS 3u

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

    out_info->name = "Windows icon";
    out_info->format_id = "ico";
    out_info->extensions = "ico";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

/* An entry reports 0 for 256 pixels, which is the format's own limit. */
static int entry_edge(uint8_t stored)
{
    return stored == 0u ? 256 : (int)stored;
}

static int read_i32(const unsigned char *bytes, int32_t *out_value)
{
    int32_t value;

    memcpy(&value, bytes, sizeof(value));
    *out_value = value;
    return 0;
}

static int read_u16(const unsigned char *bytes, uint16_t *out_value)
{
    uint16_t value;

    memcpy(&value, bytes, sizeof(value));
    *out_value = value;
    return 0;
}

static int read_u32(const unsigned char *bytes, uint32_t *out_value)
{
    uint32_t value;

    memcpy(&value, bytes, sizeof(value));
    *out_value = value;
    return 0;
}

/*
 * Writes one RGBA pixel from a device independent bitmap row. Returns 0 when
 * the pixel cannot be described, which marks the frame as undecodable.
 */
static int dib_pixel_rgba(
    const unsigned char *row_bytes,
    const unsigned char *palette,
    size_t palette_entries,
    int bit_count,
    int column,
    unsigned char *out_rgba,
    int *out_has_alpha)
{
    unsigned int red = 0u;
    unsigned int green = 0u;
    unsigned int blue = 0u;
    unsigned int alpha = 255u;

    if (bit_count == 32) {
        const unsigned char *pixel = row_bytes + (size_t)column * 4u;
        blue = pixel[0];
        green = pixel[1];
        red = pixel[2];
        alpha = pixel[3];
        if (alpha != 0u) {
            *out_has_alpha = 1;
        }
    }
    else if (bit_count == 24) {
        const unsigned char *pixel = row_bytes + (size_t)column * 3u;
        blue = pixel[0];
        green = pixel[1];
        red = pixel[2];
    }
    else if (bit_count == 8) {
        size_t index = row_bytes[column];
        if (index >= palette_entries) {
            return 0;
        }
        blue = palette[index * 4u];
        green = palette[index * 4u + 1u];
        red = palette[index * 4u + 2u];
    }
    else {
        size_t bit_index = (size_t)column * (size_t)bit_count;
        size_t byte_index = bit_index / 8u;
        unsigned int shift =
            8u - (unsigned int)bit_count - (unsigned int)(bit_index % 8u);
        unsigned int value =
            (row_bytes[byte_index] >> shift) & ((1u << bit_count) - 1u);

        if (value >= palette_entries) {
            return 0;
        }
        blue = palette[(size_t)value * 4u];
        green = palette[(size_t)value * 4u + 1u];
        red = palette[(size_t)value * 4u + 2u];
    }

    out_rgba[0] = (unsigned char)red;
    out_rgba[1] = (unsigned char)green;
    out_rgba[2] = (unsigned char)blue;
    out_rgba[3] = (unsigned char)alpha;
    return 1;
}

static void dib_apply_mask(
    const unsigned char *data,
    uint32_t mask_offset,
    size_t mask_stride,
    int width,
    int height,
    int top_down,
    unsigned char *pixels)
{
    int row;

    for (row = 0; row < height; row++) {
        int mask_row = top_down ? row : height - 1 - row;
        const unsigned char *bits = data + mask_offset + (size_t)mask_row * mask_stride;
        int column;

        for (column = 0; column < width; column++) {
            if ((bits[column / 8] >> (7 - (column % 8))) & 1) {
                pixels[((size_t)row * (size_t)width + (size_t)column) * 4u + 3u] = 0u;
            }
        }
    }
}

static nv_format_status decode_dib(
    const unsigned char *data,
    size_t data_size,
    const ico_entry *entry,
    unsigned char **out_pixels,
    int *out_width,
    int *out_height)
{
    uint32_t header_size = 0u;
    int32_t width = 0;
    int32_t stored_height = 0;
    int32_t height;
    uint16_t bit_count = 0u;
    uint32_t compression = 0u;
    uint32_t colors_used = 0u;
    const unsigned char *palette;
    size_t palette_entries;
    size_t pixel_offset;
    size_t xor_stride;
    size_t xor_size;
    size_t mask_stride;
    uint32_t mask_offset;
    int has_mask;
    int top_down;
    int has_alpha = 0;
    unsigned char *pixels;
    int row;

    if (data_size < DIB_HEADER_MIN_SIZE) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    read_u32(data, &header_size);
    if (header_size < DIB_HEADER_MIN_SIZE || header_size > data_size) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    read_i32(data + 4, &width);
    read_i32(data + 8, &stored_height);
    read_u16(data + 12, &bit_count);
    read_u32(data + 16, &compression);
    read_u32(data + 32, &colors_used);

    /* The stored height covers the color image and the one bit mask, stacked
     * one above the other; a negative height means the rows are top-down. */
    top_down = stored_height < 0;
    height = (top_down ? -stored_height : stored_height) / 2;
    if (width <= 0 || width > ICO_EDGE_LIMIT || height <= 0 ||
        height > ICO_EDGE_LIMIT || (stored_height % 2) != 0) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (entry_edge(entry->width) != (int)width ||
        entry_edge(entry->height) != height) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (bit_count != 1u && bit_count != 4u && bit_count != 8u &&
        bit_count != 24u && bit_count != 32u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (bit_count <= 8u) {
        if (compression != DIB_BI_RGB) {
            return NV_FORMAT_ERR_UNSUPPORTED;
        }
    }
    else if (compression != DIB_BI_RGB && compression != DIB_BI_BITFIELDS) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    palette_entries = bit_count <= 8u ? ((size_t)1u << bit_count) : 0u;
    if (colors_used != 0u && (size_t)colors_used < palette_entries) {
        palette_entries = (size_t)colors_used;
    }
    palette = data + header_size;
    pixel_offset = (size_t)header_size + palette_entries * 4u;
    if (compression == DIB_BI_BITFIELDS) {
        pixel_offset += 12u;
    }

    xor_stride = (((size_t)width * bit_count + 31u) / 32u) * 4u;
    if (xor_stride > SIZE_MAX / (size_t)height) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    xor_size = xor_stride * (size_t)height;
    if (pixel_offset > data_size || xor_size > data_size - pixel_offset) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    mask_stride = (((size_t)width + 31u) / 32u) * 4u;
    mask_offset = (uint32_t)(pixel_offset + xor_size);
    has_mask = (size_t)mask_offset + mask_stride * (size_t)height <= data_size;

    pixels = (unsigned char *)malloc((size_t)width * (size_t)height * 4u);
    if (pixels == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    for (row = 0; row < height; row++) {
        int column;
        int source_row = top_down ? row : height - 1 - row;
        const unsigned char *row_bytes =
            data + pixel_offset + (size_t)source_row * xor_stride;

        for (column = 0; column < width; column++) {
            unsigned char *target =
                pixels + ((size_t)row * (size_t)width + (size_t)column) * 4u;
            if (!dib_pixel_rgba(
                    row_bytes, palette, palette_entries, (int)bit_count,
                    column, target, &has_alpha)) {
                free(pixels);
                return NV_FORMAT_ERR_DECODE_FAILED;
            }
        }
    }

    if (has_mask) {
        dib_apply_mask(data, mask_offset, mask_stride, (int)width, height, top_down, pixels);
    }

    /* Windows treats a fully zero alpha channel as "no alpha" and the mask
     * alone decides transparency; common tools keep both in sync, so the mask
     * above already covers that case. */
    (void)has_alpha;

    *out_pixels = pixels;
    *out_width = (int)width;
    *out_height = height;
    return NV_FORMAT_OK;
}

static nv_format_status decode_png_frame(
    const unsigned char *data,
    size_t data_size,
    unsigned char **out_pixels,
    int *out_width,
    int *out_height)
{
    int width = 0;
    int height = 0;
    int channels = 0;
    unsigned char *pixels;

    if (data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    pixels = stbi_load_from_memory(
        data, (int)data_size, &width, &height, &channels, 4);
    if (pixels == NULL || width <= 0 || height <= 0) {
        stbi_image_free(pixels);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    *out_pixels = pixels;
    *out_width = width;
    *out_height = height;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    const unsigned char *data;
    size_t data_size;
    uint16_t reserved = 0u;
    uint16_t type = 0u;
    uint16_t count = 0u;
    ico_entry entry;
    uint32_t frame_index;
    uint32_t payload_size = 0u;
    uint32_t payload_offset = 0u;
    nv_format_pixel_format output_format;
    unsigned char *source = NULL;
    int source_width = 0;
    int source_height = 0;
    int width;
    int height;
    int32_t max_dimension;
    unsigned char *pixels;
    int components;
    nv_format_status status;

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

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    data = request->data;
    data_size = request->data_size;
    if (data_size < ICO_HEADER_SIZE) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    read_u16(data, &reserved);
    read_u16(data + 2, &type);
    read_u16(data + 4, &count);
    if (reserved != 0u || type != ICO_TYPE_ICON || count == 0u ||
        count > ICO_MAX_ENTRIES) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (ICO_HEADER_SIZE + (size_t)count * ICO_ENTRY_SIZE > data_size) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    frame_index = request->frame_index;
    if (frame_index >= (uint32_t)count) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    {
        const unsigned char *entry_bytes =
            data + ICO_HEADER_SIZE + (size_t)frame_index * ICO_ENTRY_SIZE;

        memcpy(&entry.width, entry_bytes, sizeof(entry.width));
        memcpy(&entry.height, entry_bytes + 1, sizeof(entry.height));
        read_u16(entry_bytes + 4, &entry.planes);
        read_u16(entry_bytes + 6, &entry.bit_count);
        read_u32(entry_bytes + 8, &payload_size);
        read_u32(entry_bytes + 12, &payload_offset);
    }

    if (payload_size == 0u || payload_offset > data_size ||
        (size_t)payload_size > data_size - (size_t)payload_offset) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    if (payload_size >= 8u &&
        memcmp(data + payload_offset, "\x89PNG\r\n\x1a\n", 8) == 0) {
        status = decode_png_frame(
            data + payload_offset, (size_t)payload_size, &source,
            &source_width, &source_height);
    }
    else {
        status = decode_dib(
            data + payload_offset, (size_t)payload_size, &entry, &source,
            &source_width, &source_height);
    }
    if (status != NV_FORMAT_OK) {
        return status;
    }

    width = source_width;
    height = source_height;

    /* A preview asks for a smaller long edge; the full frame is released. */
    max_dimension = nv_request_max_dimension(request);
    if (max_dimension > 0 && (width > max_dimension || height > max_dimension)) {
        int scaled_width = 0;
        int scaled_height = 0;
        int scaled_stride = 0;
        unsigned char *scaled = nv_downscale_pixels(
            source, width, height, width * 4, 4, max_dimension,
            &scaled_width, &scaled_height, &scaled_stride, malloc);

        if (scaled == NULL) {
            free(source);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        free(source);
        source = scaled;
        width = scaled_width;
        height = scaled_height;
    }

    output_format = (nv_format_pixel_format)nv_output_format(request->desired_pixel_format);
    components = nv_components_for(output_format);
    if (output_format == NV_FORMAT_PIXEL_RGBA8) {
        pixels = source;
    }
    else {
        size_t pixel_count = (size_t)width * (size_t)height;
        unsigned char *converted = (unsigned char *)
            malloc(pixel_count * (size_t)components);

        if (converted == NULL) {
            free(source);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        nv_rgba_to_format(converted, source, pixel_count, output_format);
        free(source);
        pixels = converted;
    }

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * components;
    out_image->pixel_format = output_format;
    out_image->pixels = pixels;
    out_image->pixels_size =
        (size_t)width * (size_t)height * (size_t)components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = count;
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
    image->pixels_size = 0u;
    image->width = 0;
    image->height = 0;
    image->stride = 0;
}
