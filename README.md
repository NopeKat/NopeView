# NopeView

A fast, lightweight, and extensible cross-platform image viewer.

> **Status: early development.** Only the Windows side has a buildable, runnable skeleton so far: the custom title bar and the codec path (router plus four decoders) are in place, but the window content area is still empty, image display is not wired up, and there is no release yet.

## Highlights

- **Fast and light.** Startup speed, smooth browsing, and format coverage are the primary goals.
- **Consistent experience.** Localization and themes (follow system / dark / light) follow each platform's native conventions.
- **Seamless updates.** Updates use an A/B slot model: the new version is written to the inactive slot, verified, then activated with a silent restart. Failures roll back silently.

## Current progress

- **Codecs**: the router core and the decoders for PNG, JPG, GIF, and BMP are implemented. The encode entry point exists, but all four formats return "unsupported".
- **Windows app**: WinUI 3, with content extended into the title bar and a self-drawn title bar whose minimize / maximize / close buttons are XAML buttons using Fluent `PathIcon` (the native title bar strip and its tooltips are collapsed), PerMonitorV2 declared, and the NopeView brand logo shown at the left of the title bar. The app icon is set both in the executable and on the running window for Explorer, taskbar, Alt-Tab and Start. The content area is currently empty.
- **Three ABI boundaries**: router (`nv_router_abi.h`), format (`nv_format_abi.h`), and application plugin (`nv_plugin_abi.h`) are all in place, each versioned `0.0.0.0` (the `x.y.z.n` scheme).
- **Other modules**: the CLI, application plugins, and the platform capability layer contain only directories and notes; nothing is implemented.

## Supported platforms

| Platform | Minimum version | UI toolkit | Status |
| --- | --- | --- | --- |
| Windows | Windows 10 | WinUI 3 (Windows App SDK) | Runnable skeleton |
| Mac | macOS 12 | SwiftUI | Not started |
| Linux | kernel 4.x and later | GTK 4 | Not started |
| Android | Android 8 | Jetpack Compose | Not started |
| Apple (iPhone / iPad) | iOS 16 | SwiftUI | Not started |
| Harmony | Harmony 4 | ArkUI | Not started |

## Supported formats

| Format | Decode | Encode | Capability flags |
| --- | --- | --- | --- |
| PNG | Yes | No | `DECODE` |
| JPG | Yes | No | `DECODE` |
| GIF | Yes, per frame | No | `DECODE` + `ANIMATED` |
| BMP | Yes | No | `DECODE` |

Decoding is built on `apps/core/codecs/third_party/stb/stb_image.h`. More formats are planned; the format roadmap will be announced once it is defined.

## Planned capabilities

- Open files through a dialog, drag and drop, and file association
- Switch to the previous or next item within the same directory
- Zoom, pan, and rotate
- GIF playback
- Interface localization: Simplified Chinese, Traditional Chinese, American English, Japanese, Korean, Russian, French, and German
- Themes: follow system, dark, and light
- Diagnostic export (crash logs, user settings, and runtime environment, packaged as a zip archive), shared only when the user chooses to send it

None of the above is implemented yet.

## Building

- The repository contains **no build scripts**.
- Windows app: requires the .NET SDK and the Windows App SDK; build `apps/windows/NopeView.App.csproj`.
- C codecs and the router core: compile them with MSVC into DLLs, place them in a directory the app can load, and the router discovers them by scanning that directory.
- A complete, reproducible build guide has not been written yet.

## Repository layout

The project is a single monorepo:

```
apps/
├── windows/                    Windows app (WinUI 3)
│   ├── Assets/                 brand logo (SVG source, ICO, title-bar PNG)
│   └── native/                 P/Invoke bindings and test images
├── cli/                        command-line entry point (notes only)
├── plugins/                    application plugins
│   └── abi/                    application plugin ABI
├── shell/                      platform capability layer (notes only)
└── core/
    └── codecs/                 codec core
        ├── abi/                router ABI and format ABI
        ├── router/             router: directory scan, dispatch, pixel ownership
        ├── formats/            per-format codecs (png / jpg / gif / bmp)
        └── third_party/stb/    decoding dependency
```

## License

NopeView is licensed under the **GNU Affero General Public License v3.0**. See [LICENSE](LICENSE) for the full text.
