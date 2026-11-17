# plugin

Application plugins: user-facing feature extensions that are separate from the
codec core. Examples include format conversion, batch rename, image
organization, screen capture, and pin-to-screen.

> Naming note: a plugin here is a user-facing feature extension, not a
> codec/format. Codecs live under `apps/shared/codec/formats`.

- Location: `apps/shared/plugin/` groups plugins with the other non-GUI
  modules. The nesting is repository grouping only.
- One folder per plugin.
- Plugins may call shared core services and `platforms`; platform-specific work
  belongs in `platforms`, so plugins never touch platform APIs directly.
- Shipped as independent dynamic libraries, loaded through an ABI, and
  hot-pluggable at runtime.
- The shared core never depends on a plugin. A missing plugin degrades features
  but must not crash the app.
- Current state: skeleton only. No plugins yet.
