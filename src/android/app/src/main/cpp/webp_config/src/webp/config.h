// Minimal libwebp configuration for the Android arm64 C fallback path.
// The vendored tree has no NEON sources, so no instruction set macros are
// defined here. WebP_HAVE_* must stay undefined: libwebp checks definedness,
// not the value, and would otherwise call missing NEON init functions.
#ifndef WEBP_ANDROID_CONFIG_H_
#define WEBP_ANDROID_CONFIG_H_

#define WEBP_DISABLE_STATS 1
#define WEBP_USE_THREAD 0

#endif  // WEBP_ANDROID_CONFIG_H_
