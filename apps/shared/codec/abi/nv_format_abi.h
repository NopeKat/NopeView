/*
 * nv_format_abi.h - Boundary B: format ABI.
 *
 * The contract between the codec router (apps/shared/codec/router) and each
 * format codec (apps/shared/codec/formats/<format>). Every format codec is an
 * independent dynamic library that implements the entry points declared here.
 *
 * Naming: this header is named after the side that provides the interface.
 * Each format library implements these entry points; the router calls them.
 * A "format" is one codec for one file format, for example png or jpg.
 *
 * Design rules for this boundary:
 *  - Plain C calling convention (C ABI). No exceptions, no C++ objects.
 *  - Callers and callees must agree on the ABI version described below.
 *  - Structs carry a size field so fields can be appended without breaking
 *    older callers.
 *  - Memory ownership: whoever allocates, frees. Buffers returned by a format
 *    library must be released through the matching nv_format_*_free function
 *    from the same library instance that produced them.
 */

#ifndef NV_FORMAT_ABI_H
#define NV_FORMAT_ABI_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(NV_FORMAT_ABI_EXPORTS)
#define NV_FORMAT_API __declspec(dllexport)
#elif defined(_WIN32)
#define NV_FORMAT_API __declspec(dllimport)
#else
#define NV_FORMAT_API
#endif

/*
 * ABI version in x.y.z.n form. Starts at 0.0.0.0. Unless the project owner
 * explicitly requests otherwise, only the fourth segment is incremented.
 * Independent from the router ABI and the plugin ABI.
 */
#define NV_FORMAT_ABI_VERSION "0.0.0.2"

/* Status codes. Zero means success; any negative value means failure. */
typedef enum nv_format_status {
    NV_FORMAT_OK = 0,
    NV_FORMAT_ERR_INVALID_ARGUMENT = -1,
    NV_FORMAT_ERR_UNSUPPORTED = -2,
    NV_FORMAT_ERR_DECODE_FAILED = -3,
    NV_FORMAT_ERR_ENCODE_FAILED = -4,
    NV_FORMAT_ERR_OUT_OF_MEMORY = -5,
    NV_FORMAT_ERR_IO = -6,
    NV_FORMAT_ERR_ABI_MISMATCH = -7
} nv_format_status;

/* Pixel layout of decoded output. Formats report what they actually produce. */
typedef enum nv_format_pixel_format {
    NV_FORMAT_PIXEL_UNKNOWN = 0,
    NV_FORMAT_PIXEL_RGBA8 = 1,
    NV_FORMAT_PIXEL_BGRA8 = 2,
    NV_FORMAT_PIXEL_RGB8 = 3,
    NV_FORMAT_PIXEL_BGR8 = 4,
    NV_FORMAT_PIXEL_GRAY8 = 5
} nv_format_pixel_format;

/*
 * Capability flags reported by nv_format_query. A format may support decoding,
 * encoding, or both.
 */
#define NV_FORMAT_CAP_DECODE 0x00000001u
#define NV_FORMAT_CAP_ENCODE 0x00000002u
#define NV_FORMAT_CAP_ANIMATED 0x00000004u
#define NV_FORMAT_CAP_STREAMING 0x00000008u

/*
 * Static description of a format. Filled by nv_format_query.
 * struct_size must be initialized by the caller before the call.
 */
typedef struct nv_format_info {
    uint32_t struct_size;
    const char *name;          /* Human-readable name, UTF-8. */
    const char *format_id;     /* Stable identifier, for example "png". */
    const char *extensions;    /* Comma-separated extensions, for example "png". */
    uint32_t capabilities;     /* Bitmask of NV_FORMAT_CAP_*. */
} nv_format_info;

/*
 * A decoded image or frame.
 * pixels is owned by the format library; release it with nv_format_image_free.
 */
typedef struct nv_format_image {
    uint32_t struct_size;
    int32_t width;
    int32_t height;
    int32_t stride;            /* Bytes per row. */
    nv_format_pixel_format pixel_format;
    uint8_t *pixels;
    size_t pixels_size;        /* Total bytes in pixels. */
    uint32_t frame_count;      /* 1 for still images; total frames otherwise. */
    uint32_t frame_delay_ms;   /* Display duration for this frame, in milliseconds. */
    int32_t source_width;      /* Dimensions before max_dimension scaling. */
    int32_t source_height;     /* Equal to width/height when no scaling happened. */
} nv_format_image;

/*
 * Decode request. All fields are optional except struct_size.
 * desired_pixel_format of NV_FORMAT_PIXEL_UNKNOWN lets the format choose.
 * For animated formats, frame_index selects the frame (0 = first).
 * max_dimension limits the long edge of the decoded pixels; 0 means full size.
 * Codecs read it only when struct_size covers the field.
 */
typedef struct nv_format_decode_request {
    uint32_t struct_size;
    const uint8_t *data;       /* Encoded bytes. May be NULL when path is set. */
    size_t data_size;
    const char *path;          /* File path, UTF-8. May be NULL when data is set. */
    nv_format_pixel_format desired_pixel_format;
    uint32_t frame_index;
    int32_t max_dimension;
} nv_format_decode_request;

/*
 * Encode request. The format writes encoded bytes into a buffer it allocates;
 * release the buffer with nv_format_buffer_free.
 */
typedef struct nv_format_encode_request {
    uint32_t struct_size;
    const nv_format_image *image;
    int32_t quality;           /* Format-defined quality hint, 0 = default. */
} nv_format_encode_request;

typedef struct nv_format_buffer {
    uint32_t struct_size;
    uint8_t *data;
    size_t size;
} nv_format_buffer;

/* Returns the ABI version string this library was built against. */
NV_FORMAT_API const char *nv_format_abi_version(void);

/*
 * Reports the format's static description and capabilities.
 * out_info->struct_size must be set by the caller before the call.
 */
NV_FORMAT_API nv_format_status nv_format_query(nv_format_info *out_info);

/*
 * Decodes one image or frame.
 * On success, out_image holds pixels owned by the format library.
 */
NV_FORMAT_API nv_format_status nv_format_decode(
    const nv_format_decode_request *request,
    nv_format_image *out_image);

/*
 * Encodes an image into the format's native encoding.
 * On success, out_buffer holds bytes owned by the format library.
 */
NV_FORMAT_API nv_format_status nv_format_encode(
    const nv_format_encode_request *request,
    nv_format_buffer *out_buffer);

/* Releases pixels held by an image produced by nv_format_decode. */
NV_FORMAT_API void nv_format_image_free(nv_format_image *image);

/* Releases bytes held by a buffer produced by nv_format_encode. */
NV_FORMAT_API void nv_format_buffer_free(nv_format_buffer *buffer);

#ifdef __cplusplus
}
#endif

#endif /* NV_FORMAT_ABI_H */
