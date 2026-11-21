/*
 * Codec router: the ABI surface and the dispatch.
 *
 * Implements the router ABI (nv_router_abi.h). The other units do the work
 * around it: router_loader.c finds and loads the format codecs, router_io.c
 * reads and writes bytes, router_cache.c keeps the last file read, and
 * router_platform.c holds the platform calls.
 *
 * The host never talks to a format codec directly; it only sees nv_router_*.
 */

#include "router_internal.h"

/* Shared with the loader, which names format ids and extensions from a codec. */
void nv_copy_str(char *destination, size_t destination_size, const char *source)
{
    size_t length;

    if (destination == NULL || destination_size == 0u) {
        return;
    }
    if (source == NULL) {
        destination[0] = '\0';
        return;
    }

    length = strlen(source);
    if (length >= destination_size) {
        length = destination_size - 1u;
    }
    memcpy(destination, source, length);
    destination[length] = '\0';
}

/*
 * Pixels produced by a format library must be released with that library's own
 * free function. The router stores that function in the image itself, under
 * pixels_release, because nv_router_image_free receives no context.
 */

/* Case-insensitive match of a comma-separated extension list against a file extension. */
static int extension_matches(const char *list, const char *extension)
{
    const char *cursor = list;
    size_t extension_length = strlen(extension);

    while (*cursor != '\0') {
        const char *token_end = cursor;
        size_t token_length;
        size_t offset;
        size_t trimmed_length;
        int equal = 1;

        while (*token_end != '\0' && *token_end != ',') {
            token_end++;
        }
        token_length = (size_t)(token_end - cursor);

        offset = 0u;
        while (offset < token_length && isspace((unsigned char)cursor[offset]) != 0) {
            offset++;
        }
        trimmed_length = token_length - offset;
        while (trimmed_length > 0u &&
               isspace((unsigned char)cursor[offset + trimmed_length - 1u]) != 0) {
            trimmed_length--;
        }

        if (trimmed_length > 0u && trimmed_length == extension_length) {
            size_t index;
            for (index = 0u; index < trimmed_length; index++) {
                if (tolower((unsigned char)cursor[offset + index]) !=
                    tolower((unsigned char)extension[index])) {
                    equal = 0;
                    break;
                }
            }
            if (equal != 0) {
                return 1;
            }
        }

        if (*token_end == ',') {
            cursor = token_end + 1;
        }
        else {
            break;
        }
    }
    return 0;
}

/* Extracts the extension of a path without the leading dot. */
static void path_extension(const char *path, char *out_extension, size_t out_size)
{
    const char *last_dot = NULL;
    const char *cursor;

    out_extension[0] = '\0';
    if (path == NULL) {
        return;
    }

    for (cursor = path; *cursor != '\0'; cursor++) {
        if (*cursor == '\\' || *cursor == '/') {
            last_dot = NULL;
        }
        else if (*cursor == '.') {
            last_dot = cursor;
        }
    }

    if (last_dot != NULL) {
        nv_copy_str(out_extension, out_size, last_dot + 1);
    }
}

static void clear_router_image(nv_router_image *out_image)
{
    out_image->width = 0;
    out_image->height = 0;
    out_image->stride = 0;
    out_image->pixel_format = NV_ROUTER_PIXEL_UNKNOWN;
    out_image->pixels = NULL;
    out_image->pixels_size = 0;
    out_image->frame_count = 0;
    out_image->frame_delay_ms = 0;
    out_image->source_width = 0;
    out_image->source_height = 0;
    out_image->pixels_release = NULL;
}

/*
 * Dispatches already-read bytes to the first matching codec. extension may be
 * empty, which asks every decoder to probe the bytes; descriptors have no path
 * to derive an extension from.
 */
static nv_router_status dispatch_decode(
    nv_router_context *context,
    const uint8_t *data,
    size_t data_size,
    const char *path,
    const char *extension,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    int32_t max_dimension,
    nv_router_image *out_image)
{
    nv_router_status last_status = NV_ROUTER_ERR_UNSUPPORTED_FORMAT;
    uint32_t index;

    clear_router_image(out_image);

    for (index = 0u; index < context->entry_count; index++) {
        const nv_format_entry *entry = &context->entries[index];
        nv_format_decode_request request;
        nv_format_image image;

        if ((entry->capabilities & NV_FORMAT_CAP_DECODE) == 0u) {
            continue;
        }
        if (extension != NULL && extension[0] != '\0' &&
            extension_matches(entry->extensions, extension) == 0) {
            continue;
        }

        memset(&request, 0, sizeof(request));
        request.struct_size = (uint32_t)sizeof(request);
        request.data = data;
        request.data_size = data_size;
        request.path = path;
        request.desired_pixel_format = (nv_format_pixel_format)desired_pixel_format;
        request.frame_index = frame_index;
        request.max_dimension = max_dimension;

        memset(&image, 0, sizeof(image));
        image.struct_size = (uint32_t)sizeof(image);

        if (entry->decode(&request, &image) != NV_FORMAT_OK ||
            image.pixels == NULL || image.pixels_size == 0u) {
            last_status = NV_ROUTER_ERR_DECODE_FAILED;
            continue;
        }

        out_image->struct_size = (uint32_t)sizeof(*out_image);
        out_image->width = image.width;
        out_image->height = image.height;
        out_image->stride = image.stride;
        out_image->pixel_format = (nv_router_pixel_format)image.pixel_format;
        out_image->pixels = image.pixels;
        out_image->pixels_size = image.pixels_size;
        out_image->frame_count = image.frame_count;
        out_image->frame_delay_ms = image.frame_delay_ms;
        out_image->source_width = image.source_width;
        out_image->source_height = image.source_height;

        /* The host holds only this pointer, so the codec travels with the
         * image; the cast is back to fn_image_free in the free path. */
        out_image->pixels_release = (void *)entry->image_free;
        return NV_ROUTER_OK;
    }

    if (last_status == NV_ROUTER_ERR_UNSUPPORTED_FORMAT &&
        context->abi_mismatch_count > 0u) {
        return NV_ROUTER_ERR_ABI_MISMATCH;
    }
    return last_status;
}

const char *nv_router_abi_version(void)
{
    return NV_ROUTER_ABI_VERSION;
}

nv_router_status nv_router_create(const char *codec_dir, nv_router_context **out_context)
{
    nv_router_context *context;
    nv_router_status status;

    if (out_context == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }
    *out_context = NULL;

    context = (nv_router_context *)calloc(1u, sizeof(nv_router_context));
    if (context == NULL) {
        return NV_ROUTER_ERR_OUT_OF_MEMORY;
    }
    nv_rwlock_init(&context->cache_lock);

    status = nv_loader_start(context, codec_dir);
    if (status != NV_ROUTER_OK) {
        nv_rwlock_dispose(&context->cache_lock);
        free(context);
        return status;
    }

    *out_context = context;
    return NV_ROUTER_OK;
}

void nv_router_destroy(nv_router_context *context)
{
    if (context == NULL) {
        return;
    }

    /* Format modules stay loaded for the process lifetime. nv_router_image_free
     * has no context, so an image may be released after this destroy call and
     * still needs its library's free function to be valid. The cache belongs to
     * this context, so its bytes go with it. */
    nv_cache_clear(context);
    nv_rwlock_dispose(&context->cache_lock);
    free(context->entries);
    free(context);
}

nv_router_status nv_router_enum_formats(
    nv_router_context *context,
    uint32_t index,
    nv_router_format *out_format)
{
    const nv_format_entry *entry;
    uint32_t available;
    uint32_t writable;
    uint32_t full = (uint32_t)sizeof(*out_format);

    if (context == NULL || out_format == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }
    if (index >= context->entry_count) {
        return NV_ROUTER_ERR_NOT_FOUND;
    }

    available = out_format->struct_size;
    writable = available < full ? available : full;
    if (writable < (uint32_t)offsetof(nv_router_format, format_id)) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }

    entry = &context->entries[index];
    out_format->struct_size = writable;
    if (writable >= (uint32_t)offsetof(nv_router_format, format_id) +
                     sizeof(out_format->format_id)) {
        out_format->format_id = entry->format_id;
    }
    if (writable >= (uint32_t)offsetof(nv_router_format, extensions) +
                     sizeof(out_format->extensions)) {
        out_format->extensions = entry->extensions;
    }
    if (writable >= (uint32_t)offsetof(nv_router_format, can_decode) +
                     sizeof(out_format->can_decode)) {
        out_format->can_decode = ((entry->capabilities & NV_FORMAT_CAP_DECODE) != 0u) ? 1u : 0u;
    }
    if (writable >= (uint32_t)offsetof(nv_router_format, can_encode) +
                     sizeof(out_format->can_encode)) {
        out_format->can_encode = ((entry->capabilities & NV_FORMAT_CAP_ENCODE) != 0u) ? 1u : 0u;
    }
    if (writable >= (uint32_t)offsetof(nv_router_format, can_animate) +
                     sizeof(out_format->can_animate)) {
        out_format->can_animate = ((entry->capabilities & NV_FORMAT_CAP_ANIMATED) != 0u) ? 1u : 0u;
    }
    return NV_ROUTER_OK;
}

nv_router_status nv_router_decode_file_ex(
    nv_router_context *context,
    const char *path,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    int32_t max_dimension,
    nv_router_image *out_image)
{
    char extension[32];
    nv_file_bytes file;
    nv_router_status status;

    if (context == NULL || path == NULL || out_image == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }

    path_extension(path, extension, sizeof(extension));

    if (nv_io_read_file(context, path, &file) == 0) {
        return NV_ROUTER_ERR_IO;
    }

    status = dispatch_decode(
        context, file.data, file.size, path, extension,
        desired_pixel_format, frame_index, max_dimension, out_image);
    nv_io_release(context, &file);
    return status;
}

nv_router_status nv_router_decode_fd_ex(
    nv_router_context *context,
    int fd,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    int32_t max_dimension,
    nv_router_image *out_image)
{
    nv_file_bytes file;
    nv_router_status status;

    if (context == NULL || fd < 0 || out_image == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }

    if (nv_io_read_fd(fd, &file) == 0) {
        return NV_ROUTER_ERR_IO;
    }

    status = dispatch_decode(
        context, file.data, file.size, NULL, "",
        desired_pixel_format, frame_index, max_dimension, out_image);
    nv_io_release(context, &file);
    return status;
}

nv_router_status nv_router_decode_fd(
    nv_router_context *context,
    int fd,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    nv_router_image *out_image)
{
    return nv_router_decode_fd_ex(
        context, fd, desired_pixel_format, frame_index, 0, out_image);
}

/* Kept for callers that only want full-size decoding. */
nv_router_status nv_router_decode_file(
    nv_router_context *context,
    const char *path,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    nv_router_image *out_image)
{
    return nv_router_decode_file_ex(
        context, path, desired_pixel_format, frame_index, 0, out_image);
}

nv_router_status nv_router_encode_file(
    nv_router_context *context,
    const char *path,
    const nv_router_image *image,
    int32_t quality)
{
    char extension[32];
    uint32_t index;

    if (context == NULL || path == NULL || image == NULL ||
        image->pixels == NULL || image->width <= 0 || image->height <= 0) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }

    path_extension(path, extension, sizeof(extension));
    if (extension[0] == '\0') {
        return NV_ROUTER_ERR_UNSUPPORTED_FORMAT;
    }

    for (index = 0u; index < context->entry_count; index++) {
        const nv_format_entry *entry = &context->entries[index];
        nv_format_image source;
        nv_format_encode_request request;
        nv_format_buffer buffer;
        nv_format_status status;

        if ((entry->capabilities & NV_FORMAT_CAP_ENCODE) == 0u ||
            entry->encode == NULL) {
            continue;
        }
        if (extension_matches(entry->extensions, extension) == 0) {
            continue;
        }

        memset(&source, 0, sizeof(source));
        source.struct_size = (uint32_t)sizeof(source);
        source.width = image->width;
        source.height = image->height;
        source.stride = image->stride;
        source.pixel_format = (nv_format_pixel_format)image->pixel_format;
        source.pixels = image->pixels;
        source.pixels_size = image->pixels_size;
        source.frame_count = image->frame_count;
        source.frame_delay_ms = image->frame_delay_ms;

        memset(&request, 0, sizeof(request));
        request.struct_size = (uint32_t)sizeof(request);
        request.image = &source;
        request.quality = quality;

        memset(&buffer, 0, sizeof(buffer));
        buffer.struct_size = (uint32_t)sizeof(buffer);

        status = entry->encode(&request, &buffer);
        if (status == NV_FORMAT_OK && buffer.data != NULL && buffer.size > 0u) {
            int written = nv_io_write_file(path, buffer.data, buffer.size);

            if (entry->buffer_free != NULL) {
                entry->buffer_free(&buffer);
            }
            return written != 0 ? NV_ROUTER_OK : NV_ROUTER_ERR_IO;
        }

        if (entry->buffer_free != NULL) {
            entry->buffer_free(&buffer);
        }

        switch (status) {
        case NV_FORMAT_ERR_OUT_OF_MEMORY:
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
        case NV_FORMAT_ERR_UNSUPPORTED:
            continue;
        default:
            return NV_ROUTER_ERR_ENCODE_FAILED;
        }
    }

    return NV_ROUTER_ERR_UNSUPPORTED_FORMAT;
}

void nv_router_image_free(nv_router_image *image)
{
    fn_image_free image_free = NULL;

    if (image == NULL || image->pixels == NULL) {
        return;
    }

    /* pixels_release is the last field, so a struct smaller than this one
     * cannot hold it; the pixels are then left to the process, which is only
     * possible with a host built against an older header. */
    if (image->struct_size >= (uint32_t)sizeof(*image)) {
        image_free = (fn_image_free)image->pixels_release;
    }
    if (image_free != NULL) {
        nv_format_image format_image;

        memset(&format_image, 0, sizeof(format_image));
        format_image.struct_size = (uint32_t)sizeof(format_image);
        format_image.pixels = image->pixels;
        image_free(&format_image);
    }

    image->pixels = NULL;
    image->pixels_size = 0;
    image->width = 0;
    image->height = 0;
    image->stride = 0;
    image->frame_count = 0;
    image->frame_delay_ms = 0;
    image->source_width = 0;
    image->source_height = 0;
    image->pixels_release = NULL;
}
