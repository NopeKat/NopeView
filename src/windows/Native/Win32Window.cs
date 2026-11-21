using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Win32 window helpers that the WinAppSDK windowing APIs do not cover.
/// </summary>
internal static partial class Win32Window
{
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);

    /// <summary>
    /// Whether the window is minimized.
    /// </summary>
    internal static bool IsMinimized(nint hWnd)
    {
        return hWnd != 0 && IsIconic(hWnd);
    }

    /// <summary>
    /// Effective rasterization scale (DPI / 96) of the window, or 1.0 when the
    /// DPI cannot be read. Callers use it to convert physical pixels reported
    /// by Win32 and AppWindow into XAML units.
    /// </summary>
    internal static double GetScaleFactor(nint hWnd)
    {
        var dpi = GetDpiForWindow(hWnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }
}
