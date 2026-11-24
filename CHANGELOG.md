# NopeView changelog

## [Unreleased] - Android codec expansion

No release yet; this section records the Android codec work now on main.

### Added

- Android decoding grew from four formats to fifteen: ICO, TGA, HDR, DNG, TIFF,
  PSD / PSB, WebP, DDS, SVG, EXR, and JPEG 2000 are wired through the Android
  CMake build.
- The first batch added the stb-backed ICO, TGA, HDR, and DNG codecs.
- The second batch added TIFF and PSD / PSB with vendored miniz.
- WebP decoding uses vendored libwebp sources with a minimal config header that
  keeps arm64 on the C fallback because the vendored tree has no NEON sources.
- DDS decoding uses the header-only bcdec implementation.
- SVG, EXR, and JPEG 2000 decoding are wired in; SVG and EXR use C++ codecs, and
  JPEG 2000 vendors OpenJPEG.
- Deterministic fixtures, debug MIME entries, and `AdditionalFormatDecodeTest`
  cover the new formats.
- `NOTICE.md` documents the provenance and MIT license for the replacement WebP
  and JP2 fixtures.

### Changed

- Android CMake enables C++ so the SVG and EXR codecs can be built.
- HDR, DNG, PSD / PSB, EXR, SVG, JPEG 2000, and DDS link libm where they use it.
- Android debug and release build scripts clean `src/android/app/.cxx/`
  alongside `build/android/` and `app/build/`.

### Known limitations

- Android still has no aligned `versionName` / `versionCode` and has not shipped
  a new release.
- The Android release build still produces an unsigned APK until a keystore is
  configured.
- HEIC / HEIF, AVIF, and JPEG XL are not ported to Android yet.
- Android instrumented tests compile today but have not run on a device or
  emulator yet.

## [0.0.2.0] - nightly

Third release, Windows x64 only this time. Everything below was verified.

### Added

- Decoding grew to fifteen formats: besides the existing PNG, JPG, GIF and BMP,
  the router now handles TGA, EXR, TIFF, HDR, DDS, PSD / PSB, DNG, JPEG 2000,
  ICO, WebP and SVG. Each stays an independent DLL wired through the format ABI;
  APNG is served by the PNG codec.
- Common extension aliases are claimed as well: jpeg / jfif / jpe for JPG, tif
  for TIFF, dib for BMP, cur for ICO, j2k / j2c / jpf / jpc / jpx for JPEG 2000,
  psb for PSD, icb / vda / vst for TGA, radiance for HDR, apng for PNG.
- Settings moved into its own top-level window: it remembers the position and
  size it was closed at, reopens at the target size and in the last state, and
  is reachable from both the toolbar and the title bar.
- Top-level navigation and an about page: the navigation shell follows Fluent
  metrics, and the about content folded into Settings instead of taking a
  navigation slot of its own.
- Bilingual interface: American English and Simplified Chinese follow the
  Windows display language and can be switched in Settings without a restart.
- Full Fluent pass: both windows use the official title bar, Settings uses a
  NavigationView and the stock SettingsCard style, open failures surface in an
  InfoBar, the status bar uses design tokens, and the command bar and settings
  details were re-laid out against the design baseline.
- Icon system: every interface icon comes from Fluent UI System Icons at its
  official 16 / 20 artwork and is drawn as inline PathIcon geometry. Sizing
  follows the ink rule - a long-to-short difference of 3 or more scales the long
  edge to 15 / 18.75, anything closer to square takes 14 / 17.5 - and every
  outline band is pulled back to one pixel.
- Settings card icons and shared text styles, viewer edge styling, and the 1:1
  command as a text button.

### Changed

- Router split into dispatch, loader, io, cache and platform units; the file
  byte cache and pixel ownership now live in the router context and are shared
  by every decode in the process.
- Main window split by responsibility, decode paths unified and serialized,
  view state centralized, commands guarded by availability, and duplicate style
  dictionaries plus copied design tokens removed.
- Animated icons are rebuilt per site instead of sharing one resource.
- Layout and artifacts: `apps/` renamed to `src/`, build output moved into the
  repository `build/` tree together with the Gradle output, and the .NET SDK
  pinned to 10.0.404.
- Fixes: Settings opens at its target size instead of a fixed one, the theme
  dropdown anchors below its button, caption button colors are pinned to the app
  theme, and the toolbar buttons are square with the empty state icon scaled
  proportionally.
- Fix (Android): after the router split the CMake target still compiled
  `router.c` alone and the link failed on undefined `nv_*` symbols; all five
  units are now compiled.

### Known limitations

- Windows x64 only for this release; Android did not ship and its `versionName`
  is still 0.0.1.0. Android codec progress is tracked under Unreleased above.
- The portable zip is self-contained and about 96 MB, so the target machine no
  longer needs the Windows App SDK runtime. The portable exe is a 7z
  self-extracting archive that unpacks into a temporary folder, starts the app,
  and removes that folder when the app exits.
- No MSIX package, no code signature, no file association; unsigned artifacts
  trigger the SmartScreen warning.
- Encoding still covers PNG, JPG and BMP only; GIF and the newly added formats
  stay decode-only.

## [0.0.1.0] - nightly

Second release, Windows x64 and Android arm64 shipped together. Everything below
was verified.

### Added

- First Android release: a single-activity viewer shell with a Compose UI and a
  native codec bridge that reuses the shared decoding library; `minSdk 26`,
  `targetSdk 36`, and `arm64-v8a` only.
- Android viewer: pinch and double-tap zoom, pan, image metadata, and EXIF
  orientation correction; oversized images are sampled tile by tile in region
  view; adaptive icons are derived inside the safe zone.
- Android GIF: frame-by-frame playback with clamped bounds, restoring the view
  position and state on return.
- Android file entry points: handles the system Open and Share intents, and the
  toolbar carries a recent file list.
- Windows Settings page: three theme states (follow system / light / dark)
  persisted to the local app data directory, with the back button and caption
  colors following the theme.

### Changed

- Caption colors resolve through the element theme: the system owns the active
  state, the inactive state takes the theme dictionary key, and the close button
  hover and pressed colors moved into the theme dictionary so no template holds
  a color literal.
- The caption keeps Mica, with a 1px divider calibrating the transition into the
  viewer; holding a caption button now raises the `Pressed` visual state through
  a Win32 subclass filter.
- Settings and the context menu dropped their extra separators and match the
  toolbar style.
- Codec fix: the preview downscale allocation now pairs with the `stbi` release
  path, removing the mismatched free.
- Test images left the repository; UIA and instrumentation tests read them from
  the workspace `.test/` directory.
- Windows artifact directories became capitalized (`Assets/`, `Native/`,
  `Styles/`) and native library loading follows `Native/x64`.
- The region view instrumentation case no longer recycles a preview bitmap it
  already handed to Compose and lets the GC take it, removing the occasional
  "recycled bitmap" crash in full runs.

### Known limitations

- The Android nightly APK is signed with the debug keystore; the release key
  must replace it before a stable channel release.
- Android is `arm64-v8a` only, with no x86 or emulator ABI.
- The Windows portable build is still framework-dependent and needs Windows App
  SDK 2.6.5 on the target machine.
- No MSIX, no code signature, no file association; GIF encoding is still
  unsupported.

## [0.0.0.0] - nightly

First release, Windows x64 only. Everything below was verified.

### Added

- Codec router core: directory scanning discovers formats, dispatch follows the
  extension, process-wide pixel ownership is registered, and file bytes are
  cached; adding a format needs no application change.
- Four format decoders: JPG, PNG, GIF and BMP as independent DLLs through the
  format ABI, with Chinese paths supported.
- Windows viewer: file dialog and drag-and-drop opening, previous and next in
  the same directory, fit and actual size, wheel and pinch zoom, pan, and 90
  degree stepwise rotation.
- GIF animation: frame-by-frame decoding, frame delay passed through, play
  and pause, and looping.
- Status bar and loading state: file name, source size, format, zoom, angle,
  frame number and pan, with a loading indicator during decode and no UI stalls.
- Encoding and Save As: PNG, BMP and JPG written through the codec ABI.
- Large images: the preview is downscaled to the viewport and the full size
  loads on demand when zooming in.
- Shortcuts, a context menu, and copy image / path commands; a self-drawn
  caption bar and a brand icon.
- Settings page: three theme states (follow system / light / dark) with JSON
  persistence in the local app data directory.
- Quality gates: 98 UIA assertions green, a WGC capture tool, and an encode
  round trip that passed.
- Release forms: portable zip and portable exe (7z self-extracting).

### Changed

- Decoding and pixel movement left the UI thread; the latest request wins and
  stale results are dropped.
- Pixels upload to the SoftwareBitmap without a copy, skipping the premultiplied
  intermediate buffer.
- A GIF frame cache plus the router file cache avoid repeated per-frame decode
  and whole-file reads.
- The `assets` directory is lowercase and test images stay out of release
  artifacts.

### Known limitations

- Windows only; the other platforms are not implemented.
- GIF encoding is unsupported and Save As reports it as unsupported.
- No MSIX, no code signature, no file association.
- The portable build is framework-dependent and needs the Windows App SDK 2.6.5
  runtime on the target machine.

### Not in this version (planned)

- Application plugins, CLI, and the platform capability layer.
- Format conversion UI, batch export, and encode quality settings.
- Ports to other platforms, localization, theming, accessibility, packaging, and
  file associations.
