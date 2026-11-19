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
        using var first = new SingleInstance();
        if (!first.IsOwner && first.TryForward(request))
        {
            return;
        }
        // Resolve the owner again if the previous process exited during handoff.
        using var retry = first.IsOwner ? null : new SingleInstance();
        var instance = retry ?? first;
        if (!instance.IsOwner)
        {
            instance.TryForward(request);
            return;
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
