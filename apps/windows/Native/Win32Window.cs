using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Win32 window helpers for native window chrome behavior that
/// AppWindowTitleBar customization does not cover.
/// </summary>
/// <remarks>
/// The system menu is suppressed by filtering messages rather than by clearing
/// the WS_SYSMENU style bit. WS_SYSMENU also governs how the system lays out the
/// caption buttons, so clearing it shifts the close button away from the visible
/// right edge and leaves a dead strip over the resize border.
/// </remarks>
internal static partial class Win32Window
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_NCLBUTTONUP = 0x00A2;
    private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_NCRBUTTONDBLCLK = 0x00A6;
    private const uint WM_EXITSIZEMOVE = 0x0232;

    private const long SC_KEYMENU = 0xF100;
    private const long VK_SPACE = 0x20;
    private const long VK_F10 = 0x79;
    private const long VK_APPS = 0x5D;

    private const nuint SubclassId = 0x4E56; // "NV"
    private const nuint PressFilterSubclassId = 0x4E57; // "NW"

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint idSubclass, nuint refData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        nint hWnd, SubclassProc pfnSubclass, nuint idSubclass);

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

    private static readonly object s_menuFilterLock = new();
    private static readonly Dictionary<nint, SubclassProc> s_menuFilters = new();

    private static readonly object s_pressFilterLock = new();
    private static readonly Dictionary<nint, SubclassProc> s_pressFilters = new();

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

    /// <summary>
    /// Blocks every path that opens the native system menu on the custom title
    /// bar: Alt+Space, Shift+F10, the Menu key, the downstream SC_KEYMENU
    /// command, keyboard context menus, and non-client right clicks. Mouse
    /// context menus inside the client area keep working. This leaves
    /// WS_SYSMENU in place so the system keeps laying out the caption buttons
    /// against the visible window edge.
    /// Returns false when the native subclass could not be installed, so the
    /// caller can log or degrade instead of assuming suppression succeeded.
    /// </summary>
    internal static bool TrySuppressSystemMenu(nint hWnd)
    {
        if (hWnd == 0)
        {
            return false;
        }

        lock (s_menuFilterLock)
        {
            if (s_menuFilters.ContainsKey(hWnd))
            {
                return true;
            }

            SubclassProc filter = MenuFilterProc;
            if (!SetWindowSubclass(hWnd, filter, SubclassId, 0u))
            {
                return false;
            }

            s_menuFilters.Add(hWnd, filter);
            return true;
        }
    }

    /// <summary>
    /// Removes the filter when its window closes. Tracking one delegate per
    /// handle avoids the old single-window guard and lets another window (or a
    /// recycled HWND) install its own filter.
    /// </summary>
    internal static void UnsuppressSystemMenu(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }

        lock (s_menuFilterLock)
        {
            if (s_menuFilters.Remove(hWnd, out var filter))
            {
                RemoveWindowSubclass(hWnd, filter, SubclassId);
            }
        }
    }

    private static nint MenuFilterProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData)
    {
        if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = wParam.ToInt64();
            // Alt+Space, Shift+F10 and the Menu key must reach neither XAML's
            // ContextRequested nor DefWindowProc.
            if ((msg == WM_SYSKEYDOWN && key == VK_SPACE) || key is VK_F10 or VK_APPS)
            {
                return 0;
            }
        }

        // Belt and braces for the downstream SC_KEYMENU path.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU)
        {
            return 0;
        }

        // An all-bits-set lParam marks a keyboard context menu; pointer requests
        // carry screen coordinates and still reach the client area.
        if (msg == WM_CONTEXTMENU && lParam == new nint(-1))
        {
            return 0;
        }

        // Non-client right clicks only ever target the caption or frame, which is
        // where the unwanted system menu used to come from.
        if (msg is WM_NCRBUTTONDOWN or WM_NCRBUTTONUP or WM_NCRBUTTONDBLCLK)
        {
            return 0;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Reports non-client left-button presses on the window so the caller can
    /// show caption button pressed feedback. Presses inside the drag region
    /// start the window move loop before the XAML buttons see the pointer, so
    /// the XAML state machine never shows pressed on its own.
    /// Returns false when the native subclass could not be installed.
    /// </summary>
    internal static bool TryInstallCaptionPressFilter(
        nint hWnd,
        Action<int, int> onCaptionPress,
        Action onCaptionRelease)
    {
        if (hWnd == 0)
        {
            return false;
        }

        lock (s_pressFilterLock)
        {
            if (s_pressFilters.ContainsKey(hWnd))
            {
                return true;
            }

            SubclassProc filter = CaptionPressFilterProc;
            if (!SetWindowSubclass(hWnd, filter, PressFilterSubclassId, 0u))
            {
                return false;
            }

            s_pressFilters.Add(hWnd, filter);
            s_pressCallbacks[hWnd] = (onCaptionPress, onCaptionRelease);
            return true;
        }
    }

    /// <summary>
    /// Removes the caption press filter when its window closes.
    /// </summary>
    internal static void UninstallCaptionPressFilter(nint hWnd)
    {
        if (hWnd == 0)
        {
            return;
        }

        lock (s_pressFilterLock)
        {
            if (s_pressFilters.Remove(hWnd, out var filter))
            {
                RemoveWindowSubclass(hWnd, filter, PressFilterSubclassId);
            }

            s_pressCallbacks.Remove(hWnd);
        }
    }

    private static readonly Dictionary<nint, (Action<int, int> Press, Action Release)> s_pressCallbacks = new();

    private static nint CaptionPressFilterProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint idSubclass, nuint refData)
    {
        // Non-client left-button messages only reach the window when the
        // press started in the drag region or on the frame, which is exactly
        // the case where the XAML buttons miss their pressed state.
        if (msg is WM_NCLBUTTONDOWN or WM_NCLBUTTONUP or WM_NCLBUTTONDBLCLK or WM_EXITSIZEMOVE)
        {
            if (s_pressCallbacks.TryGetValue(hWnd, out var callbacks))
            {
                if (msg == WM_NCLBUTTONDOWN)
                {
                    var x = (short)(lParam & 0xFFFF);
                    var y = (short)((lParam >> 16) & 0xFFFF);
                    callbacks.Press(x, y);
                }
                else
                {
                    callbacks.Release();
                }
            }
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
