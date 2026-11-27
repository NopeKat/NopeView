// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * JPEG format codec.
 * Implements the format ABI (nv_format_abi.h) on top of libjpeg-turbo for
 * decoding and stb_image_write for encoding.
 * Decoding and encoding are both supported.
 */

#if defined(NV_CODEC_MEMORY_BUDGET)
#include "nv_stbi_alloc.h"
#define STBI_MALLOC nv_stbi_malloc
#define STBI_REALLOC nv_stbi_realloc
#define STBI_FREE nv_stbi_free
#endif

#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STBI_WRITE_NO_STDIO
#include "stb_image_write.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

/* jpeglib.h declares the stdio source manager, so FILE must exist first. */
#include <stdio.h>

#include <jpeglib.h>

#include <setjmp.h>
#include <stdlib.h>
#include <string.h>

/* libjpeg reports a failure by calling error_exit and expecting the caller to
 * longjmp back out of the library, so the decode state lives in one block that
 * both the normal and the error path can reach. The jump buffer is allocated
 * separately because embedding a jmp_buf pads the structure. */
typedef struct jpg_error_manager {
    struct jpeg_error_mgr manager;
    jmp_buf *jump;
} jpg_error_manager;

typedef struct jpg_decode_state {
    struct jpeg_decompress_struct decoder;
    jpg_error_manager error;
    unsigned char *pixels;
    unsigned char *file_bytes;
    size_t file_size;
} jpg_decode_state;

static void jpg_handle_error(j_common_ptr info)
{
    jpg_error_manager *error = (jpg_error_manager *)info->err;

    longjmp(*error->jump, 1);
}

/* The library would print warnings and trace output to stderr; the viewer has
 * no console, and a truncated JPEG still decodes, so nothing is emitted. */
static void jpg_ignore_message(j_common_ptr info)
{
    (void)info;
}

/* Largest down-scale that still leaves the longest edge at or above the
 * requested one. libjpeg-turbo turns this into a smaller IDCT, which is what
 * makes a preview cost a fraction of a full decode. */
static int jpg_scale_denominator(int width, int height, int32_t max_dimension)
{
    int denominator;
    int long_edge;

    if (max_dimension <= 0) {
        return 1;
    }

    long_edge = width > height ? width : height;
    for (denominator = 8; denominator >= 2; denominator /= 2) {
        if ((long_edge + denominator - 1) / denominator >= max_dimension) {
            return denominator;
        }
    }
    return 1;
}

/* Expands RGB to RGBA in place of the buffer, which the CMYK path needs. */
static unsigned char *jpg_rgb_to_rgba(unsigned char *rgb, int width, int height)
{
    size_t pixel_count = (size_t)width * (size_t)height;
    unsigned char *rgba;
    size_t index;

    rgba = (unsigned char *)malloc(pixel_count * 4u);
    if (rgba == NULL) {
        free(rgb);
        return NULL;
    }

    for (index = 0u; index < pixel_count; index++) {
        rgba[index * 4u] = rgb[index * 3u];
        rgba[index * 4u + 1u] = rgb[index * 3u + 1u];
        rgba[index * 4u + 2u] = rgb[index * 3u + 2u];
        rgba[index * 4u + 3u] = 0xFFu;
    }

    free(rgb);
    return rgba;
}

/* Reads the whole file so jpeg_mem_src can be used on the path branch. The
 * router always hands over bytes, so this only serves direct codec callers, and
 * it takes the narrow path the C runtime gives it. */
static unsigned char *jpg_read_file(const char *path, size_t *out_size)
{
    FILE *file;
    long length;
    unsigned char *bytes;

    file = fopen(path, "rb");
    if (file == NULL) {
        return NULL;
    }

    length = ftell(file);
    if (length <= 0L || (size_t)length > NV_MAX_DECODE_BYTES) {
        fclose(file);
        return NULL;
    }

    bytes = (unsigned char *)malloc((size_t)length);
    if (bytes == NULL) {
        fclose(file);
        return NULL;
    }

    if (fread(bytes, 1u, (size_t)length, file) != (size_t)length) {
        free(bytes);
        fclose(file);
        return NULL;
    }

    fclose(file);
    *out_size = (size_t)length;
    return bytes;
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

    out_info->name = "JPEG";
    out_info->format_id = "jpg";
    out_info->extensions = "jpg,jpeg,jfif,jpe";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ENCODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    jpg_decode_state *state = NULL;
    const unsigned char *data = NULL;
    size_t data_size = 0u;
    int width;
    int height;
    int stride;
    int source_width;
    int source_height;
    int components;
    int target_components;
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

    /* JPEG is a single-image format here, so only frame 0 is valid. */
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    output_format = nv_output_format(request->desired_pixel_format);

    state = (jpg_decode_state *)malloc(sizeof(*state));
    if (state == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memset(state, 0, sizeof(*state));

    state->error.jump = (jmp_buf *)malloc(sizeof(jmp_buf));
    if (state->error.jump == NULL) {
        free(state);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    if (request->data != NULL && request->data_size > 0u) {
        if (request->data_size > NV_MAX_DECODE_BYTES) {
            free(state->error.jump);
            free(state);
            return NV_FORMAT_ERR_INVALID_ARGUMENT;
        }
        data = request->data;
        data_size = (size_t)request->data_size;
    }
    else if (request->path != NULL) {
        state->file_bytes = jpg_read_file(request->path, &state->file_size);
        if (state->file_bytes == NULL) {
            free(state->error.jump);
            free(state);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        data = state->file_bytes;
        data_size = state->file_size;
    }
    else {
        free(state->error.jump);
        free(state);
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    state->decoder.err = jpeg_std_error(&state->error.manager);
    state->error.manager.error_exit = jpg_handle_error;
    state->error.manager.output_message = jpg_ignore_message;

    if (setjmp(*state->error.jump) != 0) {
        /* The library already gave up; release what this call still owns. */
        jpeg_destroy_decompress(&state->decoder);
        free(state->pixels);
        free(state->file_bytes);
        free(state->error.jump);
        free(state);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    jpeg_create_decompress(&state->decoder);
    jpeg_mem_src(&state->decoder, data, (unsigned long)data_size);

    jpeg_read_header(&state->decoder, TRUE);

    /* The library keeps CMYK in four planes; asking for RGB makes it convert.
     * Every other file decodes straight into the RGBA layout the viewer uses. */
    if (state->decoder.jpeg_color_space == JCS_CMYK ||
        state->decoder.jpeg_color_space == JCS_YCCK) {
        state->decoder.out_color_space = JCS_RGB;
    }
    else {
        state->decoder.out_color_space = JCS_EXT_RGBA;
    }

    source_width = (int)state->decoder.image_width;
    source_height = (int)state->decoder.image_height;
    max_dimension = nv_request_max_dimension(request);
    state->decoder.scale_num = 1u;
    state->decoder.scale_denom = (unsigned int)jpg_scale_denominator(
        source_width, source_height, max_dimension);

    jpeg_start_decompress(&state->decoder);

    width = (int)state->decoder.output_width;
    height = (int)state->decoder.output_height;
    components = (int)state->decoder.output_components;

    /* Four planes from the RGBA request, three from the CMYK one. */
    if (width <= 0 || height <= 0 || components < 3 || components > 4) {
        jpeg_destroy_decompress(&state->decoder);
        free(state->file_bytes);
        free(state->error.jump);
        free(state);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    stride = width * components;
    state->pixels = (unsigned char *)malloc((size_t)stride * (size_t)height);
    if (state->pixels == NULL) {
        jpeg_destroy_decompress(&state->decoder);
        free(state->file_bytes);
        free(state->error.jump);
        free(state);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    while (state->decoder.output_scanline < state->decoder.output_height) {
        unsigned char *row =
            state->pixels + (size_t)state->decoder.output_scanline * (size_t)stride;

        jpeg_read_scanlines(&state->decoder, &row, 1);
    }

    jpeg_finish_decompress(&state->decoder);
    jpeg_destroy_decompress(&state->decoder);

    {
        unsigned char *decoded = state->pixels;
        int decode_components = components;

        state->pixels = NULL;
        free(state->file_bytes);
        free(state->error.jump);
        free(state);

        if (decode_components == 3) {
            decoded = jpg_rgb_to_rgba(decoded, width, height);
            if (decoded == NULL) {
                return NV_FORMAT_ERR_OUT_OF_MEMORY;
            }
            stride = width * 4;
        }

        /* The preview bound is applied after the decode so the result lands
         * exactly on the requested edge instead of on a power-of-two step. */
        if (!nv_limit_decoded_size(
                &decoded, &width, &height, &stride, 4, max_dimension,
                free, malloc, &source_width, &source_height)) {
            return NV_FORMAT_ERR_OUT_OF_MEMORY;
        }

        pixel_count = (size_t)width * (size_t)height;
        target_components = nv_components_for(output_format);

        if (output_format == NV_FORMAT_PIXEL_RGBA8) {
            out_image->pixels = decoded;
        }
        else {
            out_image->pixels = (unsigned char *)malloc(pixel_count * (size_t)target_components);
            if (out_image->pixels == NULL) {
                free(decoded);
                return NV_FORMAT_ERR_OUT_OF_MEMORY;
            }
            nv_rgba_to_format(out_image->pixels, decoded, pixel_count, output_format);
            free(decoded);
        }
    }

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * target_components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * (size_t)target_components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = 1u;
    out_image->frame_delay_ms = 0u;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_encode(const nv_format_encode_request *request, nv_format_buffer *out_buffer)
{
    const nv_format_image *image;
    nv_encode_sink sink;
    unsigned char *packed;
    size_t packed_size = 0u;
    int quality;
    int encoded;
    nv_format_status status;

    status = nv_validate_encode(request, out_buffer, &image);
    if (status != NV_FORMAT_OK) {
        return status;
    }

    /* JPEG has no alpha channel, so three channels are written. */
    packed = nv_image_to_rgb(image, 3, &packed_size);
    if (packed == NULL || packed_size == 0u) {
        free(packed);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    quality = (request->quality > 0 && request->quality <= 100)
        ? request->quality
        : 90;

    memset(&sink, 0, sizeof(sink));
    encoded = stbi_write_jpg_to_func(
        nv_encode_sink_write,
        &sink,
        image->width,
        image->height,
        3,
        packed,
        quality);
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
