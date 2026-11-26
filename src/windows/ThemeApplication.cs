// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

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
        var requested = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        // Assigning RequestedTheme re-resolves theme resources for the whole
        // tree. An element that already asks for the same theme has nothing
        // to re-resolve, which is the common case at startup.
        if (root.RequestedTheme != requested)
        {
            var assignClock = UiPerf.Start();
            root.RequestedTheme = requested;
            UiPerf.Stop(assignClock, "theme-assign", requested.ToString());
        }
    }
}
