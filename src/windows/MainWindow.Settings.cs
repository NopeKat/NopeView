// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using NopeView.App.Native;
using System;
using Windows.Graphics;

namespace NopeView.App;

// Settings plumbing. Settings and the about content live in a modal window of
// their own, so this window only ever shows the viewer; the command bar, the
// context menu and Ctrl+, all open that window.
public sealed partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;

    // Builds the settings window once, hidden, a moment after the viewer
    // settles. A timer rather than a load callback keeps the build out of the
    // startup path, where it would delay the first image.
    private readonly DispatcherTimer _settingsPrewarmTimer = new();
    private const double SettingsPrewarmDelayMs = 2000;

    // Set while the main window is closing. Closing the settings window at the
    // same time must not try to activate a window that is already gone.
    private bool _closing;

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    // Ctrl+, and Ctrl+2 both bring the settings window forward. It is a modal
    // window, so the keys cannot toggle a surface: they open the window on the
    // first press and focus it on every later one.
    private void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenSettingsWindow();
        args.Handled = true;
    }

    /// <summary>
    /// Opens the settings window, or brings the open one forward. The window is
    /// owned by the viewer and the viewer is blocked while it is up, so only the
    /// stored values and the theme callbacks are shared between the two.
    /// </summary>
    private void OpenSettingsWindow()
    {
        EnsureSettingsWindow();

        // The viewer ignores mouse and keyboard until the settings window
        // closes, the way the owner of a dialog does.
        Win32Window.SetEnabled(Handle, false);
        var showClock = UiPerf.Start();
        _settingsWindow!.ShowAndFocus();
        UiPerf.Stop(showClock, "settings-show", "activate");
    }

    /// <summary>
    /// Builds the settings window once, hidden. The page is expensive enough
    /// that a first open used to show nothing for about 370 ms, so the build is
    /// moved to a moment when the viewer is idle and the open itself only has
    /// to show a window that already exists.
    /// </summary>
    private SettingsWindow EnsureSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            // The window opens on the display that hosts the viewer.
            var workArea = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            var buildClock = UiPerf.Start();
            var settingsWindow = new SettingsWindow(
                _settings, ApplyTheme, ApplyLanguage, ApplyImageFrame, workArea);
            UiPerf.Stop(buildClock, "settings-build", "hidden build");

            // Settings is modal: the viewer owns it, which keeps it above the
            // viewer and out of the taskbar, and keeps the viewer blocked while
            // the options are being changed. The owner has to be set before the
            // window is shown, because Windows reads it when the window is
            // first registered.
            Win32Window.SetOwner(settingsWindow.Handle, Handle);
            settingsWindow.Closed += SettingsWindow_Closed;
            settingsWindow.Hidden += SettingsWindow_Hidden;

            _settingsWindow = settingsWindow;
        }

        return _settingsWindow;
    }

    private void SettingsPrewarmTimer_Tick(object? sender, object e)
    {
        _settingsPrewarmTimer.Stop();
        if (_closing || _settingsWindow is not null)
        {
            return;
        }

        // An open owns the UI thread for as long as it decodes. Building the
        // page in the middle of it would be felt as a delayed image, so the
        // prewarm waits for the next idle moment instead.
        if (LoadingRing.Visibility == Visibility.Visible)
        {
            _settingsPrewarmTimer.Start();
            return;
        }

        var clock = UiPerf.Start();
        EnsureSettingsWindow();
        UiPerf.Stop(clock, "settings-prewarm", "hidden build");
    }

    // A modal window has to hand input back, and an owned window is closed
    // explicitly anyway when the viewer exits, so the process does not stay
    // alive with a window that has nothing left to configure.
    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        _settingsWindow = null;
        RestoreViewerInput();
    }

    // The settings window is hidden rather than closed, so input comes back
    // through the same path a close would take, just without a destroyed page.
    private void SettingsWindow_Hidden()
    {
        RestoreViewerInput();
    }

    /// <summary>
    /// Hands focus and input back to the viewer, the way a dialog does when it
    /// goes away. Both the hidden and the closed path end here.
    /// </summary>
    private void RestoreViewerInput()
    {
        if (!_closing)
        {
            Win32Window.SetEnabled(Handle, true);
            Activate();
        }
    }

    private void ApplyLanguage(string language)
    {
        Localization.Apply(language);
        ApplyLocalization();
    }

    private void ApplyTheme(AppTheme theme)
    {
        // System means "no override", so the OS theme flows through. Explicit
        // choices pin the root element, which makes the local Dark/Light
        // dictionaries in App.xaml take effect. The settings window applies the
        // same theme to its own root before forwarding it here.
        ThemeApplication.Apply(RootGrid, theme);
        ApplyTitleBarColors(theme);
    }

    private void ApplyThemeEarly(AppTheme theme)
    {
        // The caption button colors are deferred until the window has laid out;
        // see RootGrid_Loaded. Only the frame palette, which the desktop reads
        // before the window is shown, is applied here.
        ThemeApplication.Apply(RootGrid, theme);
        WindowChrome.ApplyFramePalette(this, RootGrid, theme);
    }

    // The caption buttons are drawn by the system, outside the XAML theme.
    // WindowChrome pins their colors so a forced dark theme cannot leave
    // light-theme black glyphs on a dark Mica bar.
    private void ApplyTitleBarColors(AppTheme theme)
    {
        WindowChrome.ApplyTitleBarColors(this, RootGrid, theme);
    }

    private void ApplyImageFrame(ImageFrameStyle frame)
    {
        var oldFit = GetFitScale(_imageFrameStyle);
        var newFit = GetFitScale(frame);
        var wasFit = IsAtFit;

        _imageFrameStyle = frame;
        ApplyImageDecoration();
        UpdateImageFrameSize();

        // A shadow inset changes the fitted box. Keep the bitmap at the same
        // on-screen scale when the user was zoomed in; at fit the image simply
        // refits to the new box.
        if (!wasFit && oldFit > 0 && newFit > 0)
        {
            _view.Zoom = Math.Clamp(_view.Zoom * oldFit / newFit, MinZoom, MaxZoom);
        }

        ApplyViewTransform();
        UpdateViewStatus();
    }
}
