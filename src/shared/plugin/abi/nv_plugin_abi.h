// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

/*
 * nv_plugin_abi.h - Boundary C: plugin ABI.
 *
 * The contract between the host application and an application plugin.
 * A plugin is a user-facing feature extension (for example format conversion
 * or batch rename), not a codec. See src/shared/plugin/README.md.
 *
 * Design rules for this boundary:
 *  - Plain C calling convention (C ABI).
 *  - Each plugin is an independent dynamic library exposing the entry points
 *    declared here.
 *  - Structs carry a size field so fields can be appended safely.
 *  - The host passes its own services (logging, codec access) as a table of
 *    function pointers, so a plugin never links against the app directly.
 *  - Memory ownership: strings and buffers returned to the host are released
 *    through the matching nv_plugin_*_free function.
 */

#ifndef NV_PLUGIN_ABI_H
#define NV_PLUGIN_ABI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(NV_PLUGIN_ABI_EXPORTS)
#define NV_PLUGIN_API __declspec(dllexport)
#elif defined(_WIN32)
#define NV_PLUGIN_API __declspec(dllimport)
#else
#define NV_PLUGIN_API
#endif

/*
 * ABI version in x.y.z.n form. Starts at 0.0.0.0. Unless the project owner
 * explicitly requests otherwise, only the fourth segment is incremented.
 * Independent from the router ABI and the format ABI.
 */
#define NV_PLUGIN_ABI_VERSION "0.0.0.0"

typedef enum nv_plugin_status {
    NV_PLUGIN_OK = 0,
    NV_PLUGIN_ERR_INVALID_ARGUMENT = -1,
    NV_PLUGIN_ERR_NOT_INITIALIZED = -2,
    NV_PLUGIN_ERR_UNSUPPORTED = -3,
    NV_PLUGIN_ERR_FAILED = -4,
    NV_PLUGIN_ERR_OUT_OF_MEMORY = -5,
    NV_PLUGIN_ERR_ABI_MISMATCH = -6
} nv_plugin_status;

/*
 * Static plugin description.
 * out_descriptor->struct_size must be set by the caller before the call.
 */
typedef struct nv_plugin_descriptor {
    uint32_t struct_size;
    const char *plugin_id;     /* Stable identifier, for example "convert". */
    const char *display_name;  /* Human-readable, UTF-8. */
    const char *version;       /* Plugin version, x.y.z.n form. */
    const char *description;   /* Short description, UTF-8. */
} nv_plugin_descriptor;

/*
 * Services the host offers to plugins. A plugin calls these instead of
 * linking directly against the application.
 * Host fills this table; the plugin must not modify it.
 */
typedef struct nv_plugin_host_services {
    uint32_t struct_size;
    void *host_context;

    /* Writes a log line at the given level (0 = info, 1 = warning, 2 = error). */
    void (*log)(void *host_context, int32_t level, const char *message);

    /*
     * Decodes a file through the host's codec router.
     * The plugin treats the returned buffer as read-only and releases it with
     * the release_decoded_image function below.
     */
    nv_plugin_status (*decode_file)(
        void *host_context,
        const char *path,
        uint8_t **out_pixels,
        size_t *out_pixels_size,
        int32_t *out_width,
        int32_t *out_height,
        int32_t *out_stride);

    /* Releases a buffer produced by decode_file. */
    void (*release_decoded_image)(void *host_context, uint8_t *pixels);
} nv_plugin_host_services;

/*
 * Plugin entry points. Every plugin library must export all of these.
 */

/* Returns the plugin ABI version string this library was built against. */
NV_PLUGIN_API const char *nv_plugin_abi_version(void);

/* Reports the plugin's static description. */
NV_PLUGIN_API nv_plugin_status nv_plugin_query(nv_plugin_descriptor *out_descriptor);

/*
 * Initializes the plugin with host services.
 * Called once after the library is loaded, before nv_plugin_execute.
 */
NV_PLUGIN_API nv_plugin_status nv_plugin_initialize(const nv_plugin_host_services *services);

/*
 * Runs the plugin's action.
 * command is a plugin-defined verb, UTF-8. args_json is an optional JSON object
 * with command parameters, UTF-8. The plugin returns a JSON result string that
 * the host releases with nv_plugin_free_string.
 */
NV_PLUGIN_API nv_plugin_status nv_plugin_execute(
    const char *command,
    const char *args_json,
    char **out_result_json);

/* Releases a string produced by nv_plugin_execute. */
NV_PLUGIN_API void nv_plugin_free_string(char *value);

/* Releases resources held by the plugin. Called once before unload. */
NV_PLUGIN_API void nv_plugin_shutdown(void);

#ifdef __cplusplus
}
#endif

#endif /* NV_PLUGIN_ABI_H */
