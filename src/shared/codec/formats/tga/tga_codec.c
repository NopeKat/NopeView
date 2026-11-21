/*
 * TGA format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image, which reads
 * truecolor, indexed and RLE compressed TGA files.
 * Decoding only: nothing writes TGA.
 */

#if defined(NV_CODEC_MEMORY_BUDGET)
#include "nv_stbi_alloc.h"
#define STBI_MALLOC nv_stbi_malloc
#define STBI_REALLOC nv_stbi_realloc
#define STBI_FREE nv_stbi_free
#endif

#if defined(NV_CODEC_MEMORY_BUDGET)
#define NV_DOWNSCALE_ALLOC nv_stbi_malloc
#else
#define NV_DOWNSCALE_ALLOC malloc
#endif

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_TGA
#include "stb_image.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

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

    out_info->name = "TGA";
    out_info->format_id = "tga";
    out_info->extensions = "tga";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
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
    unsigned char *pixels = NULL;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* TGA holds one image, so only frame 0 is valid. */
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

    pixels = stbi_load_from_memory(
        request->data,
        (int)request->data_size,
        &width,
        &height,
        &channels_in_file,
        components);
    if (pixels == NULL) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* stb_image cannot scale while decoding, so a preview is produced after
     * the full image is read and the large buffer is released right away. */
    max_dimension = nv_request_max_dimension(request);
    stride = width * components;
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride, components, max_dimension,
            stbi_image_free, NV_DOWNSCALE_ALLOC, &source_width, &source_height)) {
        stbi_image_free(pixels);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    pixel_count = (size_t)width * (size_t)height;
    if (output_format == NV_FORMAT_PIXEL_BGRA8) {
        nv_swap_red_blue(pixels, pixel_count, 4);
    }
    else if (output_format == NV_FORMAT_PIXEL_BGR8) {
        nv_swap_red_blue(pixels, pixel_count, 3);
    }

    out_image->width = width;
    out_image->height = height;
    out_image->stride = stride;
    out_image->pixel_format = output_format;
    out_image->pixels = pixels;
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
    stbi_image_free(image->pixels);
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
