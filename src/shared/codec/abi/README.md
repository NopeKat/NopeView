# codecs/abi

ABI contracts owned by the codec core. Both `router` and `format` are
components of this core, so both contracts live here.

- `nv_router_abi.h` - boundary A: what the core exposes to the host
  (host calls, router implements).
- `nv_format_abi.h` - boundary B: what each format codec implements
  (router calls, format implements).

The plugin ABI is not here: plugins are a separate shared module, so their
contract lives in `src/shared/plugin/abi/`.

## License

Copyright (c) 2026 NopeKat. All rights reserved.

Part of NopeView, licensed under the GNU Affero General Public License v3.0.
See the root [LICENSE](../../../../LICENSE) for the full text.
