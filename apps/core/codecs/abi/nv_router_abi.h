/*
 * nv_router_abi.h - Boundary A: router ABI.
 *
 * The contract between a platform application (or the CLI) and the codec
 * router. The host never talks to a format codec directly; it goes through
 * this facade, which the router implements.
 *
 * Naming: this header is named after the side that provides the interface.
 * The router implements these entry points; the host calls them.
 *
 * Design rules for this boundary:
 *  - Plain C calling convention (C ABI).
 *  - Opaque context handle: the host sees only nv_router_context, never router
 *    internals.
 *  - Structs carry a size field so fields can be appended safely.
 *  - Memory ownership: buffers returned to the host are released through the
 *    matching nv_router_*_free function.
 */

#ifndef NV_ROUTER_ABI_H
#define NV_ROUTER_ABI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(NV_ROUTER_ABI_EXPORTS)
#define NV_ROUTER_API __declspec(dllexport)
#elif defined(_WIN32)
#define NV_ROUTER_API __declspec(dllimport)
#else
#define NV_ROUTER_API
#endif

/*
 * ABI version in x.y.z.n form. Starts at 0.0.0.0. Unless the project owner
 * explicitly requests otherwise, only the fourth segment is incremented.
 * Independent from the format ABI and the plugin ABI.
 */
#define NV_ROUTER_ABI_VERSION "0.0.0.0"

typedef enum nv_router_status {
    NV_ROUTER_OK = 0,
    NV_ROUTER_ERR_INVALID_ARGUMENT = -1,
    NV_ROUTER_ERR_NOT_FOUND = -2,
    NV_ROUTER_ERR_UNSUPPORTED_FORMAT = -3,
    NV_ROUTER_ERR_DECODE_FAILED = -4,
    NV_ROUTER_ERR_OUT_OF_MEMORY = -5,
    NV_ROUTER_ERR_IO = -6,
    NV_ROUTER_ERR_ABI_MISMATCH = -7
} nv_router_status;

/* Opaque router context owned by the host. */
typedef struct nv_router_context nv_router_context;

typedef enum nv_router_pixel_format {
    NV_ROUTER_PIXEL_UNKNOWN = 0,
    NV_ROUTER_PIXEL_RGBA8 = 1,
    NV_ROUTER_PIXEL_BGRA8 = 2,
    NV_ROUTER_PIXEL_RGB8 = 3,
    NV_ROUTER_PIXEL_BGR8 = 4,
    NV_ROUTER_PIXEL_GRAY8 = 5
} nv_router_pixel_format;

/*
 * Bitmap handed back to the host after a decode.
 * pixels is owned by the router and released with nv_router_image_free.
 */
typedef struct nv_router_image {
    uint32_t struct_size;
    int32_t width;
    int32_t height;
    int32_t stride;
    nv_router_pixel_format pixel_format;
    uint8_t *pixels;
    size_t pixels_size;
} nv_router_image;

/*
 * A format the router can handle. Returned by nv_router_enum_formats so the
 * host can build "open file" filters without hard-coding format knowledge.
 */
typedef struct nv_router_format {
    uint32_t struct_size;
    const char *format_id;   /* For example "png". Owned by the router. */
    const char *extensions;  /* Comma-separated, for example "png". */
    uint32_t can_decode;
    uint32_t can_encode;
    uint32_t can_animate;    /* 1 when the format supports animated frames. */
} nv_router_format;

/* Returns the router ABI version string. */
NV_ROUTER_API const char *nv_router_abi_version(void);

/*
 * Creates a router context. codec_dir is the directory that holds the format
 * codec libraries, UTF-8. Passing NULL uses the default next to the executable.
 */
NV_ROUTER_API nv_router_status nv_router_create(const char *codec_dir, nv_router_context **out_context);

/* Destroys a router context and releases everything it owns. */
NV_ROUTER_API void nv_router_destroy(nv_router_context *context);

/*
 * Enumerates available formats.
 * Pass index starting at 0 and increase until NV_ROUTER_ERR_NOT_FOUND is returned.
 * out_format->struct_size must be set by the caller before the call.
 */
NV_ROUTER_API nv_router_status nv_router_enum_formats(
    nv_router_context *context,
    uint32_t index,
    nv_router_format *out_format);

/* Decodes a file into a router image. Release the result with nv_router_image_free. */
NV_ROUTER_API nv_router_status nv_router_decode_file(
    nv_router_context *context,
    const char *path,
    nv_router_pixel_format desired_pixel_format,
    uint32_t frame_index,
    nv_router_image *out_image);

/* Releases pixels held by an image produced by nv_router_decode_file. */
NV_ROUTER_API void nv_router_image_free(nv_router_image *image);

#ifdef __cplusplus
}
#endif

#endif /* NV_ROUTER_ABI_H */
