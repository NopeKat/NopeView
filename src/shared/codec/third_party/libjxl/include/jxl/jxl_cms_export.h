/* Copyright (c) the JPEG XL Project Authors. All rights reserved.
 *
 * Use of this source code is governed by a BSD-style
 * license that can be found in the LICENSE file.
 */

/* Written by hand for the vendored static build; CMake normally generates this
 * with GenerateExportHeader. The decoder build does not link libjxl_cms. */

#ifndef JXL_CMS_EXPORT_H
#define JXL_CMS_EXPORT_H

#ifdef JXL_CMS_STATIC_DEFINE
#define JXL_CMS_EXPORT
#define JXL_CMS_NO_EXPORT
#else
#ifndef JXL_CMS_EXPORT
#define JXL_CMS_EXPORT
#endif
#ifndef JXL_CMS_NO_EXPORT
#define JXL_CMS_NO_EXPORT
#endif
#endif

#endif /* JXL_CMS_EXPORT_H */
