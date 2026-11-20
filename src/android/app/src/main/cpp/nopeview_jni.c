/*
 * JNI facade for the codec router.
 *
 * The router stays a plain C library. This file only converts Java strings,
 * descriptors, and decoded pixels, and keeps the router context behind a jlong.
 */

#include <jni.h>
#include <stdint.h>
#include <string.h>

#include <android/bitmap.h>
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

JNIEXPORT jobject JNICALL
Java_app_android_nopeview_NativeCodec_decodeFdBitmap(
    JNIEnv *env, jobject thiz, jlong handle, jint fd, jint frame_index,
    jint max_dimension, jintArray out_info)
{
    nv_router_context *context = context_from_handle(handle);
    nv_router_image image;
    nv_router_status status;
    jclass bitmap_class;
    jclass config_class;
    jmethodID create_bitmap;
    jfieldID config_field;
    jobject config;
    jobject bitmap;
    AndroidBitmapInfo bitmap_info;
    void *bitmap_pixels;
    jint info[8];
    jsize info_length;
    uint32_t y;
    int result;

    (void)thiz;

    if (context == NULL || fd < 0 || frame_index < 0 || out_info == NULL) {
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
        (uint32_t)frame_index,
        max_dimension,
        &image);
    if (status != NV_ROUTER_OK) {
        NV_LOG("decode fd failed: %d", (int)status);
        return NULL;
    }

    if (image.pixels == NULL || image.width <= 0 || image.height <= 0 ||
        (size_t)image.stride < (size_t)image.width * 4u ||
        image.pixels_size < (size_t)image.stride * (size_t)image.height ||
        image.width > INT32_MAX || image.height > INT32_MAX) {
        nv_router_image_free(&image);
        return NULL;
    }

    bitmap_class = (*env)->FindClass(env, "android/graphics/Bitmap");
    if (bitmap_class == NULL) {
        nv_router_image_free(&image);
        return NULL;
    }
    create_bitmap = (*env)->GetStaticMethodID(
        env, bitmap_class, "createBitmap",
        "(IILandroid/graphics/Bitmap$Config;)Landroid/graphics/Bitmap;");
    config_class = (*env)->FindClass(env, "android/graphics/Bitmap$Config");
    if (config_class == NULL) {
        (*env)->DeleteLocalRef(env, bitmap_class);
        nv_router_image_free(&image);
        return NULL;
    }
    config_field = (*env)->GetStaticFieldID(
        env, config_class, "ARGB_8888", "Landroid/graphics/Bitmap$Config;");
    if (create_bitmap == NULL || config_field == NULL) {
        (*env)->DeleteLocalRef(env, config_class);
        (*env)->DeleteLocalRef(env, bitmap_class);
        nv_router_image_free(&image);
        return NULL;
    }

    config = (*env)->GetStaticObjectField(env, config_class, config_field);
    bitmap = (*env)->CallStaticObjectMethod(
        env, bitmap_class, create_bitmap,
        (jint)image.width, (jint)image.height, config);
    (*env)->DeleteLocalRef(env, config);
    (*env)->DeleteLocalRef(env, config_class);
    (*env)->DeleteLocalRef(env, bitmap_class);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        if (bitmap != NULL) {
            (*env)->DeleteLocalRef(env, bitmap);
        }
        nv_router_image_free(&image);
        return NULL;
    }
    if (bitmap == NULL) {
        nv_router_image_free(&image);
        return NULL;
    }

    result = AndroidBitmap_getInfo(env, bitmap, &bitmap_info);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS ||
        bitmap_info.width != (uint32_t)image.width ||
        bitmap_info.height != (uint32_t)image.height ||
        bitmap_info.format != ANDROID_BITMAP_FORMAT_RGBA_8888 ||
        bitmap_info.stride < (uint32_t)image.width * 4u) {
        (*env)->DeleteLocalRef(env, bitmap);
        nv_router_image_free(&image);
        return NULL;
    }

    result = AndroidBitmap_lockPixels(env, bitmap, &bitmap_pixels);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS) {
        (*env)->DeleteLocalRef(env, bitmap);
        nv_router_image_free(&image);
        return NULL;
    }

    for (y = 0u; y < (uint32_t)image.height; y++) {
        const unsigned char *source_row =
            image.pixels + (size_t)y * (size_t)image.stride;
        unsigned char *destination_row =
            (unsigned char *)bitmap_pixels + (size_t)y * (size_t)bitmap_info.stride;

        memcpy(destination_row, source_row, (size_t)image.width * 4u);
    }

    result = AndroidBitmap_unlockPixels(env, bitmap);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS) {
        (*env)->DeleteLocalRef(env, bitmap);
        nv_router_image_free(&image);
        return NULL;
    }

    info[0] = image.width;
    info[1] = image.height;
    info[2] = image.stride;
    info[3] = (jint)image.frame_count;
    info[4] = (jint)image.frame_delay_ms;
    info[5] = image.source_width;
    info[6] = image.source_height;
    info[7] = (jint)image.pixel_format;
    (*env)->SetIntArrayRegion(
        env, out_info, 0, (jsize)(sizeof(info) / sizeof(info[0])), info);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        (*env)->DeleteLocalRef(env, bitmap);
        nv_router_image_free(&image);
        return NULL;
    }

    NV_LOG("decoded bitmap %dx%d source=%dx%d frame=%d/%u",
           image.width,
           image.height,
           image.source_width,
           image.source_height,
           (int)frame_index,
           (unsigned int)image.frame_count);
    nv_router_image_free(&image);
    return bitmap;
}

JNIEXPORT jboolean JNICALL
Java_app_android_nopeview_NativeCodec_decodeFdBitmapInto(
    JNIEnv *env, jobject thiz, jlong handle, jint fd, jint frame_index,
    jint max_dimension, jintArray out_info, jobject bitmap)
{
    nv_router_context *context = context_from_handle(handle);
    nv_router_image image;
    nv_router_status status;
    AndroidBitmapInfo bitmap_info;
    void *bitmap_pixels;
    jint info[8];
    jsize info_length;
    uint32_t y;
    int result;

    (void)thiz;

    if (context == NULL || fd < 0 || frame_index < 0 || out_info == NULL ||
        bitmap == NULL) {
        return JNI_FALSE;
    }
    info_length = (*env)->GetArrayLength(env, out_info);
    if (info_length < (jsize)(sizeof(info) / sizeof(info[0]))) {
        return JNI_FALSE;
    }

    memset(&image, 0, sizeof(image));
    image.struct_size = (uint32_t)sizeof(image);
    status = nv_router_decode_fd_ex(
        context,
        (int)fd,
        NV_ROUTER_PIXEL_RGBA8,
        (uint32_t)frame_index,
        max_dimension,
        &image);
    if (status != NV_ROUTER_OK) {
        NV_LOG("decode fd into bitmap failed: %d", (int)status);
        return JNI_FALSE;
    }

    if (image.pixels == NULL || image.width <= 0 || image.height <= 0 ||
        (size_t)image.stride < (size_t)image.width * 4u ||
        image.pixels_size < (size_t)image.stride * (size_t)image.height) {
        nv_router_image_free(&image);
        return JNI_FALSE;
    }

    result = AndroidBitmap_getInfo(env, bitmap, &bitmap_info);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS ||
        bitmap_info.width != (uint32_t)image.width ||
        bitmap_info.height != (uint32_t)image.height ||
        bitmap_info.format != ANDROID_BITMAP_FORMAT_RGBA_8888 ||
        bitmap_info.stride < (uint32_t)image.width * 4u) {
        nv_router_image_free(&image);
        return JNI_FALSE;
    }

    result = AndroidBitmap_lockPixels(env, bitmap, &bitmap_pixels);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS) {
        nv_router_image_free(&image);
        return JNI_FALSE;
    }

    for (y = 0u; y < (uint32_t)image.height; y++) {
        const unsigned char *source_row =
            image.pixels + (size_t)y * (size_t)image.stride;
        unsigned char *destination_row =
            (unsigned char *)bitmap_pixels + (size_t)y * (size_t)bitmap_info.stride;

        memcpy(destination_row, source_row, (size_t)image.width * 4u);
    }

    result = AndroidBitmap_unlockPixels(env, bitmap);
    if (result != ANDROID_BITMAP_RESULT_SUCCESS) {
        nv_router_image_free(&image);
        return JNI_FALSE;
    }

    info[0] = image.width;
    info[1] = image.height;
    info[2] = image.stride;
    info[3] = (jint)image.frame_count;
    info[4] = (jint)image.frame_delay_ms;
    info[5] = image.source_width;
    info[6] = image.source_height;
    info[7] = (jint)image.pixel_format;
    (*env)->SetIntArrayRegion(
        env, out_info, 0, (jsize)(sizeof(info) / sizeof(info[0])), info);
    if ((*env)->ExceptionCheck(env)) {
        (*env)->ExceptionClear(env);
        nv_router_image_free(&image);
        return JNI_FALSE;
    }

    NV_LOG("decoded bitmap into %dx%d source=%dx%d frame=%d/%u",
           image.width,
           image.height,
           image.source_width,
           image.source_height,
           (int)frame_index,
           (unsigned int)image.frame_count);
    nv_router_image_free(&image);
    return JNI_TRUE;
}
