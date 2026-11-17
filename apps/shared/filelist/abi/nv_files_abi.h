/*
 * nv_files_abi.h - Shared file list service.
 *
 * A small core service that enumerates and sorts the files in one directory.
 * The platform application asks for the supported extensions; the core owns
 * the platform-specific directory walk and returns UTF-8 paths.
 *
 * Design rules:
 *  - Plain C calling convention (C ABI).
 *  - Opaque list handle: the caller sees only nv_file_list.
 *  - UTF-8 strings everywhere.
 *  - Paths returned by the list are owned by the list and released with
 *    nv_file_list_destroy.
 */

#ifndef NV_FILES_ABI_H
#define NV_FILES_ABI_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(NV_FILES_ABI_EXPORTS)
#define NV_FILES_API __declspec(dllexport)
#elif defined(_WIN32)
#define NV_FILES_API __declspec(dllimport)
#else
#define NV_FILES_API
#endif

/*
 * ABI version in x.y.z.n form. Starts at 0.0.0.0. Unless the project owner
 * explicitly requests otherwise, only the fourth segment is incremented.
 * Independent from the router, format, and plugin ABIs.
 */
#define NV_FILES_ABI_VERSION "0.0.0.0"

typedef enum nv_files_status {
    NV_FILES_OK = 0,
    NV_FILES_ERR_INVALID_ARGUMENT = -1,
    NV_FILES_ERR_NOT_FOUND = -2,
    NV_FILES_ERR_OUT_OF_MEMORY = -3,
    NV_FILES_ERR_IO = -4
} nv_files_status;

/* Opaque file list owned by the caller. */
typedef struct nv_file_list nv_file_list;

/* Returns the files ABI version string. */
NV_FILES_API const char *nv_files_abi_version(void);

/*
 * Enumerates files in directory whose extension is in extensions.
 * extensions is a comma-separated UTF-8 list, for example "png,jpg,jpeg".
 * Leading dots are optional. Only the top-level directory is scanned.
 * Paths are sorted case-insensitively and returned as full UTF-8 paths.
 */
NV_FILES_API nv_files_status nv_file_list_create(
    const char *directory,
    const char *extensions,
    nv_file_list **out_list);

/* Destroys a list and releases every path it owns. */
NV_FILES_API void nv_file_list_destroy(nv_file_list *list);

/* Number of paths in the list. Returns 0 for a null list. */
NV_FILES_API uint32_t nv_file_list_count(const nv_file_list *list);

/*
 * Returns the path at index, or NULL when the list is null or index is out
 * of range. The string is owned by the list.
 */
NV_FILES_API const char *nv_file_list_path(const nv_file_list *list, uint32_t index);

/*
 * Returns the index of path, or -1 when it is not in the list. Comparison is
 * case-insensitive.
 */
NV_FILES_API int32_t nv_file_list_index_of(const nv_file_list *list, const char *path);

#ifdef __cplusplus
}
#endif

#endif /* NV_FILES_ABI_H */
