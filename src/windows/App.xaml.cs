using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NopeView.App;

public partial class App : Application
{
    // Assigned by Program before Application.Start, so the settings file is
    // read once and the window only consumes the values it was handed.
    internal static SettingsService Settings { get; set; } = null!;

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Settings);
        _window.Activate();
    }
}
