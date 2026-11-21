# third party

Vendored sources the format codecs compile in. Each library keeps its licence
next to its sources and is only partially imported, as noted below.

- `stb` — `stb_image.h` and `stb_image_write.h` used by the png, jpg, gif, bmp
  and ico codecs. Public domain.
- `libwebp` (1.4.0) — decode path only: `src/dec`, `src/demux`, `src/dsp`,
  `src/utils`, plus the encoder headers `src/dsp/lossless.h` needs. The `webp`
  codec uses it for still and animated WebP. BSD-style licence in `COPYING`.
- `nanosvg` — `nanosvg.h` and `nanosvgrast.h`, used by the `svg` codec to parse
  and rasterize documents. zlib licence in `LICENSE.txt`.

Only files that a codec actually links are kept; encoder-side sources are not
vendored, so the DLLs cannot write these formats.
