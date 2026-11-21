/*
 * WebP format codec.
 * Implements the format ABI (nv_format_abi.h) on top of libwebp's demuxer and
 * anim decoder, which cover still images and animated files with one path.
 * Decoding is supported; encoding is not.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <stdlib.h>
#include <string.h>

#include "webp/decode.h"
#include "webp/demux.h"

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

    out_info->name = "WebP";
    out_info->format_id = "webp";
    out_info->extensions = "webp";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ANIMATED;
    return NV_FORMAT_OK;
}

/*
 * Walks the frames up to frame_index. The anim decoder reports the timestamp
 * of every frame it hands out, so the delay of the frame one step earlier is
 * known once the next frame is pulled.
 */
nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    WebPAnimDecoderOptions options;
    WebPAnimDecoder *decoder;
    WebPAnimInfo info;
    WebPData webp_data;
    nv_format_pixel_format output_format;
    int components;
    int width;
    int height;
    int stride;
    int source_width;
    int source_height;
    int32_t max_dimension;
    uint32_t index;
    uint32_t frame_delay_ms = 0u;
    int timestamp = 0;
    int previous_timestamp = 0;
    unsigned char *pixels = NULL;
    uint8_t *canvas = NULL;
    size_t pixel_count;
    size_t bytes;

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
    if (request->data_size > NV_MAX_DECODE_BYTES) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    output_format = nv_output_format(request->desired_pixel_format);
    components = nv_components_for(request->desired_pixel_format);
    if (!WebPAnimDecoderOptionsInit(&options)) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    /* The RGBA mode keeps transparency; decoding stays single threaded so the
     * library needs no threading layer. */
    options.color_mode = MODE_RGBA;
    options.use_threads = 0;

    webp_data.bytes = request->data;
    webp_data.size = request->data_size;

    decoder = WebPAnimDecoderNew(&webp_data, &options);
    if (decoder == NULL) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    if (!WebPAnimDecoderGetInfo(decoder, &info) ||
        info.canvas_width == 0u || info.canvas_height == 0u) {
        WebPAnimDecoderDelete(decoder);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }
    if (request->frame_index >= info.frame_count) {
        WebPAnimDecoderDelete(decoder);
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    for (index = 0u; index <= request->frame_index; index++) {
        if (!WebPAnimDecoderGetNext(decoder, &canvas, &timestamp)) {
            WebPAnimDecoderDelete(decoder);
            return NV_FORMAT_ERR_DECODE_FAILED;
        }
        if (index > 0u) {
            frame_delay_ms = (uint32_t)(timestamp - previous_timestamp);
        }
        previous_timestamp = timestamp;
    }

    /* One more frame reveals how long the requested frame stays on screen;
     * the last frame keeps the delay of the frame before it. */
    if (request->frame_index + 1u < info.frame_count) {
        if (WebPAnimDecoderGetNext(decoder, &canvas, &timestamp)) {
            frame_delay_ms = (uint32_t)(timestamp - previous_timestamp);
        }
    }

    bytes = (size_t)info.canvas_width * (size_t)info.canvas_height * 4u;
    pixels = (unsigned char *)malloc(bytes);
    if (pixels == NULL) {
        WebPAnimDecoderDelete(decoder);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    memcpy(pixels, canvas, bytes);

    WebPAnimDecoderDelete(decoder);

    stride = (int)info.canvas_width * 4;
    width = (int)info.canvas_width;
    height = (int)info.canvas_height;
    max_dimension = nv_request_max_dimension(request);
    if (!nv_limit_decoded_size(
            &pixels, &width, &height, &stride,
            components, max_dimension, free, malloc, &source_width, &source_height)) {
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

    out_image->width = (int32_t)width;
    out_image->height = (int32_t)height;
    out_image->stride = (int32_t)width * components;
    out_image->pixel_format = output_format;
    out_image->pixels_size = pixel_count * (size_t)components;
    out_image->source_width = source_width;
    out_image->source_height = source_height;
    out_image->frame_count = info.frame_count;
    out_image->frame_delay_ms = frame_delay_ms;
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
