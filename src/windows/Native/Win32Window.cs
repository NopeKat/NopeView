// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Win32 window helpers that the WinAppSDK windowing APIs do not cover.
/// </summary>
internal static class Win32Window
{
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int SW_MAXIMIZE = 3;
    private const int GWLP_HWNDPARENT = -8;

    // Lets any process take the foreground. Only the process Windows just
    // activated may grant this, which is exactly the launch that hands over.
    private const int AsfwAny = -1;

    // The desktop paints the frame and the client area it has no content for
    // from its own palette; a cloaked window is skipped altogether.
    private const int DwmaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hWnd, int attribute, ref int value, int size);

    /// <summary>
    /// Hides one window from the desktop without hiding it from the application.
    /// A window that is being prepared can be shown, activated and rendered while
    /// cloaked, and the desktop starts drawing it only once it is uncloaked, so
    /// the frames before its content is ready never reach the screen.
    /// </summary>
    internal static void Cloak(nint hWnd, bool cloak)
    {
        if (hWnd == 0)
        {
            return;
        }

        int value = cloak ? 1 : 0;
        DwmSetWindowAttribute(hWnd, DwmaCloak, ref value, sizeof(int));
    }

    /// <summary>
    /// Makes one window the owner of another. An owned window stays above the
    /// window that owns it and has no taskbar button of its own, which is the
    /// frame a modal window needs. Windows reads the owner when it first
    /// registers the window, so this has to run before the window is shown.
    /// </summary>
    internal static void SetOwner(nint hWnd, nint owner)
    {
        if (hWnd != 0 && owner != 0)
        {
            NativeMethods.SetWindowLongPtrW(hWnd, GWLP_HWNDPARENT, owner);
        }
    }

    /// <summary>
    /// Turns input for a window on or off. A disabled window still paints,
    /// it just stops accepting mouse and keyboard, which is what holds the
    /// owner of a modal window still while the modal window is open.
    /// </summary>
    internal static void SetEnabled(nint hWnd, bool enabled)
    {
        if (hWnd != 0)
        {
            NativeMethods.EnableWindow(hWnd, enabled);
        }
    }

    /// <summary>
    /// Takes the window to its maximized state. The presenter request is
    /// deferred until the content is loaded, which lets the window show at
    /// its normal rectangle first; this one is applied immediately.
    /// </summary>
    internal static void Maximize(nint hWnd)
    {
        if (hWnd != 0)
        {
            NativeMethods.ShowWindow(hWnd, SW_MAXIMIZE);
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
            NativeMethods.ShowWindow(hWnd, SW_HIDE);
        }
    }

    /// <summary>
    /// Shows a window that was hidden instead of closed. It keeps its
    /// rectangle and its content, so bringing it back costs no rebuild.
    /// </summary>
    internal static void Show(nint hWnd)
    {
        if (hWnd != 0)
        {
            NativeMethods.ShowWindow(hWnd, SW_SHOW);
        }
    }

    /// <summary>
    /// Restores a minimized window to the state it had before it was minimized.
    /// </summary>
    internal static void Restore(nint hWnd)
    {
        if (hWnd != 0)
        {
            NativeMethods.ShowWindow(hWnd, SW_RESTORE);
        }
    }

    /// <summary>
    /// Grants the right to set the foreground window to another process.
    /// Windows gives that right only to the process the user just activated,
    /// so a launch that hands over has to pass it on first.
    /// </summary>
    internal static void AllowForeground()
    {
        NativeMethods.AllowSetForegroundWindow(AsfwAny);
    }

    /// <summary>
    /// Brings one window to the front. The call only takes effect while the
    /// calling process holds the foreground right, which AllowForeground
    /// grants ahead of a handover.
    /// </summary>
    internal static void BringToForeground(nint hWnd)
    {
        if (hWnd != 0)
        {
            NativeMethods.SetForegroundWindow(hWnd);
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

        return NativeMethods.SetWindowPos(
            hWnd, 0, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Whether the window is minimized.
    /// </summary>
    internal static bool IsMinimized(nint hWnd)
    {
        return hWnd != 0 && NativeMethods.IsIconic(hWnd);
    }

    /// <summary>
    /// Effective rasterization scale (DPI / 96) of the window, or 1.0 when the
    /// DPI cannot be read. Callers use it to convert physical pixels reported
    /// by Win32 and AppWindow into XAML units.
    /// </summary>
    internal static double GetScaleFactor(nint hWnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hWnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }
}
