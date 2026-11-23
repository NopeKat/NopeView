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

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int command);

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_MAXIMIZE = 3;

    /// <summary>
    /// Takes the window to its maximized state. The presenter request is
    /// deferred until the content is loaded, which lets the window show at its
    /// normal rectangle first; this one is applied immediately.
    /// </summary>
    internal static void Maximize(nint hWnd)
    {
        if (hWnd != 0)
        {
            ShowWindow(hWnd, SW_MAXIMIZE);
        }
    }

    /// <summary>
    /// Keeps the window off the screen. Restoring a window shows it, so a
    /// window that is still being prepared has to go back into hiding until
    /// its rectangle and content are both in place.
    /// </summary>
    internal static void Hide(nint hWnd)
    {
        if (hWnd != 0)
        {
            ShowWindow(hWnd, SW_HIDE);
        }
    }

    /// <summary>
    /// Moves and resizes a window through Win32. The windowing API defers its
    /// own resize until the content is loaded, which lets the framework size
    /// show up first; this one is applied immediately, so a window can open at
    /// its intended rectangle instead of jumping to it one frame later.
    /// </summary>
    internal static bool SetBounds(nint hWnd, int x, int y, int width, int height)
    {
        if (hWnd == 0)
        {
            return false;
        }

        return SetWindowPos(
            hWnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

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
