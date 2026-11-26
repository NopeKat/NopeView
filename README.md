# NopeView

A fast, lightweight, and extensible cross-platform image viewer.

> **Status: early development.** The Windows viewer is published as a nightly `0.0.2.0` build; Android last shipped with `0.0.1.0`. The Windows app opens 18 image formats through the router; Android opens 15 of them, with HEIC/HEIF, AVIF, and JPEG XL still Windows-only. It supports three themes and an English/Chinese UI, and ships as self-contained portable packages. MSIX, signing, and file association are not in place yet.

## Highlights

- **Fast and light.** Startup speed, smooth browsing, and format coverage are the primary goals.
- **Consistent experience.** Localization and themes (follow system / dark / light) follow each platform's native conventions.
- **Planned: seamless updates.** An A/B slot model is envisioned: write the new version to the inactive slot, verify it, then activate it with a silent restart. Nothing is implemented yet.

## Current progress

- **Codecs.** The router core is split into dispatch, loader, I/O, cache, and platform units. Eighteen format codecs decode images; Android builds 15 of them, and HEIC/HEIF, AVIF, and JPEG XL are not ported to Android yet. PNG, JPG, and BMP also encode. PNG (APNG), GIF, and WebP report the animated capability. The last file read is cached per context, so animation frames do not re-read the file.
- **Windows app**: WinUI 3 viewer MVP. The app uses the official WinUI 3 `TitleBar` and `CommandBar`, with the content extended into the title bar, PerMonitorV2 declared, and the NopeView brand logo shown at the left. The app icon is set both in the executable and on the running window for Explorer, taskbar, Alt-Tab and Start. The content area renders decoded images, with file picker and drag-and-drop opening, previous/next navigation within the same directory, Fit and 1:1 modes, mouse-wheel zoom, drag pan, rotation, and a view status line. Animated GIFs play through per-frame decoding with the frame delay from the codec; playback can be paused, resumed, and looped. Interface text ships in American English and Simplified Chinese, follows the Windows display language by default, and can be switched from Settings without restarting. Settings opens as a separate modal window with its own official title bar, remembers its position and size, and is reachable from the command bar and the caption. The navigation shell hosts the viewer and settings; the about information lives in settings. Open failures surface in an `InfoBar`. Interface icons use Fluent UI System Icons rendered as inline `PathIcon` geometry at 16, 20, and 48 grid sizes.
- **ABI boundaries.** router (`nv_router_abi.h`) `0.0.0.5`, format (`nv_format_abi.h`) `0.0.0.2`, application plugin (`nv_plugin_abi.h`) `0.0.0.0`, and file list (`nv_files_abi.h`) `0.0.0.0`, in `x.y.z.n` form.
- **Shared assets**: the base logo SVG lives in `shared/resource/`; platform builds derive their own icon and title-bar files from it.
- **Other modules**: the CLI, application plugins, and the platform capability layer contain only directories and notes; nothing is implemented. The CLI, plugins, and platform capability layer live under `shared/` as non-GUI foundations.

## Supported platforms

| Platform | Minimum version | UI toolkit | Status |
| --- | --- | --- | --- |
| Windows | Windows 10 | WinUI 3 (Windows App SDK) | Viewer MVP; 0.0.2.0 nightly |
| Android | Android 8 | Jetpack Compose | Viewer MVP; last released in 0.0.1.0 |
| Mac | macOS 12 | SwiftUI | Not started |
| Linux | kernel 4.x and later | GTK 4 | Not started |
| Apple (iPhone / iPad) | iOS 16 | SwiftUI | Not started |
| Harmony | Harmony 4 | ArkUI | Not started |

## Supported formats

| Format | Extensions | Decode | Encode | Android | Capability flags |
| --- | --- | --- | --- | --- | --- |
| PNG / APNG | `png`, `apng` | Yes | Yes | Yes | `DECODE`, `ENCODE`, `ANIMATED` |
| JPG | `jpg`, `jpeg`, `jfif`, `jpe` | Yes | Yes | Yes | `DECODE`, `ENCODE` |
| GIF | `gif` | Yes | No | Yes | `DECODE`, `ANIMATED` |
| BMP | `bmp`, `dib` | Yes | Yes | Yes | `DECODE`, `ENCODE` |
| WebP | `webp` | Yes | No | Yes | `DECODE`, `ANIMATED` |
| HEIC / HEIF | `heic`, `heif` | Yes | No | No | `DECODE` |
| AVIF | `avif` | Yes | No | No | `DECODE` |
| JPEG XL | `jxl` | Yes | No | No | `DECODE` |
| TIFF | `tif`, `tiff` | Yes | No | Yes | `DECODE` |
| TGA | `tga`, `icb`, `vda`, `vst` | Yes | No | Yes | `DECODE` |
| ICO | `ico`, `cur` | Yes | No | Yes | `DECODE` |
| JPEG 2000 | `jp2`, `j2k`, `j2c`, `jpf`, `jpc`, `jpx` | Yes | No | Yes | `DECODE` |
| PSD / PSB | `psd`, `psb` | Yes | No | Yes | `DECODE` |
| DDS | `dds` | Yes | No | Yes | `DECODE` |
| HDR | `hdr`, `radiance` | Yes | No | Yes | `DECODE` |
| EXR | `exr` | Yes | No | Yes | `DECODE` |
| DNG | `dng` | Yes | No | Yes | `DECODE` |
| SVG | `svg`, `svgz` | Yes | No | Yes | `DECODE` |

Codecs live in separate libraries under `src/shared/codec/formats/` and are discovered by the router at runtime. Vendored dependencies live in `src/shared/codec/third_party/` (bcdec, dav1d, highway, libde265, libheif, libjxl, libwebp, miniz, nanosvg, openjpeg, stb, tinyexr). The HEIC/HEIF codec uses libheif for the container and libde265 for the HEVC pixels; the AVIF codec uses the same container reader with dav1d for the AV1 pixels. The JPEG XL codec uses libjxl's decoder-only C API with Highway for the pixel transforms; JPEG reconstruction and the box-content path stay compiled out. Android currently compiles 15 of the 18 codecs, so HEIC/HEIF, AVIF, and JPEG XL are not ported yet and remain Windows-only.

## Planned capabilities

- File association (planned with MSIX packaging)
- Interface localization: American English and Simplified Chinese are implemented; Traditional Chinese, Japanese, Korean, Russian, French, and German are planned
- Themes: follow system, dark, and light
- A/B slot updates with silent rollback
- Diagnostic export (crash logs, user settings, and runtime environment, packaged as a zip archive), shared only when the user chooses to send it

File opening through the picker and drag and drop, previous/next navigation, and zoom, pan, and rotate are implemented; the rest is still planned.

## Known limitations

- MSIX and code signing are not enabled; unsigned Windows packages trigger SmartScreen warnings. File association is planned with MSIX.
- The current Windows package is x64 only and self-contained; the zip is about 96 MB. A smaller framework-dependent build is possible with `-Runtime framework`.
- Encoding is limited to PNG, JPG, and BMP. GIF and the other formats decode only.
- Android is `arm64-v8a` only; x86/x86_64 emulator support is not available yet. The `0.0.1.0` release APK was unsigned, and current main has not produced a new package.
- Android instrumented tests compile today, but the new decode cases have not run on a device or emulator yet.
- Android WebP uses the libwebp C fallback because the vendored tree has no NEON sources; decode performance is unmeasured.
- Android now has PSD, EXR, and DNG test assets; the Windows `0.0.2.0` smoke test had no local samples for those formats.
- AVIF and JPEG XL are decoded as still images; animation is not supported.

## Releases

### Unreleased (Android)

- Wired the stb-backed `ico`, `tga`, `hdr`, and `dng` codecs into Android.
- Wired `tiff` and `psd` into Android with vendored miniz.
- Wired `webp` into Android with the libwebp C fallback and a minimal config header.
- Wired `dds` into Android with the header-only bcdec implementation.
- Wired `svg`, `exr`, and `jp2` into Android with C++ enabled and OpenJPEG vendored.

### Nightlies

- `0.0.2.0` nightly, 2026-11-22: Windows x64 only. Portable zip and 7-Zip self-extracting exe, both self-contained; tagged `0.0.2.nightly` and marked as a pre-release. No MSIX, no symbols package, no Android build; Android codec progress is tracked under Unreleased above.
- `0.0.1.0` nightly, 2026-11-18: Windows portable packages plus an unsigned Android arm64 APK; tagged `0.0.1.nightly` and marked as a pre-release.

## Building

- The repository contains no build scripts. The working workspace keeps them in `.scripts/`; run all commands with PowerShell from the repository root.
- Windows app: requires the .NET SDK and the Windows App SDK; build `src/windows/NopeView.App.csproj`.
- Android app: requires JDK 17 and the Android SDK; build `src/android` with Gradle.
- C codecs, the router core, and the file list service: compile them with MSVC into DLLs, place them in `src/windows/native/x64/`, and the app loads them from there.
- `global.json` pins .NET SDK 10.0.404 with `rollForward: disable`. The file is gitignored, so a fresh clone needs it placed locally before building.
- Build scripts clean `build/windows/` and the app's `bin/` / `obj/` first, then build from scratch. Android scripts clean `build/android/`, `src/android/app/build/`, and `src/android/app/.cxx/`. A build with any warning or error is treated as failed. All output goes to the repository's `build/` directory (`windows/`, `android/`, `package/`); it is regenerable and gitignored.
- Android release builds still produce an unsigned APK until a keystore is configured.

```powershell
& .\.scripts\build-windows-release.ps1
& .\.scripts\build-android-debug.ps1
& .\.scripts\build-android-release.ps1
& .\.scripts\package-all.ps1 -Configuration Release -Channel nightly
```

## Testing

- Windows UI automation uses UIA only. Cases and artifacts live in the workspace's `.tests/`, outside the repository.
- Windows screenshots use `Windows.Graphics.Capture`.
- Format smoke tests live in `src/shared/codec/tests/`.
- Android instrumented tests live in `src/android/app/src/androidTest/` and cover decode, viewer shell, restore, and memory cases.
- Their image fixtures are kept outside the repository so one copy serves every platform. Stage them onto the device under the app-specific external files root (`Android/data/app.android.nopeview/files/test-assets`) before an instrumented run; a run without staged fixtures is skipped with that directory named.
- Android instrumented tests are compile-verified only; they have not run on a device or emulator yet.

## Repository layout

The project is a single monorepo:

```
src/
├── windows/                    Windows app (WinUI 3)
│   ├── assets/                 derived ICO and title-bar PNG
│   ├── Native/                 P/Invoke bindings; DLLs load from Native/x64
│   ├── Strings/                en-US / zh-Hans Resources.resw
│   ├── SettingsWindow.xaml     modal settings window
│   └── MainWindow.*.cs         viewer split by concern
├── android/                    Android app (Jetpack Compose, Gradle)
│   └── app/
│       ├── src/main/cpp/       CMakeLists.txt and nopeview_jni.c for the native router/JNI bridge
│       │   └── webp_config/    minimal libwebp config so arm64 uses the C fallback
│       └── src/androidTest/
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
        ├── router/             dispatch, loader, I/O, cache, platform
        ├── formats/            one library per format (18 formats)
        ├── tests/              format smoke tests
        └── third_party/        bcdec, dav1d, highway, libde265, libheif,
                                libjxl, libwebp, miniz, nanosvg, openjpeg, stb,
                                tinyexr
```

## License

NopeView is licensed under the **GNU Affero General Public License v3.0**. See [LICENSE](LICENSE) for the full text.
