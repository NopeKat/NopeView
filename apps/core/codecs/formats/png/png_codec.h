#ifndef NV_PNG_CODEC_H
#define NV_PNG_CODEC_H

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32) && defined(NV_CODEC_BUILD_DLL)
#define NV_API __declspec(dllexport)
#elif defined(_WIN32)
#define NV_API __declspec(dllimport)
#else
#define NV_API
#endif

#define NV_PNG_OK 0
#define NV_PNG_ERR_INVALID_ARG (-1)
#define NV_PNG_ERR_DECODE (-2)
#define NV_PNG_ERR_OUT_OF_MEMORY (-3)

/* ABI version in x.y.z.n form. Starts at 0.0.0.0; by default only the fourth
   segment is incremented (0.0.0.n+1) unless explicitly requested otherwise. */
#define NV_PNG_VERSION "0.0.0.0"

typedef struct nv_image {
    int width;
    int height;
    int channels;
    unsigned char *pixels;
} nv_image;

/* Returns the ABI version as a static string in x.y.z.n form (for example "1.0.0.0").
   The returned pointer is owned by the codec and must not be freed. */
NV_API const char *nv_png_version(void);

/* Decodes a PNG file into RGBA pixels. On success, out_image owns a buffer
   that the caller must release with nv_png_image_free. */
NV_API int nv_png_decode_file(const char *path, nv_image *out_image);

/* Releases the pixel buffer owned by out_image and resets its fields. */
NV_API void nv_png_image_free(nv_image *image);

#ifdef __cplusplus
}
#endif

#endif
