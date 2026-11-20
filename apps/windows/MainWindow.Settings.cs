using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Runtime.InteropServices;
using Windows.UI;

namespace NopeView.App;

// Top-level navigation and settings plumbing. The viewer, settings and about
// surfaces are peers in one NavigationView instead of a page with a back stack.
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

    private enum AppSection
    {
        Viewer,
        Settings,
        About,
    }

    private SettingsPage? _settingsPage;
    private AboutPage? _aboutPage;
    private AppSection _section = AppSection.Viewer;

    private bool IsSettingsOpen => _section == AppSection.Settings;

    private bool IsAboutOpen => _section == AppSection.About;

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    // Ctrl+, opens settings, or returns to the viewer when settings is already
    // open. The same key works while a top-level page has focus.
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

    private void ViewerSectionAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActivateSection(AppSection.Viewer);
        args.Handled = true;
    }

    private void SettingsSectionAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActivateSection(AppSection.Settings);
        args.Handled = true;
    }

    private void AboutSectionAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ActivateSection(AppSection.About);
        args.Handled = true;
    }

    // The official TitleBar forwards its pane button to the NavigationView.
    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        TopLevelNav.IsPaneOpen = !TopLevelNav.IsPaneOpen;
    }

    private void TopLevelNav_SelectionChanged(
        NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
        {
            return;
        }

        var section = tag switch
        {
            "settings" => AppSection.Settings,
            "about" => AppSection.About,
            _ => AppSection.Viewer,
        };

        ShowSection(section);
    }

    private void OpenSettings()
    {
        ActivateSection(AppSection.Settings);
    }

    private void CloseSettings()
    {
        ActivateSection(AppSection.Viewer);
    }

    private void ActivateSection(AppSection section)
    {
        var item = section switch
        {
            AppSection.Settings => SettingsNavItem,
            AppSection.About => AboutNavItem,
            _ => ViewerNavItem,
        };

        if (ReferenceEquals(TopLevelNav.SelectedItem, item))
        {
            ShowSection(section);
            return;
        }

        // SelectionChanged updates the surface, so the switch happens in one
        // place whether the user clicks the pane or a command selects it.
        TopLevelNav.SelectedItem = item;
    }

    private void ShowSection(AppSection section)
    {
        var changed = _section != section;
        _section = section;
        EnsurePage(section);

        ContentRoot.Visibility = section == AppSection.Viewer
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsHost.Visibility = section == AppSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;
        AboutHost.Visibility = section == AppSection.About
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The viewer is the only surface that accepts a dropped image.
        RootGrid.AllowDrop = section == AppSection.Viewer;

        if (changed)
        {
            var surface = section switch
            {
                AppSection.Settings => (FrameworkElement)SettingsHost,
                AppSection.About => AboutHost,
                _ => ContentRoot,
            };
            AnimateSurface(surface);
        }

        if (section == AppSection.Settings)
        {
            _settingsPage?.FocusDefault();
        }
        else if (section == AppSection.About)
        {
            _aboutPage?.FocusDefault();
        }
        else
        {
            ViewerCommandBar.Focus(FocusState.Programmatic);
        }

        RefreshWindowTitle();
    }

    /// <summary>
    /// A short fade marks the top-level page change without delaying it. The
    /// animation is skipped before the first layout pass, where XamlRoot is not
    /// available yet.
    /// </summary>
    private static void AnimateSurface(FrameworkElement surface)
    {
        surface.Opacity = 1;
        if (surface.XamlRoot is null)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, surface);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void EnsurePage(AppSection section)
    {
        if (section == AppSection.Settings && _settingsPage is null)
        {
            var page = new SettingsPage();
            page.Initialize(new SettingsPageParameter(
                _settings, ApplyTheme, ApplyLanguage, ApplyImageFrame));
            _settingsPage = page;
            SettingsHost.Content = page;
        }
        else if (section == AppSection.About && _aboutPage is null)
        {
            var page = new AboutPage();
            page.ApplyLocalization();
            _aboutPage = page;
            AboutHost.Content = page;
        }
    }

    /// <summary>
    /// The caption names the open document, and switches to the page name while
    /// a top-level page owns the window. All surfaces share one title bar.
    /// </summary>
    private void RefreshWindowTitle()
    {
        if (IsSettingsOpen)
        {
            AppTitleBar.Title = Localization.Get("SettingsHeader.Text");
        }
        else if (IsAboutOpen)
        {
            AppTitleBar.Title = Localization.Get("AboutSection.Text");
        }
        else if (string.IsNullOrEmpty(_documentName))
        {
            AppTitleBar.Title = "NopeView";
        }
        else
        {
            AppTitleBar.Title = Localization.Format(
                "Window.DocumentTitle", _documentName);
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
