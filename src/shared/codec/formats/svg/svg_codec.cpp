// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * SVG format codec.
 * Implements the format ABI (nv_format_abi.h) on top of nanosvg, which parses
 * the document and rasterizes it. SVG has no fixed pixel size, so the raster
 * size comes from the document bounds, scaled down when a preview is asked for.
 * Decoding is supported; encoding is not.
 */

#define NANOSVG_IMPLEMENTATION
#define NANOSVGRAST_IMPLEMENTATION

#include "nanosvg.h"
#include "nanosvgrast.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/* miniz is compiled as C, so only the entry point needed here is declared. */
extern "C" size_t tinfl_decompress_mem_to_mem(
    void* out, size_t out_size, const void* source, size_t source_size, int flags);

#define SVG_INFLATE_FAILED ((size_t)(-1))

/*
 * Skips the gzip header and inflates the deflate stream that follows. The
 * allocator version of the inflate entry point hands back nothing with this
 * vendored miniz build, so the buffer is sized from the uncompressed length in
 * the trailer and a stream that disagrees with it is reported as a failure.
 */
static unsigned char* svg_gunzip(const unsigned char* data, size_t size, size_t* out_size)
{
    size_t header = 10u;
    size_t expected;
    unsigned int flags;
    unsigned char* output;
    size_t got;

    *out_size = 0u;
    if (size < 18u || data[0] != 0x1fu || data[1] != 0x8bu || data[2] != 0x08u) {
        return nullptr;
    }
    flags = data[3];
    if ((flags & 0xe0u) != 0u) {
        return nullptr;
    }
    if ((flags & 0x04u) != 0u) {
        unsigned int extra;
        if (header + 2u > size) {
            return nullptr;
        }
        extra = (unsigned int)data[10] | ((unsigned int)data[11] << 8);
        header += 2u + (size_t)extra;
    }
    if ((flags & 0x08u) != 0u) {
        while (header < size && data[header] != 0u) {
            header++;
        }
        header++;
    }
    if ((flags & 0x10u) != 0u) {
        while (header < size && data[header] != 0u) {
            header++;
        }
        header++;
    }
    if ((flags & 0x02u) != 0u) {
        header += 2u;
    }
    if (header + 8u >= size) {
        return nullptr;
    }

    /* The trailer holds the crc and the uncompressed length; the length is only
     * used to size the buffer, and the result is checked against it. */
    expected = (size_t)data[size - 4u] | ((size_t)data[size - 3u] << 8) |
               ((size_t)data[size - 2u] << 16) | ((size_t)data[size - 1u] << 24);
    if (expected == 0u || expected > (size_t)NV_MAX_DECODE_BYTES) {
        return nullptr;
    }

    output = static_cast<unsigned char*>(malloc(expected));
    if (output == nullptr) {
        return nullptr;
    }
    got = tinfl_decompress_mem_to_mem(output, expected, data + header,
                                      size - header - 8u, 0);
    if (got == SVG_INFLATE_FAILED) {
        free(output);
        return nullptr;
    }
    *out_size = got;
    return output;
}

const char* nv_format_abi_version(void)
{
    return NV_FORMAT_ABI_VERSION;
}

nv_format_status nv_format_query(nv_format_info* out_info)
{
    if (out_info == nullptr) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(out_info->struct_size, sizeof(nv_format_info))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    out_info->name = "SVG";
    out_info->format_id = "svg";
    out_info->extensions = "svg,svgz";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

/*
 * Raster size policy: a document that declares its own pixel size is drawn at
 * that size, and a preview request scales it down to the requested long edge.
 */
nv_format_status nv_format_decode(const nv_format_decode_request* request, nv_format_image* out_image)
{
    NSVGimage* document;
    NSVGrasterizer* rasterizer;
    char* document_text;
    const unsigned char* document_bytes;
    unsigned char* document_owned;
    size_t document_size;
    nv_format_pixel_format output_format;
    int components;
    int width;
    int height;
    int source_width;
    int source_height;
    int32_t max_dimension;
    float scale = 1.0f;
    unsigned char* rgba = nullptr;
    size_t pixel_count;
    size_t bytes;

    if (request == nullptr || out_image == nullptr) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }
    if (request->data == nullptr || request->data_size == 0u) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = static_cast<uint32_t>(sizeof(*out_image));

    output_format = nv_output_format(request->desired_pixel_format);
    components = nv_components_for(request->desired_pixel_format);

    /* The parser walks a null-terminated document, so the encoded bytes are
     * copied into a scratch buffer first; an svgz is inflated on the way. */
    document_bytes = request->data;
    document_size = request->data_size;
    document_owned = nullptr;
    if (document_size > 2u && document_bytes[0] == 0x1fu && document_bytes[1] == 0x8bu) {
        document_owned = svg_gunzip(document_bytes, document_size, &document_size);
        if (document_owned == nullptr) {
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        document_bytes = document_owned;
    }
    if (document_size > NV_MAX_DECODE_BYTES) {
        free(document_owned);
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    document_text = static_cast<char*>(malloc(document_size + 1u));
    if (document_text == nullptr) {
        free(document_owned);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memcpy(document_text, document_bytes, document_size);
    document_text[document_size] = '\0';
    free(document_owned);

    document = nsvgParse(document_text, "px", 96.0f);
    free(document_text);
    if (document == nullptr) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (document->width <= 0.0f || document->height <= 0.0f) {
        nsvgDelete(document);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    source_width = static_cast<int>(lroundf(document->width));
    source_height = static_cast<int>(lroundf(document->height));
    if (source_width <= 0 || source_height <= 0) {
        nsvgDelete(document);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    max_dimension = nv_request_max_dimension(request);
    if (max_dimension > 0 && (source_width > max_dimension || source_height > max_dimension)) {
        scale = static_cast<float>(max_dimension) /
            static_cast<float>(source_width > source_height ? source_width : source_height);
    }

    width = static_cast<int>(lroundf(static_cast<float>(source_width) * scale));
    height = static_cast<int>(lroundf(static_cast<float>(source_height) * scale));
    if (width <= 0) { width = 1; }
    if (height <= 0) { height = 1; }

    bytes = static_cast<size_t>(width) * static_cast<size_t>(height) * 4u;
    rgba = static_cast<unsigned char*>(malloc(bytes));
    if (rgba == nullptr) {
        nsvgDelete(document);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memset(rgba, 0, bytes);

    rasterizer = nsvgCreateRasterizer();
    if (rasterizer == nullptr) {
        free(rgba);
        nsvgDelete(document);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    nsvgRasterize(
        rasterizer,
        document,
        0.0f,
        0.0f,
        scale,
        rgba,
        width,
        height,
        width * 4);
    nsvgDeleteRasterizer(rasterizer);
    nsvgDelete(document);

    pixel_count = static_cast<size_t>(width) * static_cast<size_t>(height);

    if (output_format == NV_FORMAT_PIXEL_RGBA8) {
        out_image->pixels = rgba;
    }
    else {
        out_image->pixels = static_cast<unsigned char*>(malloc(pixel_count * static_cast<size_t>(components)));
        if (out_image->pixels == nullptr) {
            free(rgba);
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }
        nv_rgba_to_format(out_image->pixels, rgba, pixel_count, output_format);
        free(rgba);
    }

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * static_cast<size_t>(components);
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = 1u;
    out_image->frame_delay_ms = 0u;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_encode(const nv_format_encode_request* request, nv_format_buffer* out_buffer)
{
    return nv_encode_unsupported(request, out_buffer);
}

void nv_format_image_free(nv_format_image* image)
{
    if (image == nullptr || image->pixels == nullptr) {
        return;
    }
    free(image->pixels);
    image->pixels = nullptr;
    image->pixels_size = 0;
    image->width = 0;
    image->height = 0;
    image->stride = 0;
}

void nv_format_buffer_free(nv_format_buffer* buffer)
{
    if (buffer == nullptr) {
        return;
    }
    free(buffer->data);
    buffer->data = nullptr;
    buffer->size = 0;
}
