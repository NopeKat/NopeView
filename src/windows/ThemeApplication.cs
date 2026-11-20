using Microsoft.UI.Xaml;

namespace NopeView.App;

/// <summary>
/// Applies the stored theme to one element tree. Pinning the root keeps the
/// local Dark and Light dictionaries in App.xaml in play without touching the
/// process-wide theme.
/// </summary>
internal static class ThemeApplication
{
    internal static void Apply(FrameworkElement root, AppTheme theme)
    {
        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }
}
