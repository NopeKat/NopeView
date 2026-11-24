# third party

Vendored sources the format codecs compile in. Each library keeps its licence
next to its sources and is only partially imported, as noted below.

- `stb` — `stb_image.h` and `stb_image_write.h` used by the png, jpg, gif, bmp,
  ico, tga and hdr codecs. Public domain.
- `libwebp` (1.4.0) — decode path only: `src/dec`, `src/demux`, `src/dsp`,
  `src/utils`, plus the encoder headers `src/dsp/lossless.h` needs. The `webp`
  codec uses it for still and animated WebP. BSD-style licence in `COPYING`.
- `nanosvg` — `nanosvg.h` and `nanosvgrast.h`, used by the `svg` codec to parse
  and rasterize documents. zlib licence in `LICENSE.txt`.
- `tinyexr` (1.0.8) — `tinyexr.h` only, used by the `exr` codec to load OpenEXR
  images. The header compiles on its own because nanozlib and zfp stay
  disabled. BSD-3-Clause licence in `LICENSE`.
- `miniz` — `miniz.c` and `miniz.h`, used by the `exr`, `tiff`, `psd` and `svg`
  codecs for Deflate data; the `svg` codec only inflates the gzip wrapper of an
  svgz. Public domain.
- `bcdec` — `bcdec.h` only, used by the `dds` codec to expand BC1 to BC7 blocks.
  Public domain, licence text inside the header.
- `openjpeg` (2.5.2) — `src/lib/openjp2`: the whole decode path plus the small
  encoder it needs, minus the JPIP index writers (`cidx_manager`, `phix_manager`,
  `ppix_manager`, `thix_manager`, `tpix_manager`). The `jp2` codec uses it for
  JP2 containers and bare JPEG 2000 codestreams. `opj_config.h` and
  `opj_config_private.h` are written by hand because CMake normally generates
  them. BSD-2-Clause licence in `LICENSE`.
- `libheif` (1.17.6) — the HEIF container reader, colour conversion pipeline
  and plugin registry, plus the `decoder_libde265`, `decoder_dav1d` and
  `encoder_mask` plugins. `heif_version.h` is written by hand because CMake
  normally generates it. Unix, Windows-plugin and uncompressed-image sources
  are left out because plugin loading and that codec are disabled.
  LGPL-3.0-or-later in `COPYING`.
- `libde265` (1.0.15) — the HEVC decoder behind libheif, with the encoder,
  SIMD and tool sources left out; pure C fallback paths are used. Threads use
  the Win32 condition variable shim in `extra/`. `de265-version.h` is written
  by hand because autoconf normally generates it. LGPL-3.0-or-later in
  `COPYING`.
- `dav1d` (1.4.3) — the AV1 decoder behind the `avif` codec, built from the
  portable C paths only: assembly, the per-architecture CPU files, tools,
  tests and examples are left out. `config.h`, `include/dav1d/version.h` and
  `src/vcs_version.h` are written by hand because Meson normally generates
  them. The bitdepth template sources are compiled twice, once with
  `BITDEPTH=8` and once with `BITDEPTH=16`. BSD-2-Clause licence in `COPYING`.
- `libjxl` (0.11.1) — the decoder translation units from
  `JPEGXL_INTERNAL_DEC_SOURCES`, the public `jxl` headers, and the C decoder
  API. Encoder, tools, tests and the JPEG reconstruction path are left out;
  the box-content path is compiled out too, so Brotli and the colour
  management libraries are not needed. `include/jxl/version.h` and the two
  export headers are written by hand because CMake normally generates them. BSD-3-Clause
  licence in `LICENSE`.
- `highway` (1.1.0) — the six core `hwy` sources and the headers they include,
  used by libjxl for its vector paths. The library is linked statically, so no
  per-target dispatch DLL is involved. Apache-2.0 licence in `LICENSE`, with
  the BSD-3-Clause alternative in `LICENSE-BSD3`.

`heic_codec.dll` statically links libheif with libde265 under `HAVE_LIBDE265`;
`avif_codec.dll` statically links libheif with dav1d under `HAVE_DAV1D`; and
`jxl_codec.dll` statically links libjxl with highway. Each DLL enables only its
own backend, so HEIC cannot decode AV1 or JPEG XL, AVIF cannot decode HEVC or
JPEG XL, and JXL cannot decode HEIF or AV1. AOM, JPEG, JPEG 2000, VVC and the
libjxl encoder stay compiled out of all three.

Only files that a codec actually links are kept; encoder-side sources are not
vendored, so the DLLs cannot write WebP, SVG, EXR, TIFF, DDS, PSD, JPEG 2000,
JPEG XL, HEIC or AVIF. Libraries that ship encode and decode in the same
translation units bring their encoder along, but the codecs still only claim
decode capability.
