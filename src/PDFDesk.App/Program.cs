using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PDFDesk.Services;

namespace PDFDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var request = LaunchRequest.FromCurrentProcess(args);
        using var instance = new SingleInstance();
        if (!instance.TryAcquire())
        {
            if (instance.TryForward(request)) return;
            // The owner may have exited during handoff. Only a lock holder starts the UI.
            if (!instance.TryAcquire())
            {
                instance.TryForward(request);
                return;
            }
        }

        Application.Start(_args =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(request);
            _ = Task.Run(() => instance.ListenAsync(activation => dispatcher.TryEnqueue(() => app.Activate(activation))));
        });
    }
}
