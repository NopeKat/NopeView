// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.UI;

namespace NopeView.App;

/// <summary>
/// Caption button colors are drawn by the system, outside the XAML theme, so
/// every window pins them to the app theme after a theme switch. The main
/// window and the settings window share this code so both captions follow one
/// palette.
/// </summary>
internal static class WindowChrome
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x0001;

    // The desktop paints the window frame and the caption it still owns before
    // the content registers a title bar, and it picks the palette from this
    // attribute. Left unset, a dark app flashes a light caption until the frame
    // and the custom title bar cover it.
    private const int DwmaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint parameter, ref HighContrastInfo info, uint update);

    // AccessibilitySettings is a UWP contract and is not reliable for an
    // unpackaged WinUI 3 process. SystemParametersInfo exposes the same
    // high-contrast flag without activating that WinRT object.
    private static bool IsHighContrastOn()
    {
        var info = new HighContrastInfo
        {
            Size = (uint)Marshal.SizeOf<HighContrastInfo>(),
        };
        return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
            && (info.Flags & HcfHighContrastOn) != 0;
    }

    /// <summary>
    /// Applies the caption button colors to one window. A forced theme overrides
    /// the system one; "System" follows the theme the content actually resolved.
    /// </summary>
    internal static void ApplyTitleBarColors(
        Window window, FrameworkElement root, AppTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var isDark = IsDark(theme, root);
        var titleBar = window.AppWindow.TitleBar;

        // High contrast owns a separate system palette for text and surfaces.
        // Clearing the overrides lets the system draw the caption buttons with
        // that palette instead of the app's dark/light glyph colors.
        if (IsHighContrastOn())
        {
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        var buttonsClock = UiPerf.Start();
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0)
            : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
        UiPerf.Stop(buttonsClock, "chrome-buttons", isDark ? "dark" : "light");

        var frameClock = UiPerf.Start();
        ApplyFrameTheme(window, isDark);
        UiPerf.Stop(frameClock, "chrome-frame", isDark ? "dark" : "light");
    }

    /// <summary>
    /// Applies only the frame palette. That is the part the desktop reads before
    /// the window is shown, and it costs well under a millisecond, unlike the
    /// button colors, whose first assignment initializes the title bar
    /// customization and takes tens of milliseconds.
    /// </summary>
    internal static void ApplyFramePalette(
        Window window, FrameworkElement root, AppTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        ApplyFrameTheme(window, IsDark(theme, root));
    }

    private static bool IsDark(AppTheme theme, FrameworkElement root)
    {
        var effectiveTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => root.ActualTheme,
        };
        return effectiveTheme == ElementTheme.Dark;
    }

    /// <summary>
    /// Pins the frame palette the desktop uses for the window frame and for the
    /// caption it still draws until a custom title bar takes over. Without it a
    /// dark window flashes a light caption, because the frame does not follow
    /// the content theme on its own.
    /// </summary>
    private static void ApplyFrameTheme(Window window, bool isDark)
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (handle == 0)
        {
            return;
        }

        int dark = isDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmaUseImmersiveDarkMode, ref dark, sizeof(int));
    }
}
