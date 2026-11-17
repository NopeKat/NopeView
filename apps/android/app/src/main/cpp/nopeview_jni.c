/*
 * JNI facade for the codec router.
 *
 * The router stays a plain C library. This file only converts Java strings,
 * descriptors, and decoded pixels, and keeps the router context behind a jlong.
 */

#include <jni.h>
#include <stdint.h>
#include <string.h>

#include <android/log.h>

#include "nv_router_abi.h"

#define NV_LOG_TAG "NopeViewNative"
#define NV_LOG(...) __android_log_print(ANDROID_LOG_INFO, NV_LOG_TAG, __VA_ARGS__)

static nv_router_context *context_from_handle(jlong handle)
{
    return (nv_router_context *)(intptr_t)handle;
}

JNIEXPORT jlong JNICALL
Java_app_android_nopeview_NativeCodec_create(
    JNIEnv *env, jobject thiz, jstring codec_dir)
{
    const char *directory;
    nv_router_context *context = NULL;
    nv_router_status status;

    (void)thiz;

    directory = codec_dir == NULL ? NULL : (*env)->GetStringUTFChars(env, codec_dir, NULL);
    status = nv_router_create(directory, &context);
    if (directory != NULL) {
        (*env)->ReleaseStringUTFChars(env, codec_dir, directory);
    }

    if (status != NV_ROUTER_OK || context == NULL) {
        NV_LOG("router create failed: %d", (int)status);
        return 0;
    }
    return (jlong)(intptr_t)context;
}

JNIEXPORT void JNICALL
Java_app_android_nopeview_NativeCodec_destroy(
    JNIEnv *env, jobject thiz, jlong handle)
{
    (void)env;
    (void)thiz;
    nv_router_destroy(context_from_handle(handle));
}

JNIEXPORT jbyteArray JNICALL
Java_app_android_nopeview_NativeCodec_decodeFd(
    JNIEnv *env, jobject thiz, jlong handle, jint fd, jint max_dimension, jintArray out_info)
{
    nv_router_context *context = context_from_handle(handle);
    nv_router_image image;
    nv_router_status status;
    jbyteArray bytes;
    jint info[8];
    jsize info_length;

    (void)thiz;

    if (context == NULL || fd < 0 || out_info == NULL) {
        return NULL;
    }
    info_length = (*env)->GetArrayLength(env, out_info);
    if (info_length < (jsize)(sizeof(info) / sizeof(info[0]))) {
        return NULL;
    }

    memset(&image, 0, sizeof(image));
    image.struct_size = (uint32_t)sizeof(image);
    status = nv_router_decode_fd_ex(
        context,
        (int)fd,
        NV_ROUTER_PIXEL_RGBA8,
        0u,
        max_dimension,
        &image);
    if (status != NV_ROUTER_OK) {
        NV_LOG("decode fd failed: %d", (int)status);
        return NULL;
    }

    if (image.pixels == NULL || image.width <= 0 || image.height <= 0 ||
        image.stride != image.width * 4 ||
        image.pixels_size > (size_t)INT32_MAX) {
        nv_router_image_free(&image);
        return NULL;
    }

    NV_LOG("decoded %dx%d frames=%u", image.width, image.height,
           (unsigned int)image.frame_count);

    bytes = (*env)->NewByteArray(env, (jsize)image.pixels_size);
    if (bytes == NULL) {
        nv_router_image_free(&image);
        return NULL;
    }
    (*env)->SetByteArrayRegion(
        env, bytes, 0, (jsize)image.pixels_size, (const jbyte *)image.pixels);

    info[0] = image.width;
    info[1] = image.height;
    info[2] = image.stride;
    info[3] = (jint)image.frame_count;
    info[4] = (jint)image.frame_delay_ms;
    info[5] = image.source_width;
    info[6] = image.source_height;
    info[7] = (jint)image.pixel_format;
    (*env)->SetIntArrayRegion(env, out_info, 0, (jsize)(sizeof(info) / sizeof(info[0])), info);

    nv_router_image_free(&image);
    return bytes;
}
