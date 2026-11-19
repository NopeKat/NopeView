using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;

namespace NopeView.App;

public sealed partial class AppearanceSettingsPage : Page
{
    private SettingsPageParameter? _parameter;
    private bool _syncingTheme;

    public AppearanceSettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _parameter = e.Parameter as SettingsPageParameter;
        RefreshTheme();
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
        ChooseTheme(parameter, theme);
    }

    private void ChooseTheme(SettingsPageParameter parameter, AppTheme theme)
    {
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
}
