using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NopeView.App;

namespace NopeView;

public static class Program
{
    [STAThread]
    private static void Main()
    {
        UiPerf.MarkStart();

        // The framework resolves the language of its own strings (window
        // chrome, picker commands) when the first window is created, so the
        // stored choice has to be in place before Application.Start. The file
        // is read here once and handed to the window that consumes it.
        var settingsClock = UiPerf.Start();
        var settings = new SettingsService();
        settings.Load();
        Localization.Apply(settings.Language);
        UiPerf.Stop(settingsClock, "startup-settings", "load + language");

        // The namespace shares its name with the application class, so the
        // static slot is reached through the fully qualified type.
        global::NopeView.App.App.Settings = settings;

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new global::NopeView.App.App();
            GC.KeepAlive(app);
        });
    }
}
