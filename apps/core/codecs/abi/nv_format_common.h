/*
 * nv_format_common.h - Shared helpers for format codecs.
 *
 * Every format DLL stays an independent library; this header only removes the
 * identical struct-size checks and "encode unsupported" boilerplate that used
 * to be copied into each codec source file.
 */

#ifndef NV_FORMAT_COMMON_H
#define NV_FORMAT_COMMON_H

#include "nv_format_abi.h"

/* Accept a zero size for callers that predate the struct_size convention. */
static int nv_size_ok(uint32_t struct_size, size_t required)
{
    return struct_size == 0u || (size_t)struct_size >= required;
}

/* Shared validation for formats that do not implement encoding yet. */
static nv_format_status nv_encode_unsupported(
    const nv_format_encode_request *request,
    nv_format_buffer *out_buffer)
{
    if (request == NULL || out_buffer == NULL) {
        return NV_FORMAT_ERR_INVALID_ARGUMENT;
    }
    if (!nv_size_ok(request->struct_size, sizeof(*request)) ||
        !nv_size_ok(out_buffer->struct_size, sizeof(*out_buffer))) {
        return NV_FORMAT_ERR_ABI_MISMATCH;
    }
    return NV_FORMAT_ERR_UNSUPPORTED;
}

#endif /* NV_FORMAT_COMMON_H */
