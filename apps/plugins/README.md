# plugins

Application plugins: user-facing feature extensions that are separate from the
codec core. Examples include format conversion, batch rename, image
organization, screen capture, and pin-to-screen.

> Naming note: a plugin here is a user-facing feature extension, not a
> codec/format. Codecs live under `apps/shared/codecs/formats`.

- One folder per plugin.
- Plugins may call `core` and `platforms`; platform-specific work belongs in
  `platforms`, so plugins never touch platform APIs directly.
- Shipped as independent dynamic libraries, loaded through an ABI, and
  hot-pluggable at runtime.
- The core never depends on a plugin. A missing plugin degrades features but
  must not crash the app.
- Current state: skeleton only. No plugins yet.
