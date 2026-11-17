/*
 * Device-side smoke tests for the shared format codecs.
 *
 * The test binary links exactly one codec at a time. It checks the format
 * contract that Android relies on: still formats report one frame, GIF reports
 * its real total and honours frame_index, and a repeated GIF frame request
 * survives the cache path.
 */

#include "nv_format_abi.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures = 0;

static void check(int condition, const char *message)
{
    if (condition) {
        printf("ok   %s\n", message);
        return;
    }
    printf("FAIL %s\n", message);
    failures++;
}

static uint64_t pixel_hash(const nv_format_image *image)
{
    uint64_t hash = UINT64_C(1469598103934665603);
    size_t index;

    if (image->pixels == NULL) {
        return 0u;
    }
    for (index = 0u; index < image->pixels_size; index++) {
        hash ^= (uint64_t)image->pixels[index];
        hash *= UINT64_C(1099511628211);
    }
    return hash;
}

static nv_format_status decode_frame(
    const char *path,
    uint32_t frame_index,
    nv_format_image *out_image)
{
    nv_format_decode_request request;

    memset(&request, 0, sizeof(request));
    request.struct_size = (uint32_t)sizeof(request);
    request.path = path;
    request.desired_pixel_format = NV_FORMAT_PIXEL_RGBA8;
    request.frame_index = frame_index;
    request.max_dimension = 0;

    memset(out_image, 0, sizeof(*out_image));
    out_image->struct_size = (uint32_t)sizeof(*out_image);
    return nv_format_decode(&request, out_image);
}

int main(int argc, char **argv)
{
    nv_format_info info;
    nv_format_image first;
    nv_format_image second;
    nv_format_image repeat;
    const char *path;
    const char *expected_id;
    int expected_frames;
    int expected_width;
    int expected_height;
    uint64_t first_hash;
    uint64_t second_hash;
    uint64_t repeat_hash;

    if (argc != 6) {
        printf("usage: %s <image> <format-id> <frames> <width> <height>\n", argv[0]);
        return 2;
    }
    path = argv[1];
    expected_id = argv[2];
    expected_frames = atoi(argv[3]);
    expected_width = atoi(argv[4]);
    expected_height = atoi(argv[5]);

    memset(&info, 0, sizeof(info));
    info.struct_size = (uint32_t)sizeof(info);
    check(nv_format_query(&info) == NV_FORMAT_OK, "query returns OK");
    check(info.format_id != NULL && strcmp(info.format_id, expected_id) == 0,
          "query reports the expected format id");
    check((info.capabilities & NV_FORMAT_CAP_DECODE) != 0u, "decode capability");

    check(decode_frame(path, 0u, &first) == NV_FORMAT_OK, "frame 0 decodes");
    check(first.width == expected_width && first.height == expected_height,
          "frame 0 dimensions match");
    check(first.frame_count == (uint32_t)expected_frames,
          "frame 0 reports the expected frame count");
    check(first.pixels != NULL && first.pixels_size > 0u, "frame 0 has pixels");
    check(first.source_width == first.width && first.source_height == first.height,
          "frame 0 source size matches output");
    first_hash = pixel_hash(&first);
    check(first_hash != 0u, "frame 0 hash");

    if (expected_frames == 1) {
        check(decode_frame(path, 1u, &second) == NV_FORMAT_ERR_UNSUPPORTED,
              "still format rejects frame 1");
    }
    else {
        check(first.frame_delay_ms > 0u, "frame 0 has a delay");
        check(decode_frame(path, 1u, &second) == NV_FORMAT_OK, "frame 1 decodes");
        check(second.frame_count == (uint32_t)expected_frames,
              "frame 1 reports the same frame count");
        check(second.frame_delay_ms > 0u, "frame 1 has a delay");
        second_hash = pixel_hash(&second);
        check(second_hash != 0u, "frame 1 hash");
        check(second_hash != first_hash, "frame 1 differs from frame 0");

        /* The second request for the same frame must go through the cache and
         * still return a private copy with identical pixels. */
        check(decode_frame(path, 1u, &repeat) == NV_FORMAT_OK,
              "repeated frame 1 decodes through the cache");
        repeat_hash = pixel_hash(&repeat);
        check(repeat_hash == second_hash, "cached frame pixels match");
        check(repeat.pixels != second.pixels, "cached frame returns a private copy");
        nv_format_image_free(&repeat);
    }

    nv_format_image_free(&first);
    nv_format_image_free(&second);

    if (failures != 0) {
        printf("%d failure(s)\n", failures);
        return 1;
    }
    printf("all checks passed\n");
    return 0;
}
