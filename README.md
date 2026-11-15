# NopeView

A fast, lightweight, and extensible cross-platform image viewer.

> **Status: early development.** The project is in the planning stage, and no usable builds exist yet.

## Highlights

- **Fast and light.** Startup speed, smooth browsing, and format coverage are the primary goals.
- **Consistent experience.** Localization and themes (follow system / dark / light) follow each platform's native conventions.
- **Seamless updates.** Updates use an A/B slot model: the new version is written to the inactive slot, verified, then activated with a silent restart. Failures roll back silently.

## Supported platforms

| Platform | Minimum version | UI toolkit |
| --- | --- | --- |
| Windows | Windows 10 | WinUI 3 (Windows App SDK) |
| Mac | macOS 12 | SwiftUI |
| Linux | kernel 4.x and later | GTK 4 |
| Android | Android 8 | Jetpack Compose |
| Apple (iPhone / iPad) | iOS 16 | SwiftUI |
| Harmony | Harmony 4 | ArkUI |

## Supported formats

Currently supported: **JPG, PNG, GIF, BMP**.

More formats are planned. The format roadmap will be announced once it is defined.

## Planned capabilities

- Open files through a dialog, drag and drop, and file association
- Switch to the previous or next item within the same directory
- Zoom, pan, and rotate
- Interface localization: Simplified Chinese, Traditional Chinese, American English, Japanese, Korean, Russian, French, and German
- Themes: follow system, dark, and light
- Diagnostic export (crash logs, user settings, and runtime environment, packaged as a zip archive), shared only when the user chooses to send it

## Building

Build instructions are not available yet. They will be added once the project skeleton is in place.

## Repository layout

The project is a single monorepo:

```
apps/
├── <platform>/          native applications for each platform
├── cli/                 command-line entry point (standalone executable)
├── plugins/             application plugins (hot-pluggable)
├── shell/               platform capability layer (unified interface)
└── core/                platform-agnostic logic: scheduling, caching, and file enumeration
    └── codecs/          codec core (router), exposed through the ABI
        └── formats/
            └── <format>/   one codec per format
```

## License

NopeView is licensed under the **GNU Affero General Public License v3.0**. See [LICENSE](LICENSE) for the full text.
