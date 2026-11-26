// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * AVIF format codec.
 * Implements the format ABI (nv_format_abi.h) on top of libheif, which reads
 * the ISO BMFF container and drives dav1d for the AV1 pixel data. AVIF is the
 * same HEIF container as HEIC, so only the compressed bitstream differs.
 * libheif composites an auxiliary alpha image into the interleaved RGBA output
 * and applies the embedded colour profile.
 * Decoding only.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <libheif/heif.h>

#include <stdlib.h>
#include <string.h>

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

    out_info->name = "AVIF";
    out_info->format_id = "avif";
    out_info->extensions = "avif";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

/* Copies the interleaved RGBA plane into a tightly packed RGBA8 buffer. */
static unsigned char *avif_pack_rgba(
    const struct heif_image *image, int width, int height)
{
    int source_stride = 0;
    const uint8_t *source;
    unsigned char *pixels;
    size_t row_bytes;
    size_t total;
    int y;

    source = heif_image_get_plane_readonly(
        image, heif_channel_interleaved, &source_stride);
    if (source == NULL || source_stride < width * 4) {
        return NULL;
    }

    row_bytes = (size_t)width * 4u;
    if ((size_t)height > SIZE_MAX / row_bytes) {
        return NULL;
    }
    total = row_bytes * (size_t)height;
    pixels = (unsigned char *)malloc(total);
    if (pixels == NULL) {
        return NULL;
    }

    for (y = 0; y < height; y++) {
        memcpy(pixels + (size_t)y * row_bytes,
               source + (size_t)y * (size_t)source_stride,
               row_bytes);
    }
    return pixels;
}

nv_format_status nv_format_decode(
    const nv_format_decode_request *request, nv_format_image *out_image)
{
    struct heif_context *context = NULL;
    struct heif_image_handle *handle = NULL;
    struct heif_image *image = NULL;
    unsigned char *pixels = NULL;
    struct heif_error error;
    int width;
    int height;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    int components;

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
    if (request->data == NULL || request->data_size == 0u ||
        request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    context = heif_context_alloc();
    if (context == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    error = heif_context_read_from_memory(
        context, request->data, request->data_size, NULL);
    if (error.code != heif_error_Ok) {
        heif_context_free(context);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    error = heif_context_get_primary_image_handle(context, &handle);
    if (error.code != heif_error_Ok) {
        heif_context_free(context);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    width = heif_image_handle_get_width(handle);
    height = heif_image_handle_get_height(handle);
    if (width <= 0 || height <= 0) {
        heif_image_handle_release(handle);
        heif_context_free(context);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* libheif composites the alpha auxiliary image into RGBA and applies the
     * embedded colour transform while decoding to the RGB interleaved plane. */
    error = heif_decode_image(
        handle, &image, heif_colorspace_RGB,
        heif_chroma_interleaved_RGBA, NULL);
    if (error.code != heif_error_Ok) {
        heif_image_handle_release(handle);
        heif_context_free(context);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    pixels = avif_pack_rgba(image, width, height);
    if (pixels == NULL) {
        heif_image_release(image);
        heif_image_handle_release(handle);
        heif_context_free(context);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    heif_image_release(image);
    image = NULL;
    heif_image_handle_release(handle);
    handle = NULL;
    heif_context_free(context);
    context = NULL;

    stride = width * 4;
    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride, 4, max_dimension,
            free, malloc, &source_width, &source_height)) {
        free(pixels);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    output_format = nv_output_format(request->desired_pixel_format);
    components = nv_components_for(output_format);
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

nv_format_status nv_format_encode(
    const nv_format_encode_request *request, nv_format_buffer *out_buffer)
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
