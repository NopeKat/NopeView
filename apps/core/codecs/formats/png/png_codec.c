#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_PNG
#include "stb_image.h"

#include "png_codec.h"

const char *nv_png_version(void)
{
    return NV_PNG_VERSION;
}

int nv_png_decode_file(const char *path, nv_image *out_image)
{
    int width = 0;
    int height = 0;
    int channels_in_file = 0;
    unsigned char *pixels = NULL;

    if (path == NULL || out_image == NULL) {
        return NV_PNG_ERR_INVALID_ARG;
    }

    out_image->width = 0;
    out_image->height = 0;
    out_image->channels = 0;
    out_image->pixels = NULL;

    /* Request four channels so the output is always RGBA. */
    pixels = stbi_load(path, &width, &height, &channels_in_file, 4);
    if (pixels == NULL) {
        return NV_PNG_ERR_DECODE;
    }

    out_image->width = width;
    out_image->height = height;
    out_image->channels = 4;
    out_image->pixels = pixels;
    return NV_PNG_OK;
}

void nv_png_image_free(nv_image *image)
{
    if (image == NULL || image->pixels == NULL) {
        return;
    }
    stbi_image_free(image->pixels);
    image->pixels = NULL;
    image->width = 0;
    image->height = 0;
    image->channels = 0;
}
