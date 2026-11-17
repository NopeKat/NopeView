#include "nv_files_abi.h"

#include <windows.h>

#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

struct nv_file_list {
    char **paths;
    uint32_t count;
    uint32_t capacity;
};

static wchar_t *nv_utf8_to_wide(const char *text)
{
    int length;
    wchar_t *wide;

    if (text == NULL) {
        return NULL;
    }

    length = MultiByteToWideChar(CP_UTF8, 0, text, -1, NULL, 0);
    if (length <= 0) {
        return NULL;
    }

    wide = (wchar_t *)malloc((size_t)length * sizeof(wchar_t));
    if (wide == NULL) {
        return NULL;
    }

    if (MultiByteToWideChar(CP_UTF8, 0, text, -1, wide, length) <= 0) {
        free(wide);
        return NULL;
    }

    return wide;
}

static char *nv_wide_to_utf8(const wchar_t *text)
{
    int length;
    char *utf8;

    if (text == NULL) {
        return NULL;
    }

    length = WideCharToMultiByte(CP_UTF8, 0, text, -1, NULL, 0, NULL, NULL);
    if (length <= 0) {
        return NULL;
    }

    utf8 = (char *)malloc((size_t)length);
    if (utf8 == NULL) {
        return NULL;
    }

    if (WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, length, NULL, NULL) <= 0) {
        free(utf8);
        return NULL;
    }

    return utf8;
}

static int nv_list_push(nv_file_list *list, char *path)
{
    char **grown;
    uint32_t next;

    if (list->count == list->capacity) {
        next = list->capacity == 0 ? 16u : list->capacity * 2u;
        if (next <= list->capacity) {
            return 0;
        }

        grown = (char **)realloc(list->paths, (size_t)next * sizeof(char *));
        if (grown == NULL) {
            return 0;
        }

        list->paths = grown;
        list->capacity = next;
    }

    list->paths[list->count] = path;
    list->count++;
    return 1;
}

static int nv_extension_matches(const char *name, const char *extensions)
{
    const char *dot;
    const char *start;
    size_t wanted;

    if (name == NULL || extensions == NULL) {
        return 0;
    }

    dot = strrchr(name, '.');
    if (dot == NULL || dot[1] == '\0') {
        return 0;
    }

    wanted = strlen(dot + 1);
    start = extensions;
    while (*start != '\0') {
        const char *end = strchr(start, ',');
        size_t length = end == NULL ? strlen(start) : (size_t)(end - start);

        while (length > 0 && (*start == ' ' || *start == '\t')) {
            start++;
            length--;
        }
        while (length > 0 && (start[length - 1] == ' ' || start[length - 1] == '\t')) {
            length--;
        }
        if (length > 0 && start[0] == '.') {
            start++;
            length--;
        }

        if (length == wanted && _strnicmp(start, dot + 1, length) == 0) {
            return 1;
        }

        if (end == NULL) {
            break;
        }
        start = end + 1;
    }

    return 0;
}

static int nv_compare_paths(const void *left, const void *right)
{
    const char *const *a = (const char *const *)left;
    const char *const *b = (const char *const *)right;
    return _stricmp(*a, *b);
}

const char *nv_files_abi_version(void)
{
    return NV_FILES_ABI_VERSION;
}

nv_files_status nv_file_list_create(
    const char *directory,
    const char *extensions,
    nv_file_list **out_list)
{
    nv_files_status status = NV_FILES_OK;
    nv_file_list *list = NULL;
    wchar_t *wide_directory = NULL;
    wchar_t *pattern = NULL;
    HANDLE find = INVALID_HANDLE_VALUE;
    WIN32_FIND_DATAW data;
    size_t directory_length;
    size_t pattern_length;
    int has_separator;

    if (out_list == NULL || directory == NULL) {
        return NV_FILES_ERR_INVALID_ARGUMENT;
    }
    *out_list = NULL;

    if (extensions == NULL) {
        extensions = "";
    }

    wide_directory = nv_utf8_to_wide(directory);
    if (wide_directory == NULL) {
        return NV_FILES_ERR_OUT_OF_MEMORY;
    }

    directory_length = wcslen(wide_directory);
    has_separator = directory_length > 0 &&
        (wide_directory[directory_length - 1] == L'\\' ||
         wide_directory[directory_length - 1] == L'/');
    pattern_length = directory_length + (has_separator ? 1u : 2u);

    pattern = (wchar_t *)malloc((pattern_length + 1u) * sizeof(wchar_t));
    if (pattern == NULL) {
        status = NV_FILES_ERR_OUT_OF_MEMORY;
        goto cleanup;
    }

    if (swprintf_s(pattern, pattern_length + 1u, L"%ls%ls*",
                   wide_directory, has_separator ? L"" : L"\\") < 0) {
        status = NV_FILES_ERR_INVALID_ARGUMENT;
        goto cleanup;
    }

    list = (nv_file_list *)calloc(1, sizeof(*list));
    if (list == NULL) {
        status = NV_FILES_ERR_OUT_OF_MEMORY;
        goto cleanup;
    }

    find = FindFirstFileW(pattern, &data);
    if (find == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND) {
            status = NV_FILES_OK;
            goto cleanup;
        }
        status = NV_FILES_ERR_IO;
        goto cleanup;
    }

    do {
        char *name_utf8;
        wchar_t *full_path;
        char *full_utf8;
        size_t name_length;
        size_t full_length;
        size_t offset;

        if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) {
            continue;
        }

        name_utf8 = nv_wide_to_utf8(data.cFileName);
        if (name_utf8 == NULL) {
            status = NV_FILES_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }

        if (!nv_extension_matches(name_utf8, extensions)) {
            free(name_utf8);
            continue;
        }

        name_length = wcslen(data.cFileName);
        full_length = directory_length + (has_separator ? 0u : 1u) + name_length + 1u;
        full_path = (wchar_t *)malloc(full_length * sizeof(wchar_t));
        if (full_path == NULL) {
            free(name_utf8);
            status = NV_FILES_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }

        memcpy(full_path, wide_directory, directory_length * sizeof(wchar_t));
        offset = directory_length;
        if (!has_separator) {
            full_path[offset] = L'\\';
            offset++;
        }
        memcpy(full_path + offset, data.cFileName, (name_length + 1u) * sizeof(wchar_t));

        full_utf8 = nv_wide_to_utf8(full_path);
        free(full_path);
        free(name_utf8);
        if (full_utf8 == NULL) {
            status = NV_FILES_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }

        if (!nv_list_push(list, full_utf8)) {
            free(full_utf8);
            status = NV_FILES_ERR_OUT_OF_MEMORY;
            goto cleanup;
        }
    } while (FindNextFileW(find, &data));

    if (GetLastError() != ERROR_NO_MORE_FILES) {
        status = NV_FILES_ERR_IO;
        goto cleanup;
    }

cleanup:
    if (find != INVALID_HANDLE_VALUE) {
        FindClose(find);
    }
    free(pattern);
    free(wide_directory);

    if (status != NV_FILES_OK) {
        nv_file_list_destroy(list);
        return status;
    }

    if (list->count > 1u) {
        qsort(list->paths, list->count, sizeof(char *), nv_compare_paths);
    }

    *out_list = list;
    return NV_FILES_OK;
}

void nv_file_list_destroy(nv_file_list *list)
{
    uint32_t index;

    if (list == NULL) {
        return;
    }

    for (index = 0; index < list->count; index++) {
        free(list->paths[index]);
    }
    free(list->paths);
    free(list);
}

uint32_t nv_file_list_count(const nv_file_list *list)
{
    return list == NULL ? 0u : list->count;
}

const char *nv_file_list_path(const nv_file_list *list, uint32_t index)
{
    if (list == NULL || index >= list->count) {
        return NULL;
    }
    return list->paths[index];
}

int32_t nv_file_list_index_of(const nv_file_list *list, const char *path)
{
    uint32_t index;

    if (list == NULL || path == NULL) {
        return -1;
    }

    for (index = 0; index < list->count; index++) {
        if (_stricmp(list->paths[index], path) == 0) {
            return (int32_t)index;
        }
    }

    return -1;
}
