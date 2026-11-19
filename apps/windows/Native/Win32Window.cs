using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Win32 window helpers that the WinAppSDK windowing APIs do not cover.
/// </summary>
internal static partial class Win32Window
{
    [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static partial int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

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
    /// Switches the system-drawn caption to the dark variant so a window with a
    /// standard title bar matches the theme pinned on its content. Attribute
    /// 20 is the current contract; 19 is the pre-20H1 fallback. A failure here
    /// only leaves the caption on the system theme, so the result is ignored.
    /// </summary>
    internal static void ApplyImmersiveDarkCaption(nint hWnd, bool dark)
    {
        if (hWnd == 0)
        {
            return;
        }

        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hWnd, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hWnd, 19, ref value, sizeof(int));
        }
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
