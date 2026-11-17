/*
 * Codec router.
 *
 * Implements the router ABI (nv_router_abi.h). The router discovers format
 * codecs in a directory, asks each one for its capabilities through the format
 * ABI (nv_format_abi.h), and dispatches decode requests to a matching codec.
 *
 * The host never talks to a format codec directly; it only sees nv_router_*.
 */

#if !defined(_WIN32) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE
#endif

#include "nv_router_abi.h"
#include "nv_format_abi.h"

#if defined(_WIN32)
#include <windows.h>
#include <wchar.h>
#else
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#if defined(__ANDROID__)
#include <android/log.h>
#endif
#endif

#include <ctype.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

/* Function pointer types resolved from each format library. */
typedef const char *(*fn_abi_version)(void);
typedef nv_format_status (*fn_query)(nv_format_info *);
typedef nv_format_status (*fn_decode)(const nv_format_decode_request *, nv_format_image *);
typedef nv_format_status (*fn_encode)(const nv_format_encode_request *, nv_format_buffer *);
typedef void (*fn_image_free)(nv_format_image *);
typedef void (*fn_buffer_free)(nv_format_buffer *);

#if defined(_WIN32)
typedef HMODULE nv_module;
#else
typedef void *nv_module;
#endif

typedef struct nv_format_entry {
    nv_module module;
    fn_decode decode;
    fn_encode encode;
    fn_image_free image_free;
    fn_buffer_free buffer_free;
    char format_id[32];
    char extensions[128];
    uint32_t capabilities;
} nv_format_entry;

#if defined(_WIN32)
typedef SRWLOCK nv_rwlock;
#define NV_RWLOCK_INITIALIZER SRWLOCK_INIT

static void nv_rwlock_read(nv_rwlock *lock)
{
    AcquireSRWLockShared(lock);
}

static void nv_rwlock_write(nv_rwlock *lock)
{
    AcquireSRWLockExclusive(lock);
}

static void nv_rwlock_read_unlock(nv_rwlock *lock)
{
    ReleaseSRWLockShared(lock);
}

static void nv_rwlock_write_unlock(nv_rwlock *lock)
{
    ReleaseSRWLockExclusive(lock);
}
#else
typedef pthread_rwlock_t nv_rwlock;
#define NV_RWLOCK_INITIALIZER PTHREAD_RWLOCK_INITIALIZER

static void nv_rwlock_read(nv_rwlock *lock)
{
    pthread_rwlock_rdlock(lock);
}

static void nv_rwlock_write(nv_rwlock *lock)
{
    pthread_rwlock_wrlock(lock);
}

static void nv_rwlock_read_unlock(nv_rwlock *lock)
{
    pthread_rwlock_unlock(lock);
}

static void nv_rwlock_write_unlock(nv_rwlock *lock)
{
    pthread_rwlock_unlock(lock);
}
#endif

static void nv_log(const char *message)
{
#if defined(_WIN32)
    OutputDebugStringA(message);
#elif defined(__ANDROID__)
    __android_log_write(ANDROID_LOG_INFO, "NopeViewNative", message);
#else
    (void)message;
#endif
}

#if defined(__ANDROID__)
#define nv_log_format(...) \
    __android_log_print(ANDROID_LOG_INFO, "NopeViewNative", __VA_ARGS__)
#else
#define nv_log_format(...) ((void)0)
#endif

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
static nv_rwlock g_owner_lock = NV_RWLOCK_INITIALIZER;

/* Last file bytes read, reused while a GIF animation plays. */
typedef struct nv_file_cache {
    char *path;
    uint64_t size;
    int64_t mtime;
    uint8_t *data;
    size_t data_size;
} nv_file_cache;

/*
 * Files above this size are decoded without being cached. Holding a very large
 * image for the whole process lifetime costs more memory than the repeat reads
 * it saves, and still images are normally read once.
 */
#define NV_FILE_CACHE_MAX_BYTES ((size_t)32u * 1024u * 1024u)

static nv_file_cache g_file_cache;
static nv_rwlock g_file_cache_lock = NV_RWLOCK_INITIALIZER;

#if defined(_WIN32)
/* Address used to identify this module when resolving its own directory. */
static int g_module_marker;
#endif

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

    nv_rwlock_write(&g_owner_lock);
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
    nv_rwlock_write_unlock(&g_owner_lock);

    return added;
}

/* Removes and returns the free function registered for a pixel buffer. */
static fn_image_free owner_take(void *pixels)
{
    fn_image_free image_free = NULL;
    uint32_t index;

    nv_rwlock_write(&g_owner_lock);
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
    nv_rwlock_write_unlock(&g_owner_lock);

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

/* Size and write time, used to detect a changed file behind a cached copy. */
static int file_stamp(const char *path, uint64_t *out_size, int64_t *out_mtime)
{
#if defined(_WIN32)
    int wide_length;
    wchar_t *wide_path;
    WIN32_FILE_ATTRIBUTE_DATA attributes;
    int ok = 0;

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

    if (GetFileAttributesExW(wide_path, GetFileExInfoStandard, &attributes) &&
        (attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0u) {
        *out_size = ((uint64_t)attributes.nFileSizeHigh << 32) |
                    (uint64_t)attributes.nFileSizeLow;
        *out_mtime = ((int64_t)attributes.ftLastWriteTime.dwHighDateTime << 32) |
                     (int64_t)attributes.ftLastWriteTime.dwLowDateTime;
        ok = 1;
    }
    free(wide_path);
    return ok;
#else
    struct stat info;

    if (path == NULL || stat(path, &info) != 0 || !S_ISREG(info.st_mode)) {
        return 0;
    }
    *out_size = (uint64_t)info.st_size;
    *out_mtime = (int64_t)info.st_mtime;
    return 1;
#endif
}

#ifndef NV_ROUTER_MAX_INPUT_BYTES
#define NV_ROUTER_MAX_INPUT_BYTES 0
#endif

/*
 * Encoded bytes for one decode. data points either at the shared cache or at a
 * private allocation; cached tells release_file_bytes how to let go of it.
 */
typedef struct nv_file_bytes {
    const uint8_t *data;
    size_t size;
    int cached;
    int owned;
} nv_file_bytes;

/*
 * A cache hit keeps the shared lock and hands out the cached bytes directly.
 * Copying them per call was the dominant cost of GIF playback, where every
 * frame reads the same unchanged file.
 */
static void release_file_bytes(nv_file_bytes *bytes)
{
    if (bytes->cached != 0) {
        nv_rwlock_read_unlock(&g_file_cache_lock);
    }
    else if (bytes->owned != 0) {
        free((void *)bytes->data);
    }
    bytes->data = NULL;
    bytes->size = 0u;
    bytes->cached = 0;
    bytes->owned = 0;
}

static void cache_store(const char *path, const uint8_t *data, size_t size,
                        uint64_t file_size, int64_t mtime)
{
    char *cache_path;
    uint8_t *cache_copy;

    if (size > NV_FILE_CACHE_MAX_BYTES) {
        return;
    }

    cache_path = (char *)malloc(strlen(path) + 1u);
    cache_copy = (uint8_t *)malloc(size);
    if (cache_path != NULL && cache_copy != NULL) {
        memcpy(cache_path, path, strlen(path) + 1u);
        memcpy(cache_copy, data, size);
        nv_rwlock_write(&g_file_cache_lock);
        free(g_file_cache.path);
        free(g_file_cache.data);
        g_file_cache.path = cache_path;
        g_file_cache.size = file_size;
        g_file_cache.mtime = mtime;
        g_file_cache.data = cache_copy;
        g_file_cache.data_size = size;
        nv_rwlock_write_unlock(&g_file_cache_lock);
    }
    else {
        free(cache_path);
        free(cache_copy);
    }
}

static int nv_input_size_ok(uint64_t size)
{
    if (size == 0u || size > (uint64_t)SIZE_MAX) {
        return 0;
    }
#if NV_ROUTER_MAX_INPUT_BYTES
    if (size > (uint64_t)NV_ROUTER_MAX_INPUT_BYTES) {
        return 0;
    }
#endif
    return 1;
}

/*
 * Reads a whole file before dispatch. The format ABI accepts raw bytes, so
 * codecs never open a path by name. This keeps UTF-8 host paths working on
 * Windows, where narrow fopen would use the active code page.
 */
static int read_file_bytes(const char *path, nv_file_bytes *out)
{
    uint64_t file_size = 0u;
    int64_t mtime = 0;
    int have_stamp;

    out->data = NULL;
    out->size = 0u;
    out->cached = 0;
    out->owned = 0;

    have_stamp = file_stamp(path, &file_size, &mtime);
    if (have_stamp != 0) {
        nv_rwlock_read(&g_file_cache_lock);
        if (g_file_cache.data != NULL && g_file_cache.path != NULL &&
            strcmp(g_file_cache.path, path) == 0 &&
            g_file_cache.size == file_size &&
            g_file_cache.mtime == mtime) {
            out->data = g_file_cache.data;
            out->size = g_file_cache.data_size;
            out->cached = 1;
            return 1;
        }
        nv_rwlock_read_unlock(&g_file_cache_lock);
    }

#if defined(_WIN32)
    {
        int wide_length;
        wchar_t *wide_path;
        HANDLE file;
        uint8_t *buffer;
        uint64_t offset;
        DWORD chunk;
        DWORD bytes_read;

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

        file = CreateFileW(wide_path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
        free(wide_path);
        if (file == INVALID_HANDLE_VALUE) {
            return 0;
        }

        if (have_stamp == 0) {
            LARGE_INTEGER size;
            if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0) {
                CloseHandle(file);
                return 0;
            }
            file_size = (uint64_t)size.QuadPart;
        }
        if (!nv_input_size_ok(file_size)) {
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
            cache_store(path, buffer, (size_t)file_size, file_size, mtime);
        }

        out->data = buffer;
        out->size = (size_t)file_size;
        out->owned = 1;
        return 1;
    }
#else
    {
        int fd;
        struct stat info;
        uint8_t *buffer;
        size_t offset = 0u;

        fd = open(path, O_RDONLY | O_CLOEXEC);
        if (fd < 0) {
            return 0;
        }
        if (fstat(fd, &info) != 0 || info.st_size <= 0) {
            close(fd);
            return 0;
        }
        file_size = (uint64_t)info.st_size;
        if (!nv_input_size_ok(file_size)) {
            close(fd);
            return 0;
        }

        buffer = (uint8_t *)malloc((size_t)file_size);
        if (buffer == NULL) {
            close(fd);
            return 0;
        }

        while (offset < (size_t)file_size) {
            ssize_t got = read(fd, buffer + offset, (size_t)file_size - offset);
            if (got < 0) {
                if (errno == EINTR) {
                    continue;
                }
                free(buffer);
                close(fd);
                return 0;
            }
            if (got == 0) {
                break;
            }
            offset += (size_t)got;
        }
        close(fd);

        if (offset != (size_t)file_size) {
            free(buffer);
            return 0;
        }
        if (have_stamp != 0) {
            cache_store(path, buffer, (size_t)file_size, file_size, mtime);
        }

        out->data = buffer;
        out->size = (size_t)file_size;
        out->owned = 1;
        return 1;
    }
#endif
}

/*
 * Reads an open descriptor from the start without moving its file offset.
 * ParcelFileDescriptor-backed documents have no usable path, so the host
 * passes the descriptor instead.
 */
static int read_fd_bytes(int fd, nv_file_bytes *out)
{
    out->data = NULL;
    out->size = 0u;
    out->cached = 0;
    out->owned = 0;

#if defined(_WIN32)
    (void)fd;
    return 0;
#else
    {
        struct stat info;
        uint64_t file_size;
        uint8_t *buffer;
        size_t offset = 0u;

        if (fd < 0 || fstat(fd, &info) != 0 || info.st_size <= 0) {
            return 0;
        }
        file_size = (uint64_t)info.st_size;
        if (!nv_input_size_ok(file_size)) {
            return 0;
        }

        buffer = (uint8_t *)malloc((size_t)file_size);
        if (buffer == NULL) {
            return 0;
        }

        while (offset < (size_t)file_size) {
            ssize_t got = pread(fd, buffer + offset, (size_t)file_size - offset, (off_t)offset);
            if (got < 0) {
                if (errno == EINTR) {
                    continue;
                }
                free(buffer);
                return 0;
            }
            if (got == 0) {
                break;
            }
            offset += (size_t)got;
        }

        if (offset != (size_t)file_size) {
            free(buffer);
            return 0;
        }

        out->data = buffer;
        out->size = (size_t)file_size;
        out->owned = 1;
        return 1;
    }
#endif
}

#if defined(_WIN32)
typedef const wchar_t *nv_module_path;

static nv_module nv_module_open(nv_module_path path)
{
    return LoadLibraryW(path);
}

static void *nv_module_symbol(nv_module module, const char *name)
{
    return (void *)GetProcAddress(module, name);
}

static void nv_module_close(nv_module module)
{
    FreeLibrary(module);
}
#else
typedef const char *nv_module_path;

static nv_module nv_module_open(nv_module_path path)
{
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
}

static void *nv_module_symbol(nv_module module, const char *name)
{
    return dlsym(module, name);
}

static void nv_module_close(nv_module module)
{
    dlclose(module);
}
#endif

/* Loads one format library and appends it to the context. */
static void load_format_library(nv_router_context *context, nv_module_path full_path)
{
    nv_format_entry entry;
    fn_abi_version abi_version;
    fn_query query;
    fn_decode decode;
    fn_encode encode;
    fn_image_free image_free;
    fn_buffer_free buffer_free;
    nv_format_info info;

    memset(&entry, 0, sizeof(entry));

    entry.module = nv_module_open(full_path);
    if (entry.module == NULL) {
        return;
    }

    abi_version = (fn_abi_version)nv_module_symbol(
        entry.module, "nv_format_abi_version");
    query = (fn_query)nv_module_symbol(entry.module, "nv_format_query");
    decode = (fn_decode)nv_module_symbol(entry.module, "nv_format_decode");
    encode = (fn_encode)nv_module_symbol(entry.module, "nv_format_encode");
    image_free = (fn_image_free)nv_module_symbol(entry.module, "nv_format_image_free");
    buffer_free = (fn_buffer_free)nv_module_symbol(entry.module, "nv_format_buffer_free");

    if (abi_version == NULL || query == NULL || decode == NULL || image_free == NULL) {
        context->abi_mismatch_count++;
        nv_log("nv_router: skipped a format library with missing exports");
        nv_module_close(entry.module);
        return;
    }
    if (strcmp(abi_version(), NV_FORMAT_ABI_VERSION) != 0) {
        context->abi_mismatch_count++;
        nv_log("nv_router: skipped a format library with an ABI mismatch");
        nv_module_close(entry.module);
        return;
    }

    memset(&info, 0, sizeof(info));
    info.struct_size = (uint32_t)sizeof(info);
    if (query(&info) != NV_FORMAT_OK) {
        nv_module_close(entry.module);
        return;
    }

    entry.decode = decode;
    entry.encode = encode;
    entry.image_free = image_free;
    entry.buffer_free = buffer_free;
    copy_str(entry.format_id, sizeof(entry.format_id), info.format_id);
    copy_str(entry.extensions, sizeof(entry.extensions), info.extensions);
    entry.capabilities = info.capabilities;

    if (add_entry(context, &entry) == 0) {
        nv_module_close(entry.module);
        return;
    }
    nv_log_format("nv_router: loaded %s", entry.format_id);
}

#if defined(_WIN32)
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
#else
/* Only files named like <format>_codec.so are candidate format libraries. */
static int is_codec_so(const char *name)
{
    static const char suffix[] = "_codec.so";
    const size_t suffix_length = sizeof(suffix) - 1u;
    size_t name_length = strlen(name);

    if (name_length <= suffix_length) {
        return 0;
    }
    return strcmp(name + name_length - suffix_length, suffix) == 0;
}

/* Scans the native library directory for format codec libraries. */
static void scan_directory(nv_router_context *context, const char *directory)
{
    DIR *dir;
    struct dirent *entry;
    size_t directory_length;

    if (directory == NULL) {
        return;
    }
    dir = opendir(directory);
    if (dir == NULL) {
        nv_log_format("nv_router: cannot open codec directory %s", directory);
        return;
    }

    directory_length = strlen(directory);
    while ((entry = readdir(dir)) != NULL) {
        const char *name = entry->d_name;
        size_t name_length;
        size_t separator_length;
        char *full_path;

        if (name[0] == '.') {
            continue;
        }
        if (!is_codec_so(name)) {
            continue;
        }

        name_length = strlen(name);
        separator_length = (directory_length > 0u &&
                            directory[directory_length - 1u] == '/') ? 0u : 1u;
        full_path = (char *)malloc(directory_length + separator_length + name_length + 1u);
        if (full_path == NULL) {
            continue;
        }
        memcpy(full_path, directory, directory_length);
        if (separator_length != 0u) {
            full_path[directory_length] = '/';
        }
        memcpy(full_path + directory_length + separator_length, name, name_length + 1u);
        load_format_library(context, full_path);
        free(full_path);
    }

    closedir(dir);
}
#endif

/* Resolves the router's own directory into a heap buffer owned by the caller. */
#if defined(_WIN32)
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
#else
static char *default_directory(void)
{
    Dl_info info;
    const char *module_path;
    const char *last_separator;
    size_t directory_length;
    char *directory;

    memset(&info, 0, sizeof(info));
    if (dladdr((void *)&nv_router_abi_version, &info) == 0 ||
        info.dli_fname == NULL) {
        return NULL;
    }

    module_path = info.dli_fname;
    last_separator = strrchr(module_path, '/');
    if (last_separator == NULL) {
        return NULL;
    }

    directory_length = (size_t)(last_separator - module_path);
    directory = (char *)malloc(directory_length + 1u);
    if (directory != NULL) {
        memcpy(directory, module_path, directory_length);
        directory[directory_length] = '\0';
    }
    return directory;
}
#endif

const char *nv_router_abi_version(void)
{
    return NV_ROUTER_ABI_VERSION;
}

nv_router_status nv_router_create(const char *codec_dir, nv_router_context **out_context)
{
#if defined(_WIN32)
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
#else
    nv_router_context *context;
    char *directory = NULL;

    if (out_context == NULL) {
        return NV_ROUTER_ERR_INVALID_ARGUMENT;
    }
    *out_context = NULL;

    context = (nv_router_context *)calloc(1u, sizeof(nv_router_context));
    if (context == NULL) {
        return NV_ROUTER_ERR_OUT_OF_MEMORY;
    }

    if (codec_dir != NULL && codec_dir[0] != '\0') {
        size_t length = strlen(codec_dir) + 1u;

        directory = (char *)malloc(length);
        if (directory == NULL) {
            free(context);
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
        }
        memcpy(directory, codec_dir, length);
    }
    else {
        directory = default_directory();
        if (directory == NULL) {
            free(context);
            return NV_ROUTER_ERR_IO;
        }
    }

    nv_log_format("nv_router: scanning %s", directory);
    scan_directory(context, directory);
    free(directory);

    *out_context = context;
    return NV_ROUTER_OK;
#endif
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

        if (owner_add(image.pixels, entry->image_free) == 0) {
            entry->image_free(&image);
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
        out_image->source_width = image.source_width;
        out_image->source_height = image.source_height;
        return NV_ROUTER_OK;
    }

    if (last_status == NV_ROUTER_ERR_UNSUPPORTED_FORMAT &&
        context->abi_mismatch_count > 0u) {
        return NV_ROUTER_ERR_ABI_MISMATCH;
    }
    return last_status;
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

    if (!read_file_bytes(path, &file)) {
        return NV_ROUTER_ERR_IO;
    }

    status = dispatch_decode(
        context, file.data, file.size, path, extension,
        desired_pixel_format, frame_index, max_dimension, out_image);
    release_file_bytes(&file);
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

    if (!read_fd_bytes(fd, &file)) {
        return NV_ROUTER_ERR_IO;
    }

    status = dispatch_decode(
        context, file.data, file.size, NULL, "",
        desired_pixel_format, frame_index, max_dimension, out_image);
    release_file_bytes(&file);
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

/* Writes bytes to a file, replacing an existing one. The path is UTF-8. */
static int write_file_bytes(const char *path, const uint8_t *data, size_t size)
{
#if defined(_WIN32)
    int wide_length;
    wchar_t *wide_path;
    HANDLE file;
    size_t offset = 0u;
    int written_ok = 1;

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

    file = CreateFileW(wide_path, GENERIC_WRITE, 0u, NULL, CREATE_ALWAYS,
                       FILE_ATTRIBUTE_NORMAL, NULL);
    if (file == INVALID_HANDLE_VALUE) {
        free(wide_path);
        return 0;
    }

    while (offset < size) {
        size_t remaining = size - offset;
        DWORD chunk = remaining > (size_t)INT_MAX ? (DWORD)INT_MAX : (DWORD)remaining;
        DWORD written = 0u;
        if (!WriteFile(file, data + offset, chunk, &written, NULL) || written == 0u) {
            written_ok = 0;
            break;
        }
        offset += (size_t)written;
    }

    CloseHandle(file);

    if (written_ok == 0) {
        /* Do not leave a truncated image behind after a failed write. */
        DeleteFileW(wide_path);
    }
    free(wide_path);
    return written_ok;
#else
    int fd;
    size_t offset = 0u;
    int written_ok = 1;

    fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0644);
    if (fd < 0) {
        return 0;
    }

    while (offset < size) {
        ssize_t written = write(fd, data + offset, size - offset);
        if (written < 0) {
            if (errno == EINTR) {
                continue;
            }
            written_ok = 0;
            break;
        }
        if (written == 0) {
            written_ok = 0;
            break;
        }
        offset += (size_t)written;
    }

    close(fd);

    if (written_ok == 0) {
        /* Do not leave a truncated image behind after a failed write. */
        unlink(path);
    }
    return written_ok;
#endif
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
            int written = write_file_bytes(path, buffer.data, buffer.size);

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
    image->source_width = 0;
    image->source_height = 0;
}
