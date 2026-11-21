using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NopeView.App;

/// <summary>
/// State handed to the settings page by the host window. Persistence stays in
/// SettingsService and theme application stays in the window that owns the
/// root element.
/// </summary>
internal sealed record SettingsPageParameter(
    SettingsService Settings,
    Action<AppTheme> ApplyTheme,
    Action<string> ApplyLanguage,
    Action<ImageFrameStyle> ApplyImageFrame);

public sealed partial class SettingsPage : Page
{
    // The about section reports the assembly version, which stays fixed for the
    // lifetime of the page and only needs re-reading when the label language
    // changes.
    private readonly string _version =
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private SettingsPageParameter? _parameter;
    private bool _syncingTheme;
    private bool _syncingLanguage;
    private bool _syncingImageFrame;

    // Focus is requested before the page has been measured, so the request is
    // held until Loaded; a control that is not in the tree cannot take focus.
    private bool _focusOnLoad;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    internal void Initialize(SettingsPageParameter parameter)
    {
        _parameter = parameter;
        ApplyLocalization();
        RefreshTheme();
        RefreshLanguage();
        RefreshImageFrame();
    }

    /// <summary>
    /// Writes every localized string the page shows. The page carries no hard
    /// coded text, so this runs on first load and again after a language switch.
    /// </summary>
    private void ApplyLocalization()
    {
        AutomationProperties.SetName(this, Localization.Get("SettingsHeader.Text"));
        AppearanceSectionHeader.Text = Localization.Get("AppearanceSection.Text");

        ThemeCard.Header = Localization.Get("ThemeCard.Header");
        ThemeCard.Description = Localization.Get("ThemeCard.Description");
        ThemeLightOption.Content = Localization.Get("ThemeLight.Content");
        ThemeDarkOption.Content = Localization.Get("ThemeDark.Content");
        ThemeSystemOption.Content = Localization.Get("ThemeSystem.Content");

        ImageFrameCard.Header = Localization.Get("ImageFrameCard.Header");
        ImageFrameCard.Description = Localization.Get("ImageFrameCard.Description");
        ImageFrameNoneOption.Content = Localization.Get("ImageFrameNone.Content");
        ImageFrameStrokeOption.Content = Localization.Get("ImageFrameStroke.Content");
        ImageFrameShadowOption.Content = Localization.Get("ImageFrameShadow.Content");

        LanguageSectionHeader.Text = Localization.Get("LanguageSection.Text");
        LanguageCard.Header = Localization.Get("LanguageCard.Header");
        LanguageCard.Description = Localization.Get("LanguageCard.Description");
        LanguageSystemOption.Content = Localization.Get("LanguageSystem.Content");
        LanguageEnglishOption.Content = Localization.Get("LanguageEnglish.Content");
        LanguageChineseOption.Content = Localization.Get("LanguageChinese.Content");
        // The settings card supplies the label, but the combo box caches the
        // name it was built with, so it is set again on every switch.
        AutomationProperties.SetName(
            LanguageComboBox, LanguageCard.Header?.ToString() ?? string.Empty);

        // The about section sits at the bottom of the page, the way the design
        // guidance places it.
        AboutSectionHeader.Text = Localization.Get("AboutSection.Text");
        AboutExpander.Header = Localization.Get("AboutExpander.Header");
        // The collapsed row reports the version in the description slot; the
        // tagline was dropped, so the row stays a name and a number.
        AboutExpander.Description = Localization.Format("AboutVersionLine", _version);
        RepositoryCard.Header = Localization.Get("RepositoryCard.Header");
        RepositoryCard.Description = Localization.Get("RepositoryCard.Description");
    }

    /// <summary>
    /// Moves keyboard focus into the page when it is selected. The theme
    /// selector is the first setting and matches the first Tab stop.
    /// </summary>
    internal void FocusDefault()
    {
        if (IsLoaded)
        {
            ThemeComboBox.Focus(FocusState.Programmatic);
            return;
        }

        _focusOnLoad = true;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_focusOnLoad)
        {
            return;
        }

        _focusOnLoad = false;
        ThemeComboBox.Focus(FocusState.Programmatic);
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
        // Clear the selection first: the collapsed combo box caches the text it
        // shows, and it has to be rebuilt when the item text changes language.
        ThemeComboBox.SelectedIndex = -1;
        ThemeComboBox.SelectedIndex = theme switch
        {
            AppTheme.System => 0,
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0,
        };
        _syncingTheme = false;

        // The current value lives in the control automation name so the
        // setting can be read back without adding a redundant text row.
        var value = theme switch
        {
            AppTheme.Light => Localization.Get("Theme.ValueLight"),
            AppTheme.Dark => Localization.Get("Theme.ValueDark"),
            _ => Localization.Get("Theme.ValueSystem"),
        };
        AutomationProperties.SetName(
            ThemeComboBox, Localization.Format("Theme.Automation", value));
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var parameter = _parameter;
        if (_syncingLanguage || parameter is null)
        {
            return;
        }

        var language = LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string tag }
            ? tag
            : Localization.SystemPreference;
        parameter.Settings.SetLanguage(language);
        parameter.ApplyLanguage(language);
        ApplyLocalization();
        RefreshTheme();
        RefreshLanguage();
        RefreshImageFrame();
    }

    private void RefreshLanguage()
    {
        var language = _parameter?.Settings.Language ?? Localization.SystemPreference;

        _syncingLanguage = true;
        LanguageComboBox.SelectedIndex = -1;
        LanguageComboBox.SelectedIndex = language switch
        {
            Localization.SimplifiedChinese => 1,
            Localization.FallbackLanguage => 2,
            _ => 0,
        };
        _syncingLanguage = false;
    }

    private void ImageFrameComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var parameter = _parameter;
        if (_syncingImageFrame ||
            parameter is null ||
            ImageFrameComboBox.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, out ImageFrameStyle frame))
        {
            return;
        }

        parameter.Settings.SetImageFrame(frame);
        parameter.ApplyImageFrame(frame);
        RefreshImageFrame();
    }

    private void RefreshImageFrame()
    {
        var frame = _parameter?.Settings.ImageFrame ?? ImageFrameStyle.None;

        _syncingImageFrame = true;
        // As with the theme combo, clearing first rebuilds the collapsed text
        // after a language change instead of reusing the cached string.
        ImageFrameComboBox.SelectedIndex = -1;
        ImageFrameComboBox.SelectedIndex = frame switch
        {
            ImageFrameStyle.Stroke => 1,
            ImageFrameStyle.Shadow => 2,
            _ => 0,
        };
        _syncingImageFrame = false;

        var value = frame switch
        {
            ImageFrameStyle.Stroke => Localization.Get("ImageFrame.ValueStroke"),
            ImageFrameStyle.Shadow => Localization.Get("ImageFrame.ValueShadow"),
            _ => Localization.Get("ImageFrame.ValueNone"),
        };
        AutomationProperties.SetName(
            ImageFrameComboBox, Localization.Format("ImageFrame.Automation", value));
    }
}
