using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
    private bool _initializing;
    private bool _selectionReady;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeComboBox.Loaded += (_, _) =>
        {
            RefreshTheme();
            ThemeComboBox.DispatcherQueue.TryEnqueue(() => _selectionReady = true);
        };
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        AppearanceNavButton.Background =
            (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        AboutNavButton.Background =
            (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        AppearanceNavIndicator.Visibility = Visibility.Visible;
        AboutNavIndicator.Visibility = Visibility.Collapsed;
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

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        bool showAbout = (sender as Button)?.Tag as string == "About";
        AppearancePanel.Visibility = showAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutPanel.Visibility = showAbout ? Visibility.Visible : Visibility.Collapsed;

        var selected = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var normal = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        AppearanceNavButton.Background = showAbout ? normal : selected;
        AboutNavButton.Background = showAbout ? selected : normal;
        AppearanceNavIndicator.Visibility = showAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutNavIndicator.Visibility = showAbout ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_selectionReady || _initializing || sender is not ComboBox box ||
            box.SelectedItem is not ComboBoxItem item || item.Tag is not string tag ||
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

        // Keep the native selection in sync without writing settings back.
        _initializing = true;
        foreach (var item in ThemeComboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string tag &&
                Enum.TryParse(tag, out AppTheme candidate) && candidate == theme)
            {
                ThemeComboBox.SelectedItem = comboItem;
                break;
            }
        }
        _initializing = false;

        // The current value lives in the ComboBox automation name so the
        // setting can be read back without adding a redundant text row.
        AutomationProperties.SetName(ThemeComboBox, "Theme: " + theme);
    }
}
