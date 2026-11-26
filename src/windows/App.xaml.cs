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
        // Everything before this ran inside the framework's own startup.
        UiPerf.RecordSinceStart("startup-framework", "before the window");

        var windowClock = UiPerf.Start();
        _window = new MainWindow(Settings);
        UiPerf.Stop(windowClock, "startup-window", "constructor");

        _window.Activate();
        UiPerf.RecordSinceStart("startup-activated", "window shown");
    }
}
