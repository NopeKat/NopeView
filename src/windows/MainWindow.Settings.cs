using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using Windows.Graphics;

namespace NopeView.App;

// Top-level navigation and settings plumbing. The viewer and about surfaces are
// destinations inside one NavigationView. Settings is a window of its own, so
// the pane item, the context menu and Ctrl+, all open that window instead of
// switching the surface in place.
public sealed partial class MainWindow : Window
{
    private enum AppSection
    {
        Viewer,
        About,
    }

    private AboutPage? _aboutPage;
    private SettingsWindow? _settingsWindow;
    private AppSection _section = AppSection.Viewer;

    // Set while the main window is closing. Closing the settings window at the
    // same time must not try to activate a window that is already gone.
    private bool _closing;

    private bool IsAboutOpen => _section == AppSection.About;

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    // Ctrl+, brings the settings window forward. It is a separate window now, so
    // the key cannot toggle a surface: it opens the window on the first press and
    // focuses it on every later one.
    private void SettingsAccelerator_Invoked(
        KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenSettingsWindow();
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
        OpenSettingsWindow();
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

        ShowSection(tag == "about" ? AppSection.About : AppSection.Viewer);
    }

    /// <summary>
    /// The settings item keeps the selection unchanged (SelectsOnInvoked is off),
    /// so it arrives as a command: open its window and close the pane the way a
    /// menu action does.
    /// </summary>
    private void TopLevelNav_ItemInvoked(
        NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is NavigationViewItem { Tag: "settings" })
        {
            OpenSettingsWindow();
        }
    }

    /// <summary>
    /// Opens the settings window, or brings the open one forward. The window is
    /// not owned by this window, so it keeps its own taskbar button and closes on
    /// its own; only the stored values and the theme callbacks are shared.
    /// </summary>
    private void OpenSettingsWindow()
    {
        TopLevelNav.IsPaneOpen = false;

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

    private void ActivateSection(AppSection section)
    {
        var item = section == AppSection.About ? AboutNavItem : ViewerNavItem;

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
        AboutHost.Visibility = section == AppSection.About
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The viewer is the only surface that accepts a dropped image.
        RootGrid.AllowDrop = section == AppSection.Viewer;

        if (changed)
        {
            AnimateSurface(section == AppSection.About
                ? AboutHost
                : (FrameworkElement)ContentRoot);
        }

        if (section == AppSection.About)
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
        if (section == AppSection.About && _aboutPage is null)
        {
            var page = new AboutPage();
            page.ApplyLocalization();
            _aboutPage = page;
            AboutHost.Content = page;
        }
    }

    /// <summary>
    /// The caption names the open document, and switches to the page name while
    /// about owns the window. Settings no longer takes the caption over: it has a
    /// caption of its own.
    /// </summary>
    private void RefreshWindowTitle()
    {
        if (IsAboutOpen)
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
