# Third-party notices

NopeView is licensed under the GNU Affero General Public License v3.0; see
[LICENSE](LICENSE) for the full text. This file lists the third-party
components compiled into or shipped with the distributed binaries, their
licences and copyright holders, and the changes made to the vendored sources.

## Vendored libraries

Every library lives under `src/shared/codec/third_party/` and keeps its licence
next to the sources. `src/shared/codec/third_party/README.md` records the
import scope of each one. The version column comes from the upstream release
wherever one exists; stb and bcdec carry theirs in the header comment, while
miniz and nanosvg have none.

| Component | Version | Licence | Copyright holder |
| --- | --- | --- | --- |
| bcdec | 0.985 | MIT or the Unlicense | Copyright (c) 2022 Sergii Kudlai |
| dav1d | 1.4.3 | BSD-2-Clause | Copyright (c) 2018-2019 VideoLAN and dav1d authors; 2018 Two Orioles, LLC |
| highway | 1.1.0 | Apache-2.0 or BSD-3-Clause | Copyright (c) Google LLC |
| libde265 | 1.0.15 | LGPL-3.0-or-later | Copyright (c) 2013-2014 struktur AG, Dirk Farin |
| libheif | 1.17.6 | LGPL-3.0-or-later | Copyright (c) 2017-2023 Dirk Farin; struktur AG |
| libjpeg-turbo | 3.1.0 | BSD-3-Clause, IJG and zlib | Copyright (C) 2009-2024 D. R. Commander; 1991-2020 Thomas G. Lane, Guido Vollbeding; and other contributors |
| libjxl | 0.11.1 | BSD-3-Clause | Copyright (c) the JPEG XL Project Authors |
| libwebp | 1.4.0 | BSD-3-Clause | Copyright (c) 2010, Google Inc. |
| miniz | - | MIT | Copyright 2013-2014 RAD Game Tools and Valve Software; Copyright 2010-2014 Rich Geldreich and Tenacious Software LLC |
| nanosvg | - | zlib | Copyright (c) 2013-14 Mikko Mononen |
| openjpeg | 2.5.2 | BSD-2-Clause | Copyright (c) 2002-2014 Universite catholique de Louvain (UCL), Belgium; 2002-2014 Professor Benoit Macq; 2003-2014 Antonin Descampe; 2003-2009 Francois-Olivier Devaux; 2005 Herve Drolon, FreeImage Team; and other contributors |
| stb | stb_image 2.32, stb_image_write 1.18 | MIT or public domain | Copyright (c) 2017 Sean Barrett |
| tinyexr | 1.0.8 | BSD-3-Clause | Copyright (C) 2014 - Present, Syoyo Fujita and many contributors |

Version numbers follow the upstream release each library was imported from.
libheif, libjxl, and openjpeg keep a version header in the tree
(`heif_version.h`, `include/jxl/version.h`, `opj_config.h`); dav1d's `version.h`
records the API version 7.0.0, not the release; libjpeg-turbo records its
release in `config/windows/jconfigint.h` and `config/android/jconfigint.h`;
highway has no version marker.

The full licence text of each component is in the file named in its directory
(`LICENSE`, `COPYING`, `LICENSE.md`, `LICENSE.txt`, or inside the
single-header source).
`libjxl` and `libwebp` also ship a `PATENTS` file, and `highway` ships
`LICENSE-BSD3` for the BSD alternative.

## Changes to the vendored sources

The vendored tree is not a verbatim copy of the upstream releases:

1. The C++ sources of highway, libjxl, libheif and libde265 use the `.cpp`
   extension instead of the upstream `.cc` spelling (146 files), together with
   the 29 `HWY_TARGET_INCLUDE` macro strings and the comments that named those
   files.
2. libde265 needs a Win32 condition variable shim that the upstream release
   does not carry. `third_party/extra/win32cond.c` and `win32cond.h` come
   from the ACE/TAO DOC software by Douglas C. Schmidt and his research group
   at Washington University, the University of California, Irvine, and
   Vanderbilt University, Copyright (c) 1993-2009, and keep that notice.
3. A few files were normalised: the UTF-8 BOM was removed, CRLF line endings
   were converted to LF, and most non-ASCII characters in comments were
   replaced with ASCII equivalents. Some remain: 102 source files still carry
   12 kinds of non-ASCII characters, mostly the copyright sign in dav1d's
   copyright headers, plus a plus-minus sign, superscripts, a multiplication
   sign, section signs, curly quotes, an en dash, and a few Cyrillic and Coptic
   letters in comments and text data.
4. Several generated headers are written by hand because the upstream build
   systems normally produce them: `opj_config.h` and `opj_config_private.h`,
   `heif_version.h`, `de265-version.h`, dav1d's `config.h`,
   `include/dav1d/version.h` and `src/vcs_version.h`, libjxl's
   `include/jxl/version.h` and the two export headers, libjpeg-turbo's
   `jconfig.h`, `jconfigint.h` and `jversion.h` (one copy per target in
   `config/windows/` and `config/android/`), and the Android-side
   `src/android/app/src/main/cpp/webp_config/config.h` that keeps libwebp on
   the C fallback.

The import is selective rather than verbatim: openjpeg keeps the decode path
minus the five JPIP index writers (`cidx_manager`, `phix_manager`,
`ppix_manager`, `thix_manager`, `tpix_manager`); dav1d omits the assembly and
per-architecture paths, the tools, tests, and examples; libde265 omits the
encoder, SIMD, and tool sources; libheif omits the Unix, Windows-plugin, and
uncompressed-image sources; libjpeg-turbo keeps the decompressor and the shared
helpers it calls, and omits the encoder, the TurboJPEG API, the SIMD kernels
and the command-line tools; libjxl omits the encoder, tools, and tests and
compiles the box-content path out.

Apart from the changes above and the sources left out of the import, no file
content was altered.

These changes were made in 2026, most recently on 2026-11-26.

## Relinking the LGPL libraries

`libde265` and `libheif` are LGPL-3.0-or-later and are compiled into
`heic_codec.dll` and `avif_codec.dll`. Their sources, the build script
(`tools/build/build-native-windows.ps1`) and the compiler settings are in this
repository, so anyone who receives a binary can rebuild those DLLs with a
modified version of either library and replace them next to the executable.

## Android runtime libraries

The Android build is a single APK, so the Compose runtime and its libraries are
compiled into the package rather than shipped next to it. They are licensed
under Apache-2.0:

| Component | Copyright holder |
| --- | --- |
| androidx.activity `activity-compose` 1.12.4 | Copyright (c) The Android Open Source Project |
| androidx.compose BOM 2025.12.00 (`ui`, `material3`, tooling preview) | Copyright (c) The Android Open Source Project |
| androidx.exifinterface 1.3.7 | Copyright (c) The Android Open Source Project |
| androidx.graphics path library (`libandroidx.graphics.path.so`) | Copyright (c) The Android Open Source Project |
| Kotlin standard library and the Compose compiler output | Copyright (c) JetBrains s.r.o. and contributors |

Each AndroidX artifact keeps its licence text inside the APK under
`META-INF/androidx/`. The project licence and this file are packed into the
APK as well, under `assets/licenses/`.

## Embedded archive module

The portable executable is a 7-Zip installer SFX archive, not a single-file
publish: the `7zS.sfx` stub is concatenated in front of an installer
configuration block and the payload archive. The stub comes from 7-Zip Extra
9.20 and is licensed under the GNU LGPL. It lives in the workspace at
`.tools/7zip-sfx/7zS.sfx`, outside the repository, and is embedded in every
`-win-x64-portable.exe`; the 7-Zip application used to build the archive is
not distributed.

## Application assets and SDKs

1. The interface icons are the official Fluent UI System Icons (MIT licence,
   Copyright (c) Microsoft Corporation), embedded as SVG path data in
   `src/windows/MainWindow.xaml`, `src/windows/SettingsPage.xaml` and
   `src/windows/Icons.cs`.
2. The self-contained Windows build ships the .NET runtime (MIT licence) and
   the Windows App SDK (Microsoft Software License Terms). Their terms travel
   with the executable in `licenses/`: the .NET `LICENSE.txt` and
   `ThirdPartyNotices.txt`, and the Windows App SDK `license.txt` and the
   `NOTICE.txt` that covers the SDK's own third-party components.
