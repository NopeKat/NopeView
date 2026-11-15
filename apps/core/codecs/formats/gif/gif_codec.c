/*
 * GIF format codec.
 * Implements the format ABI (nv_format_abi.h) on top of stb_image.
 *
 * GIF can be animated, so the decoder honours nv_format_decode_request.frame_index.
 * stb exposes every frame at once; the selected frame is copied out so the
 * caller receives a buffer it can own and free normally.
 */

#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_GIF
#include "stb_image.h"

#include "nv_format_abi.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Returns 1 when the caller-provided struct_size is usable for our layout. */
static int size_ok(uint32_t struct_size, size_t required)
{
    return struct_size == 0u || (size_t)struct_size >= required;
}

/* Reads a whole file into memory. The caller frees the buffer with free(). */
static int read_file(const char *path, unsigned char **out_data, size_t *out_size)
{
    FILE *file;
    __int64 size;
    unsigned char *buffer;
    size_t read_count;

    *out_data = NULL;
    *out_size = 0u;

    if (fopen_s(&file, path, "rb") != 0 || file == NULL) {
        return 0;
    }

    if (_fseeki64(file, 0, SEEK_END) != 0) {
        fclose(file);
        return 0;
    }
    size = _ftelli64(file);
    if (size <= 0 || size > (__int64)0x7fffffff) {
        fclose(file);
        return 0;
    }
    if (_fseeki64(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return 0;
    }

    buffer = (unsigned char *)malloc((size_t)size);
    if (buffer == NULL) {
        fclose(file);
        return 0;
    }

    read_count = fread(buffer, 1u, (size_t)size, file);
    fclose(file);

    if (read_count != (size_t)size) {
        free(buffer);
        return 0;
    }

    *out_data = buffer;
    *out_size = (size_t)size;
    return 1;
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
    if (!size_ok(out_info->struct_size, sizeof(nv_format_info))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    out_info->name = "GIF";
    out_info->format_id = "gif";
    out_info->extensions = "gif";
    out_info->capabilities = NV_FORMAT_CAP_DECODE | NV_FORMAT_CAP_ANIMATED;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_decode(const nv_format_decode_request *request, nv_format_image *out_image)
{
    unsigned char *data = NULL;
    size_t data_size = 0u;
    int owns_data = 0;
    int *delays = NULL;
    int width = 0;
    int height = 0;
    int frames = 0;
    int channels_in_file = 0;
    unsigned char *all_frames = NULL;
    unsigned char *frame_pixels = NULL;
    size_t frame_bytes;

    if (request == NULL || out_image == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!size_ok(request->struct_size, sizeof(nv_format_decode_request)) ||
        !size_ok(out_image->struct_size, sizeof(nv_format_image))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);

    if (request->data != NULL && request->data_size > 0u) {
        if (request->data_size > (size_t)0x7fffffff) {
            return NV_FORMAT_ERR_INVALID_ARGUMENT;
        }
        data = (unsigned char *)request->data;
        data_size = request->data_size;
    }
    else if (request->path != NULL) {
        if (read_file(request->path, &data, &data_size) == 0) {
            return NV_FORMAT_ERR_IO;
        }
        owns_data = 1;
    }
    else {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }

    /* Four channels so the output is always RGBA. */
    all_frames = stbi_load_gif_from_memory(
        data,
        (int)data_size,
        &delays,
        &width,
        &height,
        &frames,
        &channels_in_file,
        4);

    if (owns_data != 0) {
        free(data);
    }
    free(delays);

    if (all_frames == NULL) {
        return NV_FORMAT_ERR_DECODE_FAILED;
    }

    if (request->frame_index >= (uint32_t)frames) {
        stbi_image_free(all_frames);
        return NV_FORMAT_ERR_UNSUPPORTED;
    }

    frame_bytes = (size_t)width * (size_t)height * 4u;
    frame_pixels = (unsigned char *)malloc(frame_bytes);
    if (frame_pixels == NULL) {
        stbi_image_free(all_frames);
        return NV_FORMAT_ERR_OUT_OF_MEMORY;
    }

    memcpy(frame_pixels, all_frames + (size_t)request->frame_index * frame_bytes, frame_bytes);
    stbi_image_free(all_frames);

    out_image->width = width;
    out_image->height = height;
    out_image->stride = width * 4;
    out_image->pixel_format = NV_FORMAT_PIXEL_RGBA8;
    out_image->pixels = frame_pixels;
    out_image->pixels_size = frame_bytes;
    return NV_FORMAT_OK;
}

nv_format_status nv_format_encode(const nv_format_encode_request *request, nv_format_buffer *out_buffer)
{
    if (request == NULL || out_buffer == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!size_ok(request->struct_size, sizeof(nv_format_encode_request)) ||
        !size_ok(out_buffer->struct_size, sizeof(nv_format_buffer))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }

    /* GIF encoding is not implemented yet. */
    return NV_FORMAT_ERR_UNSUPPORTED;
}

void nv_format_image_free(nv_format_image *image)
{
    if (image == NULL || image->pixels == NULL) {
        return;
    }
    /* Frame pixels are allocated with malloc, not by stb. */
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
    /* Nothing is allocated until encoding is implemented. */
    buffer->data = NULL;
    buffer->size = 0;
}
