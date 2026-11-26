# codec router

This directory holds the codec routing core: it discovers format codecs
(under `../formats`), queries each codec's declared capabilities and formats,
and dispatches decode / encode requests to the matching codec. The host calls
only the router; it never probes formats directly.

- Boundary: the host ABI (boundary A) sits above this router; the codec ABI
  (boundary B) sits between this router and each format codec.
- Format codecs live in `../formats/<format>/`, one dynamic library each.
- The router itself contains no format-specific logic.
- The sources are split by concern: `router.c` holds the ABI surface and the
  dispatch, `router_loader.c` finds and loads the libraries, `router_io.c` turns
  paths and descriptors into bytes, `router_cache.c` keeps the last file read,
  and `router_platform.c` holds the locks, the log line and the module handles.
  They share nothing but `router_internal.h` and `router_platform.h`.
- Decode requests carry raw file bytes read by the router, so format codecs
  never open files by name. This keeps UTF-8 paths working on Windows.
- The last file read is cached per context, so a GIF animation does not re-read
  the file for every frame, and browsing in one window does not evict the cache
  of another. `nv_router_destroy` releases it.
- A format library is used only when its `nv_format_abi_version` matches the
  version the router was built against; incompatible libraries are skipped.
- Format libraries stay loaded for the process lifetime. `nv_router_image_free`
  has no router context, so the codec that owns the pixels travels with the
  image in `nv_router_image.pixels_release`; an image may therefore be released
  after `nv_router_destroy` and still reach its library's free function.

## License

Copyright (c) 2026 NopeKat. All rights reserved.

Part of NopeView, licensed under the GNU Affero General Public License v3.0.
See the root [LICENSE](../../../../LICENSE) for the full text.
