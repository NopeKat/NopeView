/*
 * Codec router.
 *
 * Implements the router ABI (nv_router_abi.h). The router discovers format
 * codecs in a directory, asks each one for its capabilities through the format
 * ABI (nv_format_abi.h), and dispatches decode requests to a matching codec.
 *
 * The host never talks to a format codec directly; it only sees nv_router_*.
 */

#include "nv_router_abi.h"
#include "nv_format_abi.h"

#include <windows.h>
#include <wchar.h>

#include <ctype.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

/* Function pointer types resolved from each format library. */
typedef const char *(*fn_abi_version)(void);
typedef nv_format_status (*fn_query)(nv_format_info *);
typedef nv_format_status (*fn_decode)(const nv_format_decode_request *, nv_format_image *);
typedef void (*fn_image_free)(nv_format_image *);

typedef struct nv_format_entry {
    HMODULE module;
    fn_decode decode;
    fn_image_free image_free;
    char format_id[32];
    char extensions[128];
    uint32_t capabilities;
} nv_format_entry;

/*
 * Pixels produced by a format library must be released with that library's own
 * free function. nv_router_image_free receives no context, so ownership is kept
 * in a process-wide registry keyed by the pixel pointer.
 */
typedef struct nv_image_owner {
    void *pixels;
    fn_image_free image_free;
} nv_image_owner;

struct nv_router_context {
    nv_format_entry *entries;
    uint32_t entry_count;
    uint32_t entry_capacity;
    uint32_t abi_mismatch_count;
};

static nv_image_owner *g_owners;
static uint32_t g_owner_count;
static uint32_t g_owner_capacity;
static SRWLOCK g_owner_lock = SRWLOCK_INIT;

/* Last file bytes read, reused while a GIF animation plays. */
typedef struct nv_file_cache {
    char *path;
    uint64_t size;
    FILETIME mtime;
    uint8_t *data;
    size_t data_size;
} nv_file_cache;

static nv_file_cache g_file_cache;
static SRWLOCK g_file_cache_lock = SRWLOCK_INIT;

/* Address used to identify this module when resolving its own directory. */
static int g_module_marker;

static void copy_str(char *destination, size_t destination_size, const char *source)
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

static int add_entry(nv_router_context *context, const nv_format_entry *entry)
{
    if (context->entry_count == context->entry_capacity) {
        uint32_t capacity = (context->entry_capacity == 0u) ? 8u : context->entry_capacity * 2u;
        nv_format_entry *grown =
            (nv_format_entry *)realloc(context->entries, (size_t)capacity * sizeof(nv_format_entry));

        if (grown == NULL) {
            return 0;
        }
        context->entries = grown;
        context->entry_capacity = capacity;
    }

    context->entries[context->entry_count] = *entry;
    context->entry_count++;
    return 1;
}

/* Registers which format library owns a pixel buffer. */
static int owner_add(void *pixels, fn_image_free image_free)
{
    int added = 0;

    AcquireSRWLockExclusive(&g_owner_lock);
    {
        if (g_owner_count == g_owner_capacity) {
            uint32_t capacity = (g_owner_capacity == 0u) ? 8u : g_owner_capacity * 2u;
            nv_image_owner *grown =
                (nv_image_owner *)realloc(g_owners, (size_t)capacity * sizeof(nv_image_owner));

            if (grown != NULL) {
                g_owners = grown;
                g_owner_capacity = capacity;
            }
        }

        if (g_owner_count < g_owner_capacity) {
            g_owners[g_owner_count].pixels = pixels;
            g_owners[g_owner_count].image_free = image_free;
            g_owner_count++;
            added = 1;
        }
    }
    ReleaseSRWLockExclusive(&g_owner_lock);

    return added;
}

/* Removes and returns the free function registered for a pixel buffer. */
static fn_image_free owner_take(void *pixels)
{
    fn_image_free image_free = NULL;
    uint32_t index;

    AcquireSRWLockExclusive(&g_owner_lock);
    {
        for (index = 0u; index < g_owner_count; index++) {
            if (g_owners[index].pixels == pixels) {
                image_free = g_owners[index].image_free;
                g_owners[index] = g_owners[g_owner_count - 1u];
                g_owner_count--;
                break;
            }
        }
    }
    ReleaseSRWLockExclusive(&g_owner_lock);

    return image_free;
}

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
        copy_str(out_extension, out_size, last_dot + 1);
    }
}

/*
 * Reads a whole file into memory before dispatch. The format ABI accepts raw
 * bytes, so codecs never open a path by name. This keeps UTF-8 host paths
 * working on Windows, where narrow fopen would use the active code page.
 */
static int file_stamp(const wchar_t *wide_path, uint64_t *out_size, FILETIME *out_mtime)
{
    WIN32_FILE_ATTRIBUTE_DATA attributes;

    if (!GetFileAttributesExW(wide_path, GetFileExInfoStandard, &attributes)) {
        return 0;
    }
    if ((attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0u) {
        return 0;
    }
    *out_size = ((uint64_t)attributes.nFileSizeHigh << 32) |
                (uint64_t)attributes.nFileSizeLow;
    *out_mtime = attributes.ftLastWriteTime;
    return 1;
}

static int read_file_bytes(const char *path, uint8_t **out_data, size_t *out_size)
{
    int wide_length;
    wchar_t *wide_path;
    uint64_t file_size = 0u;
    FILETIME mtime;
    int have_stamp;
    HANDLE file;
    uint8_t *buffer;
    uint8_t *cache_copy;
    char *cache_path;
    uint64_t offset;
    DWORD chunk;
    DWORD bytes_read;

    *out_data = NULL;
    *out_size = 0u;
    memset(&mtime, 0, sizeof(mtime));

    wide_length = MultiByteToWideChar(CP_UTF8, 0u, path, -1, NULL, 0);
    if (wide_length <= 0) {
        return 0;
    }
    wide_path = (wchar_t *)malloc((size_t)wide_length * sizeof(wchar_t));
    if (wide_path == NULL) {
        return 0;
    }
    if (MultiByteToWideChar(CP_UTF8, 0u, path, -1, wide_path, wide_length) <= 0) {
        free(wide_path);
        return 0;
    }

    have_stamp = file_stamp(wide_path, &file_size, &mtime);
    if (have_stamp != 0) {
        AcquireSRWLockShared(&g_file_cache_lock);
        if (g_file_cache.data != NULL && g_file_cache.path != NULL &&
            strcmp(g_file_cache.path, path) == 0 &&
            g_file_cache.size == file_size &&
            CompareFileTime(&g_file_cache.mtime, &mtime) == 0) {
            buffer = (uint8_t *)malloc(g_file_cache.data_size);
            if (buffer != NULL) {
                memcpy(buffer, g_file_cache.data, g_file_cache.data_size);
                *out_data = buffer;
                *out_size = g_file_cache.data_size;
            }
            ReleaseSRWLockShared(&g_file_cache_lock);
            free(wide_path);
            return buffer != NULL;
        }
        ReleaseSRWLockShared(&g_file_cache_lock);
    }

    file = CreateFileW(wide_path, GENERIC_READ, FILE_SHARE_READ, NULL,
                       OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    free(wide_path);
    if (file == INVALID_HANDLE_VALUE) {
        return 0;
    }

    if (!have_stamp) {
        LARGE_INTEGER size;
        if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0) {
            CloseHandle(file);
            return 0;
        }
        file_size = (uint64_t)size.QuadPart;
    }
    if (file_size == 0u || file_size > (uint64_t)SIZE_MAX) {
        CloseHandle(file);
        return 0;
    }

    buffer = (uint8_t *)malloc((size_t)file_size);
    if (buffer == NULL) {
        CloseHandle(file);
        return 0;
    }

    offset = 0u;
    while (offset < file_size) {
        uint64_t remaining = file_size - offset;
        chunk = remaining > (uint64_t)INT_MAX ? (DWORD)INT_MAX : (DWORD)remaining;
        if (!ReadFile(file, buffer + offset, chunk, &bytes_read, NULL) || bytes_read == 0u) {
            free(buffer);
            CloseHandle(file);
            return 0;
        }
        offset += (uint64_t)bytes_read;
    }
    CloseHandle(file);

    if (have_stamp != 0) {
        cache_path = (char *)malloc(strlen(path) + 1u);
        cache_copy = (uint8_t *)malloc((size_t)file_size);
        if (cache_path != NULL && cache_copy != NULL) {
            memcpy(cache_path, path, strlen(path) + 1u);
            memcpy(cache_copy, buffer, (size_t)file_size);
            AcquireSRWLockExclusive(&g_file_cache_lock);
            free(g_file_cache.path);
            free(g_file_cache.data);
            g_file_cache.path = cache_path;
            g_file_cache.size = file_size;
            g_file_cache.mtime = mtime;
            g_file_cache.data = cache_copy;
            g_file_cache.data_size = (size_t)file_size;
            ReleaseSRWLockExclusive(&g_file_cache_lock);
        }
        else {
            free(cache_path);
            free(cache_copy);
        }
    }

    *out_data = buffer;
    *out_size = (size_t)file_size;
    return 1;
}

/* Loads one format library and appends it to the context. */
static void load_format_library(nv_router_context *context, const wchar_t *full_path)
{
    nv_format_entry entry;
    fn_abi_version abi_version;
    fn_query query;
    fn_decode decode;
    fn_image_free image_free;
    nv_format_info info;

    memset(&entry, 0, sizeof(entry));

    entry.module = LoadLibraryW(full_path);
    if (entry.module == NULL) {
        return;
    }

    abi_version = (fn_abi_version)(void *)GetProcAddress(
        entry.module, "nv_format_abi_version");
    query = (fn_query)(void *)GetProcAddress(entry.module, "nv_format_query");
    decode = (fn_decode)(void *)GetProcAddress(entry.module, "nv_format_decode");
    image_free = (fn_image_free)(void *)GetProcAddress(entry.module, "nv_format_image_free");

    if (abi_version == NULL || query == NULL || decode == NULL || image_free == NULL) {
        context->abi_mismatch_count++;
        OutputDebugStringA("nv_router: skipped a format library with missing exports");
        FreeLibrary(entry.module);
        return;
    }
    if (strcmp(abi_version(), NV_FORMAT_ABI_VERSION) != 0) {
        context->abi_mismatch_count++;
        OutputDebugStringA("nv_router: skipped a format library with an ABI mismatch");
        FreeLibrary(entry.module);
        return;
    }

    memset(&info, 0, sizeof(info));
    info.struct_size = (uint32_t)sizeof(info);
    if (query(&info) != NV_FORMAT_OK) {
        FreeLibrary(entry.module);
        return;
    }

    entry.decode = decode;
    entry.image_free = image_free;
    copy_str(entry.format_id, sizeof(entry.format_id), info.format_id);
    copy_str(entry.extensions, sizeof(entry.extensions), info.extensions);
    entry.capabilities = info.capabilities;

    if (add_entry(context, &entry) == 0) {
        FreeLibrary(entry.module);
    }
}

/* Only files named like <format>_codec.dll are candidate format libraries. */
static int is_codec_dll(const wchar_t *name)
{
    static const wchar_t suffix[] = L"_codec.dll";
    const size_t suffix_length = sizeof(suffix) / sizeof(suffix[0]) - 1u;
    size_t name_length = wcslen(name);

    if (name_length <= suffix_length) {
        return 0;
    }
    return _wcsicmp(name + name_length - suffix_length, suffix) == 0;
}

/* Scans a directory for format codec libraries. Paths are dynamically sized. */
static void scan_directory(nv_router_context *context, const wchar_t *directory)
{
    WIN32_FIND_DATAW find_data;
    size_t directory_length = wcslen(directory);
    size_t pattern_length = directory_length + 8u;
    wchar_t *pattern = (wchar_t *)malloc(pattern_length * sizeof(wchar_t));
    HANDLE find_handle;

    if (pattern == NULL) {
        return;
    }
    if (swprintf_s(pattern, pattern_length, L"%s\\*.dll", directory) < 0) {
        free(pattern);
        return;
    }

    find_handle = FindFirstFileW(pattern, &find_data);
    free(pattern);
    if (find_handle == INVALID_HANDLE_VALUE) {
        return;
    }

    do {
        const wchar_t *name = find_data.cFileName;
        size_t name_length;
        size_t full_length;
        wchar_t *full_path;

        if ((find_data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0u) {
            continue;
        }
        if (!is_codec_dll(name)) {
            continue;
        }

        name_length = wcslen(name);
        full_length = directory_length + 1u + name_length + 1u;
        full_path = (wchar_t *)malloc(full_length * sizeof(wchar_t));
        if (full_path == NULL) {
            continue;
        }
        if (swprintf_s(full_path, full_length, L"%s\\%s", directory, name) >= 0) {
            load_format_library(context, full_path);
        }
        free(full_path);
    } while (FindNextFileW(find_handle, &find_data) != 0);

    FindClose(find_handle);
}

/* Resolves the router's own directory into a heap buffer owned by the caller. */
static wchar_t *default_directory(void)
{
    HMODULE module = NULL;
    wchar_t *module_path = NULL;
    DWORD capacity = MAX_PATH;
    DWORD written;
    wchar_t *last_separator;
    size_t directory_length;
    wchar_t *directory;

    if (GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            (LPCWSTR)&g_module_marker,
            &module) == 0) {
        return NULL;
    }

    for (;;) {
        wchar_t *grown = (wchar_t *)realloc(module_path, (size_t)capacity * sizeof(wchar_t));
        if (grown == NULL) {
            free(module_path);
            return NULL;
        }
        module_path = grown;
        written = GetModuleFileNameW(module, module_path, capacity);
        if (written == 0u) {
            free(module_path);
            return NULL;
        }
        if (written < capacity) {
            break;
        }
        capacity *= 2u;
    }

    last_separator = wcsrchr(module_path, L'\\');
    if (last_separator == NULL) {
        free(module_path);
        return NULL;
    }
    *last_separator = L'\0';

    directory_length = wcslen(module_path) + 1u;
    directory = (wchar_t *)malloc(directory_length * sizeof(wchar_t));
    if (directory != NULL) {
        memcpy(directory, module_path, directory_length * sizeof(wchar_t));
    }
    free(module_path);
    return directory;
}

const char *nv_router_abi_version(void)
{
    return NV_ROUTER_ABI_VERSION;
}

nv_router_status nv_router_create(const char *codec_dir, nv_router_context **out_context)
{
    nv_router_context *context;
    wchar_t *directory = NULL;
    int length;

    if (out_context == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }
    *out_context = NULL;

    context = (nv_router_context *)calloc(1u, sizeof(nv_router_context));
    if (context == NULL) {
        return NV_ROUTER_ERR_OUT_OF_MEMORY;
    }

    if (codec_dir != NULL && codec_dir[0] != '\0') {
        length = MultiByteToWideChar(CP_UTF8, 0u, codec_dir, -1, NULL, 0);
        if (length <= 0) {
            free(context);
            return NV_ROUTER_ERR_INVALID_ARGUMENT;
        }
        directory = (wchar_t *)malloc((size_t)length * sizeof(wchar_t));
        if (directory == NULL) {
            free(context);
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
        }
        if (MultiByteToWideChar(CP_UTF8, 0u, codec_dir, -1, directory, length) <= 0) {
            free(directory);
            free(context);
            return NV_ROUTER_ERR_INVALID_ARGUMENT;
        }
    }
    else {
        directory = default_directory();
        if (directory == NULL) {
            free(context);
            return NV_ROUTER_ERR_IO;
        }
    }

    scan_directory(context, directory);
    free(directory);

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
     * still needs its library's free function to be valid. */
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

nv_router_status nv_router_decode_file(
    nv_router_context *context,
    const char *path,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    nv_router_image *out_image)
{
    char extension[32];
    uint8_t *file_data;
    size_t file_size;
    nv_router_status last_status;
    uint32_t index;

    if (context == NULL || path == NULL || out_image == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }

    out_image->width = 0;
    out_image->height = 0;
    out_image->stride = 0;
    out_image->pixel_format = NV_ROUTER_PIXEL_UNKNOWN;
    out_image->pixels = NULL;
    out_image->pixels_size = 0;
    out_image->frame_count = 0;
    out_image->frame_delay_ms = 0;

    path_extension(path, extension, sizeof(extension));

    if (!read_file_bytes(path, &file_data, &file_size)) {
        return NV_ROUTER_ERR_IO;
    }

    last_status = NV_ROUTER_ERR_UNSUPPORTED_FORMAT;

    for (index = 0u; index < context->entry_count; index++) {
        const nv_format_entry *entry = &context->entries[index];
        nv_format_decode_request request;
        nv_format_image image;

        if ((entry->capabilities & NV_FORMAT_CAP_DECODE) == 0u) {
            continue;
        }
        if (extension[0] != '\0' && extension_matches(entry->extensions, extension) == 0) {
            continue;
        }

        memset(&request, 0, sizeof(request));
        request.struct_size = (uint32_t)sizeof(request);
        request.data = file_data;
        request.data_size = file_size;
        request.path = path;
        request.desired_pixel_format = (nv_format_pixel_format)desired_pixel_format;
        request.frame_index = frame_index;

        memset(&image, 0, sizeof(image));
        image.struct_size = (uint32_t)sizeof(image);

        if (entry->decode(&request, &image) != NV_FORMAT_OK ||
            image.pixels == NULL || image.pixels_size == 0u) {
            last_status = NV_ROUTER_ERR_DECODE_FAILED;
            continue;
        }

        if (owner_add(image.pixels, entry->image_free) == 0) {
            entry->image_free(&image);
            free(file_data);
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
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
        free(file_data);
        return NV_ROUTER_OK;
    }

    free(file_data);
    if (last_status == NV_ROUTER_ERR_UNSUPPORTED_FORMAT &&
        context->abi_mismatch_count > 0u) {
        return NV_ROUTER_ERR_ABI_MISMATCH;
    }
    return last_status;
}

void nv_router_image_free(nv_router_image *image)
{
    fn_image_free image_free;

    if (image == NULL || image->pixels == NULL) {
        return;
    }

    image_free = owner_take(image->pixels);
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
}
