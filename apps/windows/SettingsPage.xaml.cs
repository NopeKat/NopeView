using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System;

namespace NopeView.App;

/// <summary>
/// State handed to the settings page by the host window. Persistence stays in
/// SettingsService and theme application stays in the window that owns the
/// root element.
/// </summary>
internal sealed record SettingsPageParameter(
    SettingsService Settings,
    Action<AppTheme> ApplyTheme);

public sealed partial class SettingsPage : Page
{
    private SettingsPageParameter? _parameter;
    private bool _syncingTheme;

    public SettingsPage()
    {
        InitializeComponent();
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    /// <summary>
    /// Raised when the user asks to leave the settings page and return to the
    /// viewer. The host window owns the switch.
    /// </summary>
    public event EventHandler? BackRequested;

    internal void Initialize(SettingsPageParameter parameter)
    {
        _parameter = parameter;
        RefreshTheme();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var parameter = _parameter;
        if (_syncingTheme ||
            parameter is null ||
            ThemeComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out AppTheme theme))
        {
            return;
        }

        parameter.Settings.SetTheme(theme);
        parameter.ApplyTheme(theme);
        RefreshTheme();
    }

    private void RefreshTheme()
    {
        var theme = _parameter?.Settings.Theme ?? AppTheme.System;

        _syncingTheme = true;
        ThemeComboBox.SelectedIndex = theme switch
        {
            AppTheme.Light => 0,
            AppTheme.Dark => 1,
            _ => 2,
        };
        _syncingTheme = false;

        // The current value lives in the control automation name so the
        // setting can be read back without adding a redundant text row.
        AutomationProperties.SetName(ThemeComboBox, "Theme: " + theme);
    }

    private async void ColorSettingsLink_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // The settings page hands the user to the system color page, where the
        // wallpaper-driven Mica tint and the system mode are managed.
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:colors"));
        }
        catch (Exception)
        {
            // A missing Settings app is not worth an error panel here; the
            // theme options above keep working.
        }
    }
}
