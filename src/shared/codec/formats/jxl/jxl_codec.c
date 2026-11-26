// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * JPEG XL format codec.
 * Implements the format ABI (nv_format_abi.h) on top of libjxl's decoder-only
 * C API. The vendored build compiles the decoder translation units without
 * the JPEG reconstruction and box paths, so no Brotli dependency is needed.
 * Decoding only.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include <jxl/color_encoding.h>
#include <jxl/decode.h>

#include <limits.h>
#include <stdint.h>
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

    out_info->name = "JPEG XL";
    out_info->format_id = "jxl";
    out_info->extensions = "jxl";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(
    const nv_format_decode_request *request, nv_format_image *out_image)
{
    JxlDecoder *decoder = NULL;
    unsigned char *pixels = NULL;
    size_t buffer_size = 0u;
    int width = 0;
    int height = 0;
    int stride = 0;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    nv_format_status status = NV_FORMAT_ERR_DECODE_FAILED;
    size_t pixel_count;
    int components;
    int done = 0;

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

    decoder = JxlDecoderCreate(NULL);
    if (decoder == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    if (JxlDecoderSubscribeEvents(
            decoder, JXL_DEC_BASIC_INFO | JXL_DEC_COLOR_ENCODING |
                         JXL_DEC_FULL_IMAGE) != JXL_DEC_SUCCESS) {
        JxlDecoderDestroy(decoder);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* The app displays straight RGBA8. */
    JxlDecoderSetUnpremultiplyAlpha(decoder, JXL_TRUE);
    JxlDecoderSetInput(decoder, request->data, request->data_size);
    JxlDecoderCloseInput(decoder);

    while (!done) {
        JxlDecoderStatus result = JxlDecoderProcessInput(decoder);

        switch (result) {
        case JXL_DEC_BASIC_INFO: {
            JxlBasicInfo info;

            if (JxlDecoderGetBasicInfo(decoder, &info) != JXL_DEC_SUCCESS) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                done = 1;
                break;
            }
            if (info.xsize == 0u || info.ysize == 0u ||
                info.xsize > (uint32_t)INT_MAX ||
                info.ysize > (uint32_t)INT_MAX) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                done = 1;
                break;
            }
            width = (int)info.xsize;
            height = (int)info.ysize;
            break;
        }

        case JXL_DEC_COLOR_ENCODING: {
            JxlColorEncoding color;

            /* Prefer sRGB for the eight-bit display path. The decoder also
             * accepts XYB inputs without an external colour management
             * system. A failure here only leaves the original encoding. */
            memset(&color, 0, sizeof(color));
            color.color_space = JXL_COLOR_SPACE_RGB;
            color.white_point = JXL_WHITE_POINT_D65;
            color.primaries = JXL_PRIMARIES_SRGB;
            color.transfer_function = JXL_TRANSFER_FUNCTION_SRGB;
            color.rendering_intent = JXL_RENDERING_INTENT_PERCEPTUAL;
            JxlDecoderSetPreferredColorProfile(decoder, &color);
            break;
        }

        case JXL_DEC_NEED_IMAGE_OUT_BUFFER: {
            JxlPixelFormat format;

            format.num_channels = 4u;
            format.data_type = JXL_TYPE_UINT8;
            format.endianness = JXL_NATIVE_ENDIAN;
            format.align = 0u;

            if (width <= 0 || height <= 0 ||
                JxlDecoderImageOutBufferSize(decoder, &format, &buffer_size) !=
                    JXL_DEC_SUCCESS ||
                buffer_size == 0u) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                done = 1;
                break;
            }

            pixels = (unsigned char *)malloc(buffer_size);
            if (pixels == NULL) {
                status = NV_FORMAT_ERR_OUT_OF_MEMORY;
                done = 1;
                break;
            }
            if (JxlDecoderSetImageOutBuffer(
                    decoder, &format, pixels, buffer_size) !=
                JXL_DEC_SUCCESS) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
                done = 1;
                break;
            }
            break;
        }

        case JXL_DEC_FULL_IMAGE:
            status = NV_FORMAT_OK;
            done = 1;
            break;

        case JXL_DEC_SUCCESS:
            done = 1;
            if (status != NV_FORMAT_OK) {
                status = NV_FORMAT_ERR_DECODE_FAILED;
            }
            break;

        case JXL_DEC_NEED_MORE_INPUT:
        case JXL_DEC_ERROR:
        default:
            status = NV_FORMAT_ERR_DECODE_FAILED;
            done = 1;
            break;
        }
    }

    JxlDecoderDestroy(decoder);
    decoder = NULL;

    if (status != NV_FORMAT_OK) {
        free(pixels);
        return status;
    }
    if (pixels == NULL || buffer_size == 0u) {
        free(pixels);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    /* The decoder applies the codestream orientation by default, which can
     * swap the reported dimensions for 90 and 270 degree rotations. */
    if (buffer_size == (size_t)width * (size_t)height * 4u) {
        /* Dimensions already match. */
    }
    else if (buffer_size == (size_t)height * (size_t)width * 4u) {
        int swap = width;

        width = height;
        height = swap;
    }
    else {
        free(pixels);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

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
    image->pixels_size = 0u;
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
    buffer->size = 0u;
}
