/*
 * BMP format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image and
 * stb_image_write.
 * Decoding and encoding are both supported.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_BMP
#include "stb_image.h"

#define STB_IMAGE_WRITE_IMPLEMENTATION
#define STBI_WRITE_NO_STDIO
#include "stb_image_write.h"

#include "nv_format_abi.h"
#include "nv_format_common.h"

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

    out_info->name = "BMP";
    out_info->format_id = "bmp";
    out_info->extensions = "bmp";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ENCODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    int width = 0;
    int height = 0;
    int channels_in_file = 0;
    int components;
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

    /* BMP is a single-image format here, so only frame 0 is valid. */
    if (request->frame_index != 0u) {
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);

    if (request->data != NULL && request->data_size > 0u) {
        if (request->data_size > NV_MAX_DECODE_BYTES) {
            return NV_FORMAT_ERR_INVALID_ARGUMENT;
        }
        pixels = stbi_load_from_memory(
            request->data,
            (int)request->data_size,
            &width,
            &height,
            &channels_in_file,
            components);
    }
    else if (request->path != NULL) {
        pixels = stbi_load(request->path, &width, &height, &channels_in_file, components);
    }
    else {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    if (pixels == NULL) {
        return NV_FORMAT_ERR_DECODE_FAILED;
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
    out_image->stride = width * components;
    out_image->pixel_format = (nv_format_pixel_format)output_format;
    out_image->pixels = pixels;
    out_image->pixels_size = pixel_count * (size_t)components;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_encode(const nv_format_encode_request *request, nv_format_buffer *out_buffer)
{
    const nv_format_image *image;
    nv_encode_sink sink;
    unsigned char *packed;
    size_t packed_size = 0u;
    int encoded;
    nv_format_status status;

    status = nv_validate_encode(request, out_buffer, &image);
    if (status != NV_FORMAT_OK) {
        return status;
    }

    /* A 32-bit BMP keeps the alpha channel, matching the decoder. */
    packed = nv_image_to_rgb(image, 4, &packed_size);
    if (packed == NULL || packed_size == 0u) {
        free(packed);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    memset(&sink, 0, sizeof(sink));
    encoded = stbi_write_bmp_to_func(
        nv_encode_sink_write,
        &sink,
        image->width,
        image->height,
        4,
        packed);
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
