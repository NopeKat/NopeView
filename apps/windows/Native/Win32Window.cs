using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Win32 window helpers that the WinAppSDK windowing APIs do not cover.
/// </summary>
internal static partial class Win32Window
{
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hWnd, int index, nint value);

    private const int GWLP_HWNDPARENT = -8;

    /// <summary>
    /// Makes a secondary window owned by the viewer. An owned window stays
    /// above its owner, minimizes with it, and does not get its own taskbar
    /// button, so the app still reads as a single task.
    /// </summary>
    internal static void SetOwner(nint hWnd, nint ownerHwnd)
    {
        if (hWnd == 0 || ownerHwnd == 0)
        {
            return;
        }

        SetWindowLongPtr(hWnd, GWLP_HWNDPARENT, ownerHwnd);
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
