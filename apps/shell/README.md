# shell

The facade between the application and the operating system. It wraps
platform-specific capabilities behind one unified interface.

- Scope: platform differences only, such as screen capture, always-on-top
  windows, filesystem specifics, and notifications.
- Form: a library. It provides no executable entry point.
- Consumers: platform apps, the CLI, and plugins.
- Dependency direction: `shell` may call `core`; `core` never depends on `shell`.
- Pure logic belongs in `core`; do not use `shell` as a catch-all.
- Per-platform implementations live inside `shell`; callers see one interface
  and never branch on the platform.
- Current state: skeleton only. No implementation yet.
