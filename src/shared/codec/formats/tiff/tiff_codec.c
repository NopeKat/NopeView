// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * TIFF format codec.
 * Implements the format ABI (nv_format_abi.h) with a baseline reader: the
 * first page only, strips rather than tiles, uncompressed, LZW, PackBits or
 * Deflate data, one, three or four samples of 1, 8 or 16 bits, and the
 * horizontal predictor.
 * Decoding only.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <stdlib.h>
#include <string.h>

#include "miniz.h"

#define NV_TIFF_MAX_ENTRIES 512u
#define NV_TIFF_MAX_DIMENSION 32768u
#define NV_TIFF_LZW_MAX_CODES 4096u

typedef struct nv_tiff_file {
    const unsigned char *data;
    size_t size;
    int big_endian;
} nv_tiff_file;

typedef struct nv_tiff_entry {
    uint16_t tag;
    uint16_t type;
    uint32_t count;
    uint32_t value_field;
    uint32_t entry_offset;
} nv_tiff_entry;

typedef struct nv_tiff_page {
    uint32_t width;
    uint32_t height;
    uint16_t bits_per_sample;
    uint16_t compression;
    uint16_t photometric;
    uint16_t samples_per_pixel;
    uint16_t planar_configuration;
    uint16_t predictor;
    uint16_t fill_order;
    uint32_t rows_per_strip;
    const unsigned char *color_map;
    size_t color_map_size;
    nv_tiff_entry strip_offsets;
    nv_tiff_entry strip_byte_counts;
} nv_tiff_page;

static uint16_t nv_tiff_u16(const nv_tiff_file *file, uint32_t offset)
{
    if (file->big_endian) {
        return (uint16_t)(((uint32_t)file->data[offset] << 8) | file->data[offset + 1u]);
    }
    return (uint16_t)(((uint32_t)file->data[offset + 1u] << 8) | file->data[offset]);
}

static uint32_t nv_tiff_u32(const nv_tiff_file *file, uint32_t offset)
{
    if (file->big_endian) {
        return ((uint32_t)file->data[offset] << 24) | ((uint32_t)file->data[offset + 1u] << 16) |
            ((uint32_t)file->data[offset + 2u] << 8) | (uint32_t)file->data[offset + 3u];
    }
    return ((uint32_t)file->data[offset + 3u] << 24) | ((uint32_t)file->data[offset + 2u] << 16) |
        ((uint32_t)file->data[offset + 1u] << 8) | (uint32_t)file->data[offset];
}

/* Bytes one value of a TIFF type occupies; 0 means the type is not read here. */
static int nv_tiff_type_size(uint16_t type)
{
    switch (type) {
    case 1: /* BYTE */
    case 2: /* ASCII */
    case 6: /* SBYTE */
    case 7: /* UNDEFINED */
        return 1;
    case 3: /* SHORT */
    case 8: /* SSHORT */
        return 2;
    case 4: /* LONG */
    case 9: /* SLONG */
    case 11: /* FLOAT */
        return 4;
    case 5: /* RATIONAL */
    case 10: /* SRATIONAL */
    case 12: /* DOUBLE */
        return 8;
    default:
        return 0;
    }
}

/* Values of an entry live either in its four value bytes or further in the
 * file; the pointer is into the file either way. */
static const unsigned char *nv_tiff_values(const nv_tiff_file *file,
                                           const nv_tiff_entry *entry, size_t *out_size)
{
    int unit = nv_tiff_type_size(entry->type);
    size_t total;

    if (unit == 0) {
        return NULL;
    }
    total = (size_t)unit * (size_t)entry->count;
    if (total <= 4u) {
        *out_size = total;
        return file->data + entry->entry_offset + 8u;
    }
    if (entry->value_field > file->size || total > file->size - entry->value_field) {
        return NULL;
    }
    *out_size = total;
    return file->data + entry->value_field;
}

/* Reads value index of an integer entry. */
static int nv_tiff_uint(const nv_tiff_file *file, const nv_tiff_entry *entry,
                        uint32_t index, uint32_t *out)
{
    int unit = nv_tiff_type_size(entry->type);
    const unsigned char *values;
    size_t size;
    uint32_t position;

    if (unit != 1 && unit != 2 && unit != 4) {
        return 0;
    }
    if (index >= entry->count) {
        return 0;
    }
    values = nv_tiff_values(file, entry, &size);
    if (values == NULL) {
        return 0;
    }
    position = (uint32_t)(values - file->data) + index * (uint32_t)unit;
    if (position + (uint32_t)unit > file->size) {
        return 0;
    }
    if (unit == 1) {
        *out = file->data[position];
    }
    else if (unit == 2) {
        *out = nv_tiff_u16(file, position);
    }
    else {
        *out = nv_tiff_u32(file, position);
    }
    return 1;
}

/*
 * TIFF LZW grows the code width one code early. Codes are read most significant
 * bit first and the first two code slots after 256 are reserved.
 * Only the stream defined by TIFF 6.0 is handled; the legacy 4.25 bit LZW
 * variant that some old writers produced is not.
 */
static int nv_tiff_lzw_decode(const unsigned char *source, size_t source_size,
                              unsigned char *dest, size_t dest_size)
{
    uint16_t prefix[NV_TIFF_LZW_MAX_CODES];
    unsigned char suffix[NV_TIFF_LZW_MAX_CODES];
    unsigned char first[NV_TIFF_LZW_MAX_CODES];
    unsigned char expansion[NV_TIFF_LZW_MAX_CODES + 2u];
    size_t written = 0u;
    size_t bit_offset = 0u;
    size_t total_bits = source_size * 8u;
    int code_size = 9;
    int next_code = 258;
    int previous = -1;
    int literal;

    /* Codes 0 to 255 stand for themselves, so their entry is a single byte
     * equal to the code. Later entries store the byte a string ends with. */
    for (literal = 0; literal < 256; literal++) {
        suffix[literal] = (unsigned char)literal;
        first[literal] = (unsigned char)literal;
    }

    for (;;) {
        uint32_t code = 0u;
        size_t length;
        int bit;
        int current;

        if (bit_offset + (size_t)code_size > total_bits) {
            break;
        }
        for (bit = 0; bit < code_size; bit++) {
            size_t position = bit_offset + (size_t)bit;
            unsigned char byte = source[position >> 3];
            int shift = 7 - (int)(position & 7u);
            code = (code << 1) | ((byte >> shift) & 1u);
        }
        bit_offset += (size_t)code_size;

        if (code == 256u) {
            code_size = 9;
            next_code = 258;
            previous = -1;
            continue;
        }
        if (code == 257u) {
            break;
        }
        if (previous < 0) {
            if (code >= NV_TIFF_LZW_MAX_CODES || written >= dest_size) {
                return 0;
            }
            dest[written++] = (unsigned char)code;
            first[code] = (unsigned char)code;
            previous = (int)code;
            continue;
        }

        current = (int)code;
        length = 0u;
        if (current >= next_code) {
            /* The only code that can be missing from the table is the one being
             * defined right now, which is the previous string plus its first byte. */
            if (current != next_code) {
                return 0;
            }
            expansion[length++] = first[previous];
            current = previous;
        }

        while (current >= 0) {
            if (length >= NV_TIFF_LZW_MAX_CODES) {
                return 0;
            }
            expansion[length++] = suffix[current];
            if (current < 258) {
                break;
            }
            current = (int)prefix[current];
        }

        while (length > 0u) {
            if (written >= dest_size) {
                return 0;
            }
            dest[written++] = expansion[length - 1u];
            length--;
        }

        if (next_code < (int)NV_TIFF_LZW_MAX_CODES) {
            prefix[next_code] = (uint16_t)previous;
            suffix[next_code] = first[code < (uint32_t)next_code ? code : (uint32_t)previous];
            first[next_code] = first[previous];
            next_code++;
            if (code_size < 12 && next_code == (1 << code_size) - 1) {
                code_size++;
            }
        }
        previous = (int)code;
    }

    return written == dest_size;
}

static size_t nv_tiff_packbits_decode(const unsigned char *source, size_t source_size,
                                      unsigned char *dest, size_t dest_size)
{
    size_t read_offset = 0u;
    size_t written = 0u;

    while (read_offset < source_size && written < dest_size) {
        signed char control = (signed char)source[read_offset++];
        size_t count;

        if (control >= 0) {
            count = (size_t)control + 1u;
            if (read_offset + count > source_size) {
                break;
            }
            if (count > dest_size - written) {
                count = dest_size - written;
            }
            memcpy(dest + written, source + read_offset, count);
            read_offset += (size_t)control + 1u;
            written += count;
        }
        else if (control != -128) {
            unsigned char value;
            count = (size_t)(1 - (int)control);
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

    return written;
}

static int nv_tiff_decompress(const nv_tiff_page *page, const unsigned char *source,
                              size_t source_size, unsigned char *dest, size_t dest_size)
{
    switch (page->compression) {
    case 1u:
        if (source_size < dest_size) {
            return 0;
        }
        memcpy(dest, source, dest_size);
        return 1;
    case 5u:
        return nv_tiff_lzw_decode(source, source_size, dest, dest_size);
    case 32773u:
        return nv_tiff_packbits_decode(source, source_size, dest, dest_size) == dest_size;
    case 8u:
    case 32946u:
        /* miniz reports the inflated size, or a sentinel when the stream is
         * broken. */
        return tinfl_decompress_mem_to_mem(dest, dest_size, source, source_size,
                                           TINFL_FLAG_PARSE_ZLIB_HEADER) == dest_size;
    default:
        return 0;
    }
}

static void nv_tiff_unpack_row(const nv_tiff_file *file, const unsigned char *source,
                               uint16_t bits_per_sample, uint32_t samples, uint16_t *values)
{
    uint32_t index;

    if (bits_per_sample == 8u) {
        for (index = 0u; index < samples; index++) {
            values[index] = (uint16_t)source[index];
        }
    }
    else if (bits_per_sample == 16u) {
        for (index = 0u; index < samples; index++) {
            uint32_t offset = index * 2u;
            values[index] = file->big_endian
                ? (uint16_t)(((uint32_t)source[offset] << 8) | source[offset + 1u])
                : (uint16_t)(((uint32_t)source[offset + 1u] << 8) | source[offset]);
        }
    }
    else {
        /* One bit per sample, most significant bit first. */
        for (index = 0u; index < samples; index++) {
            values[index] = (uint16_t)((source[index >> 3] >> (7 - (index & 7u))) & 1u);
        }
    }
}

/* The horizontal predictor stores differences between neighbouring samples. */
static void nv_tiff_apply_predictor(uint16_t *values, uint32_t samples,
                                    uint32_t per_pixel, unsigned int mask)
{
    uint32_t index;

    for (index = per_pixel; index < samples; index++) {
        values[index] = (uint16_t)(((unsigned int)values[index] + values[index - per_pixel]) & mask);
    }
}

static void nv_tiff_palette_entry(const nv_tiff_file *file, const nv_tiff_page *page,
                                  uint32_t index, unsigned char *out)
{
    size_t entries = page->color_map_size / 6u;
    size_t offset;

    if (index >= entries) {
        out[0] = 0;
        out[1] = 0;
        out[2] = 0;
        return;
    }
    /* Three 16 bit channels follow each other: red, then green, then blue. */
    offset = (size_t)index * 2u;
    out[0] = (unsigned char)(nv_tiff_u16(file, (uint32_t)(page->color_map - file->data) + (uint32_t)offset) >> 8);
    out[1] = (unsigned char)(nv_tiff_u16(file, (uint32_t)(page->color_map - file->data) +
                                        (uint32_t)(page->color_map_size / 3u) + (uint32_t)offset) >> 8);
    out[2] = (unsigned char)(nv_tiff_u16(file, (uint32_t)(page->color_map - file->data) +
                                        (uint32_t)(page->color_map_size / 3u * 2u) + (uint32_t)offset) >> 8);
}

static unsigned char nv_tiff_scale(unsigned int value, unsigned int maximum)
{
    if (maximum == 0u) {
        return 0;
    }
    return (unsigned char)((value * 255u + maximum / 2u) / maximum);
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

    out_info->name = "TIFF";
    out_info->format_id = "tiff";
    out_info->extensions = "tif,tiff";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    nv_tiff_file file;
    nv_tiff_page page;
    nv_tiff_entry entry;
    uint32_t ifd_offset;
    uint32_t entry_count;
    uint32_t index;
    uint32_t strip_count;
    uint32_t row_bytes;
    uint32_t per_row_samples;
    uint32_t strip_index;
    unsigned int max_sample;
    unsigned int mask;
    unsigned char *canvas = NULL;
    unsigned char *row_buffer = NULL;
    uint16_t *values = NULL;
    unsigned char *pixels;
    int components;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    int width;
    int height;
    nv_format_status status = NV_FORMAT_OK;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (request->data == NULL || request->data_size < 8u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);
    memset(&page, 0, sizeof(page));
    page.planar_configuration = 1u;

    file.data = request->data;
    file.size = request->data_size;
    if (memcmp(request->data, "II", 2u) == 0) {
        file.big_endian = 0;
    }
    else if (memcmp(request->data, "MM", 2u) == 0) {
        file.big_endian = 1;
    }
    else {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (nv_tiff_u16(&file, 2u) != 42u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    ifd_offset = nv_tiff_u32(&file, 4u);
    if (ifd_offset > file.size || file.size - ifd_offset < 2u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    entry_count = nv_tiff_u16(&file, ifd_offset);
    if (entry_count == 0u || entry_count > NV_TIFF_MAX_ENTRIES ||
        (size_t)ifd_offset + 2u + (size_t)entry_count * 12u > file.size) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    for (index = 0u; index < entry_count; index++) {
        uint32_t offset = ifd_offset + 2u + index * 12u;
        size_t values_size = 0u;

        entry.tag = nv_tiff_u16(&file, offset);
        entry.type = nv_tiff_u16(&file, offset + 2u);
        entry.count = nv_tiff_u32(&file, offset + 4u);
        entry.value_field = nv_tiff_u32(&file, offset + 8u);
        entry.entry_offset = offset;

        switch (entry.tag) {
        case 256u:
            nv_tiff_uint(&file, &entry, 0u, &page.width);
            break;
        case 257u:
            nv_tiff_uint(&file, &entry, 0u, &page.height);
            break;
        case 258u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.bits_per_sample = (uint16_t)value;
            }
            break;
        }
        case 259u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.compression = (uint16_t)value;
            }
            break;
        }
        case 262u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.photometric = (uint16_t)value;
            }
            break;
        }
        case 277u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.samples_per_pixel = (uint16_t)value;
            }
            break;
        }
        case 278u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.rows_per_strip = value;
            }
            break;
        }
        case 284u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.planar_configuration = (uint16_t)value;
            }
            break;
        }
        case 317u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value)) {
                page.predictor = (uint16_t)value;
            }
            break;
        }
        case 320u:
            page.color_map = nv_tiff_values(&file, &entry, &values_size);
            page.color_map_size = values_size;
            break;
        case 273u:
            page.strip_offsets = entry;
            break;
        case 279u:
            page.strip_byte_counts = entry;
            break;
        case 266u: {
            uint32_t value = 0u;
            if (nv_tiff_uint(&file, &entry, 0u, &value) && value > 1u) {
                return NV_FORMAT_ERR_UNSUPPORTED;
            }
            break;
        }
        case 322u:
            /* Tiled files are not part of the baseline reader. */
            return NV_FORMAT_ERR_UNSUPPORTED;
        default:
            break;
        }
    }

    if (page.width == 0u || page.height == 0u ||
        page.width > NV_TIFF_MAX_DIMENSION || page.height > NV_TIFF_MAX_DIMENSION ||
        page.samples_per_pixel == 0u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (page.samples_per_pixel > 4u || page.planar_configuration > 1u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (page.bits_per_sample != 1u && page.bits_per_sample != 8u && page.bits_per_sample != 16u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (page.photometric != 0u && page.photometric != 1u && page.photometric != 2u &&
        page.photometric != 3u && page.photometric != 5u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (page.photometric == 2u && page.samples_per_pixel < 3u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }
    if (page.photometric == 3u && (page.color_map == NULL || page.color_map_size < 6u)) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (page.strip_offsets.count == 0u || page.strip_byte_counts.count == 0u) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    strip_count = page.strip_offsets.count < page.strip_byte_counts.count
        ? page.strip_offsets.count
        : page.strip_byte_counts.count;
    if (page.rows_per_strip == 0u || page.rows_per_strip > page.height) {
        page.rows_per_strip = page.height;
    }

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    max_sample = (1u << page.bits_per_sample) - 1u;
    mask = max_sample;
    per_row_samples = page.width * (uint32_t)page.samples_per_pixel;
    row_bytes = (per_row_samples * (uint32_t)page.bits_per_sample + 7u) / 8u;

    canvas = (unsigned char *)malloc((size_t)page.width * (size_t)page.height * 4u);
    if (canvas == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memset(canvas, 0, (size_t)page.width * (size_t)page.height * 4u);

    values = (uint16_t *)malloc((size_t)per_row_samples * sizeof(uint16_t));
    row_buffer = (unsigned char *)malloc((size_t)row_bytes * (size_t)page.rows_per_strip);
    if (values == NULL || row_buffer == NULL) {
        free(values);
        free(row_buffer);
        free(canvas);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    for (strip_index = 0u; strip_index < strip_count; strip_index++) {
        uint32_t strip_offset = 0u;
        uint32_t strip_size = 0u;
        uint32_t first_row = strip_index * page.rows_per_strip;
        uint32_t rows;
        uint32_t row;

        if (first_row >= page.height) {
            break;
        }
        if (!nv_tiff_uint(&file, &page.strip_offsets, strip_index, &strip_offset) ||
            !nv_tiff_uint(&file, &page.strip_byte_counts, strip_index, &strip_size)) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }
        rows = page.height - first_row;
        if (rows > page.rows_per_strip) {
            rows = page.rows_per_strip;
        }
        if (strip_offset > file.size || strip_size > file.size - strip_offset) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }
        if (!nv_tiff_decompress(&page, file.data + strip_offset, strip_size,
                                row_buffer, (size_t)rows * row_bytes)) {
            status = NV_FORMAT_ERR_DECODE_FAILED;
            goto cleanup;
        }

        for (row = 0u; row < rows; row++) {
            const unsigned char *line = row_buffer + (size_t)row * row_bytes;
            unsigned char *target = canvas +
                ((size_t)(first_row + row) * page.width) * 4u;
            uint32_t column;

            nv_tiff_unpack_row(&file, line, page.bits_per_sample, per_row_samples, values);
            if (page.predictor == 2u) {
                nv_tiff_apply_predictor(values, per_row_samples,
                                        (uint32_t)page.samples_per_pixel, mask);
            }

            for (column = 0u; column < page.width; column++) {
                const uint16_t *pixel_values = values + (size_t)column * page.samples_per_pixel;
                unsigned char *out = target + (size_t)column * 4u;
                unsigned char red;
                unsigned char green;
                unsigned char blue;
                unsigned char alpha = 255;

                if (page.photometric == 3u) {
                    unsigned char palette[3];
                    nv_tiff_palette_entry(&file, &page, pixel_values[0], palette);
                    red = palette[0];
                    green = palette[1];
                    blue = palette[2];
                }
                else if (page.samples_per_pixel >= 3u) {
                    if (page.photometric == 5u && page.samples_per_pixel >= 4u) {
                        /* CMYK is converted by removing the black plate. */
                        unsigned int cyan = pixel_values[0];
                        unsigned int magenta = pixel_values[1];
                        unsigned int yellow = pixel_values[2];
                        unsigned int black = pixel_values[3];
                        red = nv_tiff_scale((max_sample - cyan) * (max_sample - black) / max_sample, max_sample);
                        green = nv_tiff_scale((max_sample - magenta) * (max_sample - black) / max_sample, max_sample);
                        blue = nv_tiff_scale((max_sample - yellow) * (max_sample - black) / max_sample, max_sample);
                        alpha = 255;
                    }
                    else {
                        red = nv_tiff_scale(pixel_values[0], max_sample);
                        green = nv_tiff_scale(pixel_values[1], max_sample);
                        blue = nv_tiff_scale(pixel_values[2], max_sample);
                        if (page.samples_per_pixel == 4u) {
                            alpha = nv_tiff_scale(pixel_values[3], max_sample);
                        }
                    }
                }
                else {
                    unsigned int gray = pixel_values[0];
                    if (page.photometric == 0u) {
                        gray = max_sample - gray;
                    }
                    red = nv_tiff_scale(gray, max_sample);
                    green = red;
                    blue = red;
                    if (page.samples_per_pixel == 2u) {
                        alpha = nv_tiff_scale(pixel_values[1], max_sample);
                    }
                }

                out[0] = red;
                out[1] = green;
                out[2] = blue;
                out[3] = alpha;
            }
        }
    }

    free(values);
    values = NULL;
    free(row_buffer);
    row_buffer = NULL;

    pixels = canvas;
    canvas = NULL;
    width = (int)page.width;
    height = (int)page.height;
    stride = (int)page.width * 4;
    pixel_count = (size_t)width * (size_t)height;

    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride, 4, max_dimension,
            free, malloc, &source_width, &source_height)) {
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

    out_image->width = (int32_t)width;
    out_image->height = (int32_t)height;
    out_image->stride = (int32_t)width * components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * (size_t)components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = 1u;
    out_image->frame_delay_ms = 0u;

cleanup:
    free(values);
    free(row_buffer);
    free(canvas);
    return status;
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
