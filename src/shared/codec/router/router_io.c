// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * Codec router file I/O.
 *
 * The format ABI accepts raw bytes, so codecs never open a path by name. This
 * unit turns a UTF-8 path or an open descriptor into those bytes, writes the
 * result of an encode, and releases what it handed out.
 */

#include "router_internal.h"

#ifndef NV_ROUTER_MAX_INPUT_BYTES
#define NV_ROUTER_MAX_INPUT_BYTES 0
#endif

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

static int input_size_ok(uint64_t size)
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
 * Reads a whole file before dispatch. This keeps UTF-8 host paths working on
 * Windows, where narrow fopen would use the active code page.
 */
int nv_io_read_file(nv_router_context *context, const char *path, nv_file_bytes *out)
{
    uint64_t file_size = 0u;
    int64_t mtime = 0;
    int have_stamp;

    out->data = NULL;
    out->size = 0u;
    out->cached = 0;
    out->owned = 0;

    have_stamp = file_stamp(path, &file_size, &mtime);
    if (have_stamp != 0 && nv_cache_lookup(context, path, file_size, mtime, out) != 0) {
        return 1;
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
        if (!input_size_ok(file_size)) {
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
            nv_cache_store(context, path, buffer, (size_t)file_size, file_size, mtime);
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
        if (!input_size_ok(file_size)) {
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
            nv_cache_store(context, path, buffer, (size_t)file_size, file_size, mtime);
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
int nv_io_read_fd(int fd, nv_file_bytes *out)
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
        if (!input_size_ok(file_size)) {
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

void nv_io_release(nv_router_context *context, nv_file_bytes *bytes)
{
    if (bytes->cached != 0) {
        nv_cache_release(context);
    }
    else if (bytes->owned != 0) {
        free((void *)bytes->data);
    }
    bytes->data = NULL;
    bytes->size = 0u;
    bytes->cached = 0;
    bytes->owned = 0;
}

/* Writes bytes to a file, replacing an existing one. The path is UTF-8. */
int nv_io_write_file(const char *path, const uint8_t *data, size_t size)
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
