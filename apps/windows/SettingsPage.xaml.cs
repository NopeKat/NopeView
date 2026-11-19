using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace NopeView.App;

/// <summary>
/// State handed to the settings page by the host window. The page owns the
/// controls; persistence stays in SettingsService and theme application stays
/// in the window that owns the root element.
/// </summary>
internal sealed record SettingsPageParameter(
    SettingsService Settings,
    Action<AppTheme> ApplyTheme);

public sealed partial class SettingsPage : Page
{
    private SettingsService? _settings;
    private Action<AppTheme>? _applyTheme;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeButton.Loaded += (_, _) => RefreshTheme();
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is SettingsPageParameter parameter)
        {
            _settings = parameter.Settings;
            _applyTheme = parameter.ApplyTheme;
        }
        RefreshTheme();
    }

    private void SettingsNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var showAbout = string.Equals(item.Tag as string, "About", StringComparison.Ordinal);
        AppearancePanel.Visibility = showAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutPanel.Visibility = showAbout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse(tag, out AppTheme theme))
        {
            return;
        }
        ChooseTheme(theme);
    }

    private void ChooseTheme(AppTheme theme)
    {
        _settings?.SetTheme(theme);
        _applyTheme?.Invoke(theme);
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        var theme = _settings?.Theme ?? AppTheme.System;

        ThemeButtonText.Text = ThemeDisplayName(theme);
        foreach (var item in ThemeMenuItems())
        {
            item.IsChecked = item.Tag is string tag &&
                Enum.TryParse(tag, out AppTheme candidate) && candidate == theme;
        }

        // The current value lives in the button automation name so the
        // setting can be read back without adding a redundant text row.
        AutomationProperties.SetName(ThemeButton, "Theme: " + theme);
    }

    private IEnumerable<RadioMenuFlyoutItem> ThemeMenuItems()
    {
        if (ThemeButton.Flyout is not MenuFlyout flyout)
        {
            yield break;
        }
        foreach (var item in flyout.Items)
        {
            if (item is RadioMenuFlyoutItem radio)
            {
                yield return radio;
            }
        }
    }

    private static string ThemeDisplayName(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => "Use system setting",
    };
}
