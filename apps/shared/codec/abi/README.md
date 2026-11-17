# codecs/abi

ABI contracts owned by the codec core. Both `router` and `format` are
components of this core, so both contracts live here.

- `nv_router_abi.h` - boundary A: what the core exposes to the host
  (host calls, router implements).
- `nv_format_abi.h` - boundary B: what each format codec implements
  (router calls, format implements).

The plugin ABI is not here: plugins are a separate shared module, so their
contract lives in `apps/shared/plugin/abi/`.
