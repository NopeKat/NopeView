using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NopeView.App;

namespace NopeView;

public static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new global::NopeView.App.App();
            GC.KeepAlive(app);
        });
    }
}
