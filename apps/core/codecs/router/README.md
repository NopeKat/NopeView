# codec router

This directory holds the codec routing core: it discovers format codecs
(under `../formats`), queries each codec's declared capabilities and formats,
and dispatches decode / encode requests to the matching codec. The host calls
only the router; it never probes formats directly.

- Boundary: the host ABI (boundary A) sits above this router; the codec ABI
  (boundary B) sits between this router and each format codec.
- Format codecs live in `../formats/<format>/`, one dynamic library each.
- The router itself contains no format-specific logic.
