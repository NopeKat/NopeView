# platform

The facade between the application and the operating system. It wraps
platform-specific capabilities behind one unified interface.

- Location: `src/shared/platform/` groups the platform facade under shared.
  The nesting is repository grouping only; shared codecs and file services
  never depend on `platforms`.
- Scope: platform differences only, such as screen capture, always-on-top
  windows, filesystem specifics, and notifications.
- Form: a library. It provides no executable entry point.
- Consumers: platform apps, the CLI, and plugins.
- Dependency direction: `platforms` may call `core`; `core` never depends on
  `platforms`.
- Pure logic belongs in `core`; do not use `platforms` as a catch-all.
- Per-platform implementations live inside `platforms`; callers see one
  interface and never branch on the platform.
- Current state: skeleton only. No implementation yet.

## License

Copyright (c) 2026 NopeKat. All rights reserved.

Part of NopeView, licensed under the GNU Affero General Public License v3.0.
See the root [LICENSE](../../../LICENSE) for the full text.
