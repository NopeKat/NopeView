using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace NopeView.App;

// Settings plumbing: the page replaces the viewer inside the same window, the
// way the Windows design guidance and the WinUI Gallery present settings.
public sealed partial class MainWindow : Window
{
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
                _settings, ApplyTheme, ApplyLanguage));
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
    }
}
