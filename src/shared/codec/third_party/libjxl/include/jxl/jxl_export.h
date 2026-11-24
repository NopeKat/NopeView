/* Copyright (c) the JPEG XL Project Authors. All rights reserved.
 *
 * Use of this source code is governed by a BSD-style
 * license that can be found in the LICENSE file.
 */

/* Written by hand for the vendored static build; CMake normally generates this
 * with GenerateExportHeader. The codec links libjxl into its own DLL, so the
 * API symbols need no import or export decoration. */

#ifndef JXL_EXPORT_H
#define JXL_EXPORT_H

#ifdef JXL_STATIC_DEFINE
#define JXL_EXPORT
#define JXL_NO_EXPORT
#else
#ifndef JXL_EXPORT
#ifdef jxl_EXPORTS
#define JXL_EXPORT __declspec(dllexport)
#else
#define JXL_EXPORT __declspec(dllimport)
#endif
#endif
#ifndef JXL_NO_EXPORT
#define JXL_NO_EXPORT
#endif
#endif

#ifndef JXL_DEPRECATED
#define JXL_DEPRECATED
#endif
#ifndef JXL_DEPRECATED_EXPORT
#define JXL_DEPRECATED_EXPORT JXL_EXPORT
#endif
#ifndef JXL_DEPRECATED_NO_EXPORT
#define JXL_DEPRECATED_NO_EXPORT JXL_NO_EXPORT
#endif

#endif /* JXL_EXPORT_H */
