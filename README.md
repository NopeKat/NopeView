# NopeView

A fast, lightweight, and extensible cross-platform image viewer.

> **Status: early development.** The Windows side can open JPG, PNG, GIF, and BMP through the file picker or drag and drop, decode them through the router, display them in the content area, step to the previous or next image in the same directory, zoom, pan, or rotate the view, and play animated GIFs. The custom title bar is in place; there is no release yet.

## Highlights

- **Fast and light.** Startup speed, smooth browsing, and format coverage are the primary goals.
- **Consistent experience.** Localization and themes (follow system / dark / light) follow each platform's native conventions.
- **Seamless updates.** Updates use an A/B slot model: the new version is written to the inactive slot, verified, then activated with a silent restart. Failures roll back silently.

## Current progress

- **Codecs**: the router core and the decoders for PNG, JPG, GIF, and BMP are implemented. The encode entry point exists, but all four formats return "unsupported".
- **Windows app**: WinUI 3 viewer MVP. The content is extended into the title bar, with a self-drawn title bar whose minimize / maximize / close buttons are XAML buttons using Fluent `PathIcon` (the native title bar strip and its tooltips are collapsed), PerMonitorV2 declared, and the NopeView brand logo shown at the left. The app icon is set both in the executable and on the running window for Explorer, taskbar, Alt-Tab and Start. The content area renders decoded images, with file picker and drag-and-drop opening, previous/next navigation within the same directory, Fit and 1:1 modes, mouse-wheel zoom, drag pan, rotation, and a view status line. Animated GIFs play through per-frame decoding with the frame delay from the codec; playback can be paused, resumed, and looped.
- **Three ABI boundaries**: router (`nv_router_abi.h`), format (`nv_format_abi.h`), and application plugin (`nv_plugin_abi.h`) are all in place. Router and format are at `0.0.0.1`; the plugin ABI is still at `0.0.0.0` (the `x.y.z.n` scheme).
- **Shared assets**: the base logo SVG lives in `shared/resource/`; platform builds derive their own icon and title-bar files from it.
- **Other modules**: the CLI, application plugins, and the platform capability layer contain only directories and notes; nothing is implemented. The CLI, plugins, and platform capability layer live under `shared/` as non-GUI foundations.

## Supported platforms

| Platform | Minimum version | UI toolkit | Status |
| --- | --- | --- | --- |
| Windows | Windows 10 | WinUI 3 (Windows App SDK) | Viewer MVP |
| Mac | macOS 12 | SwiftUI | Not started |
| Linux | kernel 4.x and later | GTK 4 | Not started |
| Android | Android 8 | Jetpack Compose | Viewer MVP |
| Apple (iPhone / iPad) | iOS 16 | SwiftUI | Not started |
| Harmony | Harmony 4 | ArkUI | Not started |

## Supported formats

| Format | Decode | Encode | Capability flags |
| --- | --- | --- | --- |
| PNG | Yes | No | `DECODE` |
| JPG | Yes | No | `DECODE` |
| GIF | Yes, per frame | No | `DECODE` + `ANIMATED` |
| BMP | Yes | No | `DECODE` |

Decoding is built on `apps/shared/codec/third_party/stb/stb_image.h`. More formats are planned; the format roadmap will be announced once it is defined.

## Planned capabilities

- File association (planned with MSIX packaging)
- Interface localization: Simplified Chinese, Traditional Chinese, American English, Japanese, Korean, Russian, French, and German
- Themes: follow system, dark, and light
- Diagnostic export (crash logs, user settings, and runtime environment, packaged as a zip archive), shared only when the user chooses to send it

File opening through the picker and drag and drop, previous/next navigation, and zoom, pan, and rotate are implemented; the rest is still planned.

## Building

- The repository contains **no build scripts**.
- Windows app: requires the .NET SDK and the Windows App SDK; build `apps/windows/NopeView.App.csproj`.
- C codecs, the router core, and the file list service: compile them with MSVC into DLLs, place them in `apps/windows/native/x64/`, and the app loads them from there.
- The current Windows build is driven by local scripts kept outside the repository; a complete reproducible build guide is not committed yet.

## Repository layout

The project is a single monorepo:

```
apps/
├── windows/                    Windows app (WinUI 3)
│   ├── assets/                 derived ICO and title-bar PNG
│   └── native/                 P/Invoke bindings and test images
└── shared/
    ├── cli/                    command-line entry point (notes only)
    ├── platform/               platform capability layer (notes only)
    ├── plugin/                 application plugins
    │   └── abi/                application plugin ABI
    ├── resource/               cross-platform source assets (base logo SVG)
    ├── filelist/               shared file list service
    │   └── abi/                file list ABI
    └── codec/                  codec core
        ├── abi/                router ABI and format ABI
        ├── router/             router: directory scan, dispatch, pixel ownership
        ├── formats/            per-format codecs (png / jpg / gif / bmp)
        └── third_party/stb/    decoding dependency
```

## License

NopeView is licensed under the **GNU Affero General Public License v3.0**. See [LICENSE](LICENSE) for the full text.
