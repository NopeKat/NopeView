# resource

Cross-platform source assets shared by every platform build.

- `nopeview.svg` - the base logo, 2048x2048 with the rounded-square frame. It is
  the source for platform-specific derivatives.
- Platform builds keep their generated or platform-only files next to their own
  app assets. For example, the Windows `.ico` and title-bar PNG live in
  `src/windows/assets/` and are derived from this SVG.
- The Android adaptive icon lives in `src/android/app/src/main/res/drawable/`
  as `ic_launcher_background.xml`, `ic_launcher_foreground.xml` and
  `ic_launcher_monochrome.xml`. All three are derived from the same `nopeview.svg`
  geometry; update them together when the base logo changes.
- Keep runtime-only and platform-specific files out of this directory.
