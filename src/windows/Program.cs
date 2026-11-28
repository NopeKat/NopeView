// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NopeView.App;

namespace NopeView;

public static class Program
{
    [STAThread]
    private static void Main()
    {
        // Claim the viewer before anything else costs time. A launch that finds
        // the name taken hands its arguments to the running copy and leaves,
        // so a second window never opens.
        var instance = SingleInstanceService.Claim();
        if (instance.IsPrimary)
        {
            global::NopeView.App.App.Instance = instance;
        }
        else if (instance.Forward(LaunchArguments.OpenPath()))
        {
            // The running viewer took the request, so this launch has no work
            // left and leaves without building a window.
            instance.Dispose();
            return;
        }
        else
        {
            // The running viewer did not answer. Opening a second window with
            // the requested image is better than dropping the launch.
            instance.Dispose();
        }

        UiPerf.MarkStart();

        // The framework resolves the language of its own strings (window
        // chrome, picker commands) when the first window is created, so the
        // stored choice has to be in place before Application.Start. The file
        // is read here once and handed to the window that consumes it.
        var settingsClock = UiPerf.Start();
        var settings = new SettingsService();
        var loadClock = UiPerf.Start();
        settings.Load();
        UiPerf.Stop(loadClock, "startup-settings-load", "read json");
        var languageClock = UiPerf.Start();
        Localization.Apply(settings.Language);
        UiPerf.Stop(languageClock, "startup-language", settings.Language);
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
