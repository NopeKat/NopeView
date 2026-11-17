# resource

Cross-platform source assets shared by every platform build.

- `nopeview.svg` - the base logo, 2048x2048 with the rounded-square frame. It is
  the source for platform-specific derivatives.
- Platform builds keep their generated or platform-only files next to their own
  app assets. For example, the Windows `.ico` and title-bar PNG live in
  `apps/windows/assets/` and are derived from this SVG.
- Keep runtime-only and platform-specific files out of this directory.
