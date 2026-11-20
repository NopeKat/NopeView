using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Runtime.InteropServices;
using Windows.UI;

namespace NopeView.App;

// Settings plumbing: the page replaces the viewer inside the same window, the
// way the Windows design guidance and the WinUI Gallery present settings.
public sealed partial class MainWindow : Window
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HcfHighContrastOn = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action, uint parameter, ref HighContrastInfo info, uint update);

    // AccessibilitySettings is a UWP contract and is not reliable for an
    // unpackaged WinUI 3 process. SystemParametersInfo exposes the same
    // high-contrast flag without activating that WinRT object.
    private static bool IsHighContrastOn()
    {
        var info = new HighContrastInfo
        {
            Size = (uint)Marshal.SizeOf<HighContrastInfo>(),
        };
        return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
            && (info.Flags & HcfHighContrastOn) != 0;
    }

    private SettingsPage? _settingsPage;

    private bool IsSettingsOpen => SettingsHost.Visibility == Visibility.Visible;

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    // Ctrl+, opens the settings page, or returns to the viewer when the page
    // is already open.
    private void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsSettingsOpen)
        {
            CloseSettings();
        }
        else
        {
            OpenSettings();
        }
        args.Handled = true;
    }

    private void OpenSettings()
    {
        if (IsSettingsOpen)
        {
            return;
        }

        if (_settingsPage is null)
        {
            var page = new SettingsPage();
            page.Initialize(new SettingsPageParameter(
                _settings, ApplyTheme, ApplyLanguage, ApplyImageFrame));
            page.BackRequested += (_, _) => CloseSettings();
            _settingsPage = page;
            SettingsHost.Content = page;
        }

        ContentRoot.Visibility = Visibility.Collapsed;
        SettingsHost.Visibility = Visibility.Visible;

        // The page owns the window while it is open, so the viewer behind it
        // must not accept a dropped image.
        RootGrid.AllowDrop = false;
        RefreshWindowTitle();

        // Keyboard focus starts on the page, not on the caption, so the first
        // Tab lands in the settings content and the back command is reachable.
        _settingsPage?.FocusDefault();
    }

    private void CloseSettings()
    {
        if (!IsSettingsOpen)
        {
            return;
        }

        SettingsHost.Visibility = Visibility.Collapsed;
        ContentRoot.Visibility = Visibility.Visible;
        RootGrid.AllowDrop = true;
        RefreshWindowTitle();
        ViewerCommandBar.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// The caption names the open document, and switches to the page name while
    /// the settings page owns the window. Both surfaces share one title bar.
    /// </summary>
    private void RefreshWindowTitle()
    {
        if (IsSettingsOpen)
        {
            AppTitleBar.Title = Localization.Get("SettingsHeader.Text");
        }
        else if (string.IsNullOrEmpty(_documentName))
        {
            AppTitleBar.Title = "NopeView";
        }
        else
        {
            AppTitleBar.Title = Localization.Format("Window.DocumentTitle", _documentName);
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
        // dictionaries in App.xaml take effect. The settings page lives under
        // the same root, so it follows without a second theme application.
        ThemeApplication.Apply(RootGrid, theme);
        ApplyTitleBarColors(theme);
    }

    // The caption buttons are drawn by the system, outside the XAML theme.
    // Pin their colors to the app theme so a forced dark theme cannot leave
    // light-theme black glyphs on a dark Mica bar.
    private void ApplyTitleBarColors(AppTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        var effectiveTheme = theme switch
        {
            AppTheme.Dark => ElementTheme.Dark,
            AppTheme.Light => ElementTheme.Light,
            _ => RootGrid.ActualTheme,
        };
        var isDark = effectiveTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;

        // High contrast owns a separate system palette for text and surfaces.
        // Clearing the overrides lets the system draw the caption buttons with
        // that palette instead of the app's dark/light glyph colors.
        if (IsHighContrastOn())
        {
            titleBar.ButtonBackgroundColor = null;
            titleBar.ButtonInactiveBackgroundColor = null;
            titleBar.ButtonForegroundColor = null;
            titleBar.ButtonHoverForegroundColor = null;
            titleBar.ButtonHoverBackgroundColor = null;
            titleBar.ButtonPressedForegroundColor = null;
            titleBar.ButtonPressedBackgroundColor = null;
            titleBar.ButtonInactiveForegroundColor = null;
            return;
        }

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = isDark
            ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x09, 0x00, 0x00, 0x00);
        titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
        titleBar.ButtonPressedBackgroundColor = isDark
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0A, 0x00, 0x00, 0x00);
        titleBar.ButtonInactiveForegroundColor = isDark
            ? Color.FromArgb(0xFF, 0xA0, 0xA0, 0xA0)
            : Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
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
