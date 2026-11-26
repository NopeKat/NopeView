# Third-party notices

NopeView is licensed under the GNU Affero General Public License v3.0; see
[LICENSE](LICENSE) for the full text. This file lists the third-party
components compiled into the distributed binaries, their licences and
copyright holders, and the changes made to the vendored sources.

## Vendored libraries

Every library lives under `src/shared/codec/third_party/` and keeps its licence
next to the sources. `src/shared/codec/third_party/README.md` records the
version and the import scope of each one.

| Component | Version | Licence | Copyright holder |
| --- | --- | --- | --- |
| bcdec | 0.985 | MIT or the Unlicense | Copyright (c) 2022 Sergii Kudlai |
| dav1d | 1.4.3 | BSD-2-Clause | Copyright (c) 2018-2019, VideoLAN and dav1d authors |
| highway | 1.1.0 | Apache-2.0 or BSD-3-Clause | Copyright (c) Google LLC |
| libde265 | 1.0.15 | LGPL-3.0-or-later | Copyright (c) struktur AG |
| libheif | 1.17.6 | LGPL-3.0-or-later | Copyright (c) struktur AG |
| libjxl | 0.11.1 | BSD-3-Clause | Copyright (c) the JPEG XL Project Authors |
| libwebp | 1.4.0 | BSD-3-Clause | Copyright (c) 2010, Google Inc. |
| miniz | - | MIT | Copyright 2013-2014 RAD Game Tools and Valve Software; Copyright 2010-2014 Rich Geldreich and Tenacious Software LLC |
| nanosvg | - | zlib | Copyright (c) 2013-14 Mikko Mononen |
| openjpeg | 2.5.2 | BSD-2-Clause | Copyright (c) 2002-2014, Universite catholique de Louvain (UCL), Belgium |
| stb | 2.32 | MIT or public domain | Copyright (c) 2017 Sean Barrett |
| tinyexr | 1.0.8 | BSD-3-Clause | Copyright (C) 2014 - Present, Syoyo Fujita and many contributors |

The full licence text of each component is in the file named in its directory
(`LICENSE`, `COPYING`, `LICENSE.txt`, or inside the single-header source).
`libjxl` and `libwebp` also ship a `PATENTS` file, and `highway` ships
`LICENSE-BSD3` for the BSD alternative.

## Changes to the vendored sources

The vendored tree is not a verbatim copy of the upstream releases:

1. The C++ sources of highway, libjxl, libheif and libde265 use the `.cpp`
   extension instead of the upstream `.cc` spelling (146 files), together with
   the 29 `HWY_TARGET_INCLUDE` macro strings and the comments that named those
   files.
2. A few files were normalised: the UTF-8 BOM was removed, CRLF line endings
   were converted to LF, and the handful of non-ASCII characters in comments
   were replaced with ASCII equivalents. No other file content was altered.
3. Several generated headers are written by hand because the upstream build
   systems normally produce them: `opj_config.h` and `opj_config_private.h`,
   `heif_version.h`, `de265-version.h`, dav1d's `config.h`,
   `include/dav1d/version.h` and `src/vcs_version.h`, plus libjxl's
   `include/jxl/version.h` and the two export headers.

These changes were made in 2026, most recently on 2026-11-26.

## Relinking the LGPL libraries

`libde265` and `libheif` are LGPL-3.0-or-later and are compiled into
`heic_codec.dll` and `avif_codec.dll`. Their sources, the build script
(`.scripts/build-native-windows.ps1`) and the compiler settings are in this
repository, so anyone who receives a binary can rebuild those DLLs with a
modified version of either library and replace them next to the executable.

## Application assets and SDKs

1. The interface icons are the official Fluent UI System Icons (MIT licence,
   Copyright (c) Microsoft Corporation), embedded as SVG path data in
   `src/windows/MainWindow.xaml`, `src/windows/SettingsPage.xaml` and
   `src/windows/Icons.cs`.
2. The self-contained Windows build ships the .NET runtime (MIT licence) and
   the Windows App SDK; their licence files are part of the NuGet packages
   restored during the build.
