using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.Graphics;

namespace NopeView.App;

// Settings plumbing. Settings and the about content live in a window of their
// own, so this window only ever shows the viewer; the command bar, the context
// menu and Ctrl+, all open that window.
public sealed partial class MainWindow : Window
{
    private SettingsWindow? _settingsWindow;

    // Set while the main window is closing. Closing the settings window at the
    // same time must not try to activate a window that is already gone.
    private bool _closing;

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    // Ctrl+, and Ctrl+2 both bring the settings window forward. It is a
    // separate window now, so the keys cannot toggle a surface: they open the
    // window on the first press and focus it on every later one.
    private void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenSettingsWindow();
        args.Handled = true;
    }

    /// <summary>
    /// Opens the settings window, or brings the open one forward. The window is
    /// not owned by this window, so it keeps its own taskbar button and closes on
    /// its own; only the stored values and the theme callbacks are shared.
    /// </summary>
    private void OpenSettingsWindow()
    {
        if (_settingsWindow is null)
        {
            // The new window opens on the display that hosts the viewer.
            var workArea = DisplayArea
                .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
                .WorkArea;
            _settingsWindow = new SettingsWindow(
                _settings, ApplyTheme, ApplyLanguage, ApplyImageFrame, workArea);
            _settingsWindow.Closed += SettingsWindow_Closed;
        }

        _settingsWindow.ShowAndFocus();
    }

    // The settings window is not owned by the viewer, so it is closed explicitly
    // when the viewer exits; otherwise the process would stay alive with a window
    // that has nothing left to configure.
    private void SettingsWindow_Closed(object sender, WindowEventArgs args)
    {
        _settingsWindow = null;

        if (!_closing)
        {
            // Focus returns to the viewer, the way a dialog hands focus back.
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
        var wasFit = Math.Abs(_view.Zoom - 1.0) < 0.001;

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
