/*
 * Codec router library loader.
 *
 * Finds the directory that holds the format codecs, then loads every library
 * there and asks it for its capabilities through the format ABI. A library
 * that is missing an export, or whose ABI does not match this router, is
 * skipped and counted so the host can report an ABI mismatch later.
 */

#include "router_internal.h"

/* Appends one loaded codec to the context, growing the list as needed. */
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
    nv_copy_str(entry.format_id, sizeof(entry.format_id), info.format_id);
    nv_copy_str(entry.extensions, sizeof(entry.extensions), info.extensions);
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
            (LPCWSTR)&nv_router_abi_version,
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

nv_router_status nv_loader_start(nv_router_context *context, const char *codec_dir)
{
#if defined(_WIN32)
    wchar_t *directory = NULL;
    int length;

    if (codec_dir != NULL && codec_dir[0] != '\0') {
        length = MultiByteToWideChar(CP_UTF8, 0u, codec_dir, -1, NULL, 0);
        if (length <= 0) {
            return NV_ROUTER_ERR_INVALID_ARGUMENT;
        }
        directory = (wchar_t *)malloc((size_t)length * sizeof(wchar_t));
        if (directory == NULL) {
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
        }
        if (MultiByteToWideChar(CP_UTF8, 0u, codec_dir, -1, directory, length) <= 0) {
            free(directory);
            return NV_ROUTER_ERR_INVALID_ARGUMENT;
        }
    }
    else {
        directory = default_directory();
        if (directory == NULL) {
            return NV_ROUTER_ERR_IO;
        }
    }

    scan_directory(context, directory);
    free(directory);
    return NV_ROUTER_OK;
#else
    char *directory = NULL;

    if (codec_dir != NULL && codec_dir[0] != '\0') {
        size_t length = strlen(codec_dir) + 1u;

        directory = (char *)malloc(length);
        if (directory == NULL) {
            return NV_ROUTER_ERR_OUT_OF_MEMORY;
        }
        memcpy(directory, codec_dir, length);
    }
    else {
        directory = default_directory();
        if (directory == NULL) {
            return NV_ROUTER_ERR_IO;
        }
    }

    nv_log_format("nv_router: scanning %s", directory);
    scan_directory(context, directory);
    free(directory);
    return NV_ROUTER_OK;
#endif
}
