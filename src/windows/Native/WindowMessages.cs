using System.Runtime.InteropServices;

namespace NopeView.App.Native;

/// <summary>
/// Window message plumbing that the WinAppSDK window APIs do not expose.
/// </summary>
internal static class WindowMessages
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

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private const int SW_HIDE = 0;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = unchecked((int)0x40000000);

    // The shell hosts the caption button tooltip in this window class.
    private const string CaptionTooltipClass = "MicrosoftWindowsTooltip";

    // The subclass id only has to be unique on the one window it is applied to.
    private const nuint CaptionSubclassId = 1;

    // The hook keeps its raw function pointer too, so the delegate stays parked.
    private static readonly WinEventProc WinEventProcReference = HideCaptionTooltip;
    private static readonly nint WinEventProcPointer =
        Marshal.GetFunctionPointerForDelegate(WinEventProcReference);

    private static nint _tooltipHook;

    // The hook belongs to the process, so it has to outlive the first window
    // that asked for it. Every window wearing the subclass is counted, and the
    // hook only comes off with the last one.
    private static int _captionWindows;

    private delegate nint SubclassProc(
        nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nint refData);

    private delegate void WinEventProc(
        nint hook, uint evt, nint hWnd, int idObject, int idChild, uint thread, uint time);

    // Native code keeps the raw function pointer, so the delegate behind it
    // has to live for as long as the window does.
    private static readonly SubclassProc SubclassProcReference = SuppressCaptionMenu;
    private static readonly nint SubclassProcPointer =
        Marshal.GetFunctionPointerForDelegate(SubclassProcReference);

    /// <summary>
    /// Takes the window menu and the caption button tooltips off the caption.
    /// The title bar stays non-client, so the shell owns both: a right click on
    /// it arrives as WM_NCRBUTTONUP and opens the classic window menu, a click
    /// on the caption icon arrives on HTSYSMENU and opens the same menu, and
    /// hovering one of the three system buttons makes the shell create a
    /// MicrosoftWindowsTooltip window under it.
    /// The menu is dropped at the message level. The tooltip cannot be: the
    /// shell builds the caption button hover state, and both the tip and the
    /// button highlight come out of it, so dropping the hover messages would
    /// take the highlight with it. The tip window belongs to this process
    /// though, so the window events are watched and the tip is hidden instead.
    /// Dragging, double click, the button highlight, the system buttons
    /// themselves and the Alt+Space menu are untouched.
    /// </summary>
    internal static bool SuppressCaptionChrome(nint hWnd)
    {
        if (hWnd == 0)
        {
            return false;
        }

        bool applied = NativeMethods.SetWindowSubclass(
            hWnd, SubclassProcPointer, CaptionSubclassId, 0);

        if (applied)
        {
            _captionWindows++;
        }

        if (_tooltipHook == 0)
        {
            _tooltipHook = NativeMethods.SetWinEventHook(
                EVENT_OBJECT_CREATE,
                EVENT_OBJECT_SHOW,
                module: 0,
                proc: WinEventProcPointer,
                processId: NativeMethods.GetCurrentProcessId(),
                threadId: 0,
                flags: WINEVENT_OUTOFCONTEXT);
        }

        return applied;
    }

    private static void HideCaptionTooltip(
        nint hook, uint evt, nint hWnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hWnd == 0 || idObject != 0)
        {
            return;
        }

        // Only the shell's caption button tip is of interest, and only when it
        // is a top level window, which keeps any child control out of reach.
        if ((NativeMethods.GetWindowLongW(hWnd, GWL_STYLE) & WS_CHILD) != 0)
        {
            return;
        }

        if (!IsClass(hWnd, CaptionTooltipClass))
        {
            return;
        }

        NativeMethods.ShowWindow(hWnd, SW_HIDE);
    }

    private static bool IsClass(nint hWnd, string name)
    {
        const int Capacity = 128;

        nint buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));

        try
        {
            int length = NativeMethods.GetClassNameW(hWnd, buffer, Capacity);

            if (length <= 0 || length >= Capacity)
            {
                return false;
            }

            string? actual = Marshal.PtrToStringUni(buffer, length);

            return actual is not null && string.Equals(actual, name, StringComparison.Ordinal);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
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
            // so it has to come off before the window goes away. The hook is
            // process wide, so it waits for the last window instead of leaving
            // with the first one.
            case WM_NCDESTROY:
                NativeMethods.RemoveWindowSubclass(hWnd, SubclassProcPointer, id);

                if (_captionWindows > 0)
                {
                    _captionWindows--;
                }

                if (_captionWindows == 0 && _tooltipHook != 0)
                {
                    NativeMethods.UnhookWinEvent(_tooltipHook);
                    _tooltipHook = 0;
                }

                break;
        }

        return NativeMethods.DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
