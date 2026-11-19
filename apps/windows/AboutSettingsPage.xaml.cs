using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace NopeView.App;

public sealed partial class AboutSettingsPage : Page
{
    public AboutSettingsPage()
    {
        InitializeComponent();
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        VersionText.Text = typeof(App).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }
}
