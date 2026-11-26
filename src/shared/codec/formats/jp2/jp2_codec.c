// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * JP2 format codec.
 * Implements the format ABI (nv_format_abi.h) on top of OpenJPEG, which reads
 * JPEG 2000 codestreams and the JP2 container around them. Both the container
 * and the bare codestream extensions are claimed.
 * Greyscale, RGB colour and YCbCr colour are converted to 8 bit RGBA, using the
 * component precision OpenJPEG reports.
 * Decoding only.
 */

#include "nv_format_abi.h"
#include "nv_format_common.h"

#include "openjpeg.h"

#include <stdlib.h>
#include <string.h>

typedef struct jp2_source {
    const unsigned char *data;
    size_t size;
    size_t position;
} jp2_source;

/* The codec reads through this pool sized buffer on top of the file bytes. */
static OPJ_SIZE_T jp2_read(void *buffer, OPJ_SIZE_T count, void *user)
{
    jp2_source *source = (jp2_source *)user;
    size_t available;

    if (source->position >= source->size) {
        /* OpenJPEG only stops asking on a negative answer, a plain zero would
         * keep it polling the same exhausted stream. */
        return (OPJ_SIZE_T)-1;
    }
    available = source->size - source->position;
    if ((size_t)count > available) {
        count = (OPJ_SIZE_T)available;
    }
    memcpy(buffer, source->data + source->position, (size_t)count);
    source->position += (size_t)count;
    return count;
}

static OPJ_OFF_T jp2_skip(OPJ_OFF_T count, void *user)
{
    jp2_source *source = (jp2_source *)user;
    size_t step;

    if (count < 0) {
        return -1;
    }
    step = (size_t)count;
    if (step > source->size - source->position) {
        /* A short answer would make the caller retry for ever, so report the
         * end of the stream instead. */
        source->position = source->size;
        return -1;
    }
    source->position += step;
    return (OPJ_OFF_T)step;
}

static OPJ_BOOL jp2_seek(OPJ_OFF_T position, void *user)
{
    jp2_source *source = (jp2_source *)user;

    if (position < 0 || (size_t)position > source->size) {
        return OPJ_FALSE;
    }
    source->position = (size_t)position;
    return OPJ_TRUE;
}

/* Corrupt files are reported through the return value, not through the log. */
static void jp2_ignore_message(const char *message, void *user)
{
    (void)message;
    (void)user;
}

/* JPEG 2000 keeps YCbCr in the codestream when the container says so. */
static unsigned char jp2_clamp(int value)
{
    if (value < 0) {
        return 0u;
    }
    if (value > 255) {
        return 255u;
    }
    return (unsigned char)value;
}

static int jp2_component_value(const opj_image_comp_t *comp, int x, int y,
                               int width, int height)
{
    size_t source_x = (size_t)((long long)x * (long long)comp->w / (long long)width);
    size_t source_y = (size_t)((long long)y * (long long)comp->h / (long long)height);
    int value;

    if (comp->data == NULL || comp->w == 0u || comp->h == 0u) {
        return 0;
    }
    if (source_x >= comp->w) {
        source_x = comp->w - 1u;
    }
    if (source_y >= comp->h) {
        source_y = comp->h - 1u;
    }
    value = comp->data[source_y * (size_t)comp->w + source_x];
    return value;
}

/* Component samples are stored at their own precision, so they are rescaled. */
static int jp2_to_byte(int value, const opj_image_comp_t *comp)
{
    long long scaled;

    if (comp->prec == 0u) {
        return 0;
    }
    scaled = value;
    if (comp->sgnd) {
        scaled += (1LL << (comp->prec - 1u));
    }
    if (comp->prec > 8u) {
        scaled >>= (int)(comp->prec - 8u);
    }
    else if (comp->prec < 8u) {
        scaled <<= (int)(8u - comp->prec);
    }
    return (int)scaled;
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

    out_info->name = "JPEG 2000";
    out_info->format_id = "jp2";
    out_info->extensions = "jp2,j2c,jpf,jpc,jpx,j2k";
    out_info->capabilities = NV_FORMAT_CAP_DECODE;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    jp2_source source;
    opj_stream_t *stream = NULL;
    opj_codec_t *codec = NULL;
    opj_dparameters_t parameters;
    opj_image_t *image = NULL;
    unsigned char *pixels = NULL;
    int width;
    int height;
    int components;
    int stride;
    int source_width = 0;
    int source_height = 0;
    int32_t max_dimension;
    nv_format_pixel_format output_format;
    size_t pixel_count;
    unsigned int component_count;
    int is_jp2;
    int y;
    int x;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !nv_size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* One codestream per file, so only frame 0 is valid. */
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

    is_jp2 = request->data[0] == 0x00 && request->data[1] == 0x00 &&
             request->data[2] == 0x00 && request->data[3] == 0x0c &&
             request->data[4] == 'j' && request->data[5] == 'P' &&
             request->data[6] == ' ' && request->data[7] == ' ';

    source.data = request->data;
    source.size = request->data_size;
    source.position = 0u;

    stream = opj_stream_create(8192u, OPJ_TRUE);
    if (stream == NULL) {
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    opj_stream_set_user_data(stream, &source, NULL);
    opj_stream_set_user_data_length(stream, (OPJ_UINT64)request->data_size);
    opj_stream_set_read_function(stream, jp2_read);
    opj_stream_set_skip_function(stream, jp2_skip);
    opj_stream_set_seek_function(stream, jp2_seek);

    codec = opj_create_decompress(is_jp2 ? OPJ_CODEC_JP2 : OPJ_CODEC_J2K);
    if (codec == NULL) {
        opj_stream_destroy(stream);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }
    opj_set_info_handler(codec, jp2_ignore_message, NULL);
    opj_set_warning_handler(codec, jp2_ignore_message, NULL);
    opj_set_error_handler(codec, jp2_ignore_message, NULL);

    opj_set_default_decoder_parameters(&parameters);
    if (!opj_setup_decoder(codec, &parameters) ||
        !opj_read_header(stream, codec, &image) ||
        !opj_decode(codec, stream, image) ||
        !opj_end_decompress(codec, stream)) {
        if (image != NULL) {
            opj_image_destroy(image);
        }
        opj_destroy_codec(codec);
        opj_stream_destroy(stream);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    opj_destroy_codec(codec);
    opj_stream_destroy(stream);
    codec = NULL;
    stream = NULL;

    width = (int)(image->x1 - image->x0);
    height = (int)(image->y1 - image->y0);
    component_count = image->numcomps;
    if (width <= 0 || height <= 0 || component_count == 0u ||
        (size_t)width * (size_t)height > NV_MAX_DECODE_BYTES / 4u) {
        opj_image_destroy(image);
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    components = nv_components_for(request->desired_pixel_format);
    output_format = nv_output_format(request->desired_pixel_format);
    pixel_count = (size_t)width * (size_t)height;

    pixels = (unsigned char *)malloc(pixel_count * 4u);
    if (pixels == NULL) {
        opj_image_destroy(image);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    for (y = 0; y < height; y++) {
        for (x = 0; x < width; x++) {
            unsigned char *out = pixels + ((size_t)y * (size_t)width + (size_t)x) * 4u;
            int samples[4] = { 0, 0, 0, 255 };
            unsigned int index;

            for (index = 0u; index < component_count && index < 4u; index++) {
                samples[index] = jp2_to_byte(
                    jp2_component_value(&image->comps[index], x, y, width, height),
                    &image->comps[index]);
            }

            if (component_count >= 3u) {
                if (image->color_space == OPJ_CLRSPC_SYCC) {
                    /* BT.601 full range, which is what the files carry. */
                    int luma = samples[0];
                    int blue = samples[1] - 128;
                    int red = samples[2] - 128;
                    int converted_red = luma + ((359 * red) >> 8);
                    int converted_green =
                        luma - ((88 * blue + 183 * red) >> 8);
                    int converted_blue = luma + ((454 * blue) >> 8);
                    out[0] = jp2_clamp(converted_red);
                    out[1] = jp2_clamp(converted_green);
                    out[2] = jp2_clamp(converted_blue);
                }
                else {
                    out[0] = jp2_clamp(samples[0]);
                    out[1] = jp2_clamp(samples[1]);
                    out[2] = jp2_clamp(samples[2]);
                }
                out[3] = component_count >= 4u ? jp2_clamp(samples[3]) : 255u;
            }
            else {
                out[0] = out[1] = out[2] = jp2_clamp(samples[0]);
                out[3] = 255u;
            }
        }
    }

    opj_image_destroy(image);
    image = NULL;

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
