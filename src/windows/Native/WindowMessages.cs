using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Window message plumbing that the WinAppSDK window APIs do not expose.
/// </summary>
internal static partial class WindowMessages
{
    private const uint WM_NCDESTROY = 0x0002;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const uint WM_NCLBUTTONUP = 0x00A2;
    private const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_NCRBUTTONDBLCLK = 0x00A6;

    // The caption icon is its own hit test target, not part of the caption.
    private const int HTSYSMENU = 3;

    // The subclass id only has to be unique on the one window it is applied to.
    private const nuint CaptionSubclassId = 1;

    private delegate nint SubclassProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint refData);

    // Native code keeps the raw function pointer, so the delegate behind it
    // has to live for as long as the window does.
    private static readonly SubclassProc SubclassProcReference = SuppressCaptionMenu;
    private static readonly nint SubclassProcPointer =
        Marshal.GetFunctionPointerForDelegate(SubclassProcReference);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hWnd, nint proc, nuint id, nint refData);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hWnd, nint proc, nuint id);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static partial nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    /// <summary>
    /// Drops the window menu from the caption. A title bar stays non-client, so
    /// a right click there arrives as WM_NCRBUTTONUP and the default handling
    /// answers it with the classic Restore, Move, Size, Minimize, Close menu.
    /// A left click on the caption icon lands on HTSYSMENU and opens the same
    /// menu, so the button messages carrying that hit test are dropped too.
    /// Dragging, double click, the system buttons and the Alt+Space menu are
    /// untouched.
    /// </summary>
    internal static bool SuppressCaptionContextMenu(nint hWnd)
    {
        if (hWnd == 0)
        {
            return false;
        }

        return SetWindowSubclass(hWnd, SubclassProcPointer, CaptionSubclassId, 0);
    }

    private static nint SuppressCaptionMenu(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint refData)
    {
        switch (msg)
        {
            case WM_NCRBUTTONDOWN:
            case WM_NCRBUTTONUP:
            case WM_NCRBUTTONDBLCLK:
                return 0;

            // wParam carries the hit test code for the non-client button messages.
            case WM_NCLBUTTONDOWN:
            case WM_NCLBUTTONUP:
            case WM_NCLBUTTONDBLCLK:
                if (wParam == HTSYSMENU)
                {
                    return 0;
                }

                break;

            // The subclass outlives the message only while the window exists,
            // so it has to come off before the window goes away.
            case WM_NCDESTROY:
                RemoveWindowSubclass(hWnd, SubclassProcPointer, id);
                break;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
