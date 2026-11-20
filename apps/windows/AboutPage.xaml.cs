using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace NopeView.App;

public sealed partial class AboutPage : Page
{
    private readonly string _version =
        typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private bool _focusOnLoad;

    public AboutPage()
    {
        InitializeComponent();
        ApplyLocalization();
        Loaded += AboutPage_Loaded;
    }

    internal void ApplyLocalization()
    {
        AutomationProperties.SetName(this, Localization.Get("AboutSection.Text"));
        AboutHeader.Text = Localization.Get("AboutSection.Text");
        AppNameText.Text = Localization.Get("AboutExpander.Header");
        DescriptionText.Text = Localization.Get("AboutExpander.Description");
        VersionText.Text = Localization.Format("AboutVersionLine", _version);
        RepositoryCard.Header = Localization.Get("RepositoryCard.Header");
        RepositoryCard.Description = Localization.Get("RepositoryCard.Description");
    }

    internal void FocusDefault()
    {
        if (IsLoaded)
        {
            RepositoryButton.Focus(FocusState.Programmatic);
            return;
        }

        _focusOnLoad = true;
    }

    private void AboutPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_focusOnLoad)
        {
            return;
        }

        _focusOnLoad = false;
        RepositoryButton.Focus(FocusState.Programmatic);
    }
}
