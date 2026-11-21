/*
 * HDR format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image, which reads
 * Radiance RGBE pictures into linear float RGB.
 * The pixels are tone mapped into sRGB 8-bit values for display.
 * Decoding only.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_HDR
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

/*
 * Radiance values are unbounded, so each channel goes through a Reinhard curve
 * before the sRGB gamma. Alpha stays a plain fraction.
 */
static unsigned char hdr_to_byte(float value)
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

static unsigned char hdr_alpha_to_byte(float value)
{
    if (!(value > 0.0f)) {
        return 0;
    }
    if (value >= 1.0f) {
        return 255;
    }
    return (unsigned char)(value * 255.0f + 0.5f);
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

    out_info->name = "Radiance HDR";
    out_info->format_id = "hdr";
    out_info->extensions = "hdr,radiance";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    float *rgba = NULL;
    int width = 0;
    int height = 0;
    int channels_in_file = 0;
    int components;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    size_t index;
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
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    rgba = stbi_loadf_from_memory(
        request->data,
        (int)request->data_size,
        &width,
        &height,
        &channels_in_file,
        4);
    if (rgba == NULL || width <= 0 || height <= 0) {
        if (rgba != NULL) {
            stbi_image_free(rgba);
        }
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* The tone mapped bytes are made first so the preview path stays the same
     * as the eight bit codecs. */
    pixel_count = (size_t)width * (size_t)height;
    pixels = (unsigned char *)malloc(pixel_count * 4u);
    if (pixels == NULL) {
        stbi_image_free(rgba);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    for (index = 0u; index < pixel_count; index++) {
        pixels[index * 4u] = hdr_to_byte(rgba[index * 4u]);
        pixels[index * 4u + 1u] = hdr_to_byte(rgba[index * 4u + 1u]);
        pixels[index * 4u + 2u] = hdr_to_byte(rgba[index * 4u + 2u]);
        pixels[index * 4u + 3u] = hdr_alpha_to_byte(rgba[index * 4u + 3u]);
    }
    stbi_image_free(rgba);
    rgba = NULL;

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
