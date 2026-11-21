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
- `miniz` — `miniz.c` and `miniz.h`, used by the `exr`, `tiff` and `psd` codecs
  for Deflate data. Public domain.
- `bcdec` — `bcdec.h` only, used by the `dds` codec to expand BC1 to BC7 blocks.
  Public domain, licence text inside the header.

Only files that a codec actually links are kept; encoder-side sources are not
vendored, so the DLLs cannot write WebP, SVG, EXR, TIFF, DDS or PSD.
