using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NopeView.App;

namespace NopeView;

public static class Program
{
    [STAThread]
    private static void Main()
    {
        // The framework resolves the language of its own strings (window
        // chrome, picker commands) when the first window is created, so the
        // stored choice has to be in place before Application.Start.
        var settings = new SettingsService();
        settings.Load();
        Localization.Apply(settings.Language);

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new global::NopeView.App.App();
            GC.KeepAlive(app);
        });
    }
}
