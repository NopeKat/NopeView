using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
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

    public SettingsPage()
    {
        InitializeComponent();
    }

    internal void Initialize(SettingsPageParameter parameter)
    {
        _parameter = parameter;

        // The first item is selected in XAML, so selection changed may fire
        // before the parameter arrives. Navigate once the parameter is known.
        SettingsContentFrame.Navigate(
            typeof(AppearanceSettingsPage),
            _parameter,
            new EntranceNavigationTransitionInfo());
    }

    private void SettingsNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_parameter is null ||
            args.SelectedItemContainer is not NavigationViewItem item)
        {
            return;
        }

        var pageType = string.Equals(item.Tag as string, "About", StringComparison.Ordinal)
            ? typeof(AboutSettingsPage)
            : typeof(AppearanceSettingsPage);

        if (SettingsContentFrame.CurrentSourcePageType != pageType)
        {
            SettingsContentFrame.Navigate(
                pageType,
                _parameter,
                args.RecommendedNavigationTransitionInfo);
        }
    }
}
