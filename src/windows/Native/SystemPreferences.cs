using Microsoft.Win32;

namespace NopeView.App.Native;

/// <summary>
/// Preferences the desktop keeps for every application window in the session.
/// </summary>
internal static class SystemPreferences
{
    private const string AccessibilityKey = @"HKEY_CURRENT_USER\Software\Microsoft\Accessibility";
    private const string MaximizeWindowsValue = "MaximizeWindowsByDefault";

    /// <summary>
    /// Whether Settings, Accessibility, Visual effects asks every application
    /// window to open maximized. The desktop applies that to windows that open
    /// with the default state, so a window that picks its own rectangle has to
    /// read the preference and maximize itself.
    /// </summary>
    internal static bool MaximizeWindowsByDefault()
    {
        object? value = Registry.GetValue(AccessibilityKey, MaximizeWindowsValue, null);

        return value is int enabled && enabled != 0;
    }
}
