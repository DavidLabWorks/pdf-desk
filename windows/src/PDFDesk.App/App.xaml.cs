using Microsoft.UI.Xaml;
using PDFDesk.Services;

namespace PDFDesk;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    private readonly LaunchRequest _initialLaunch;
    private readonly Queue<LaunchRequest> _pendingLaunches = new();

    internal App(LaunchRequest initialLaunch)
    {
        _initialLaunch = initialLaunch;
        InitializeComponent();

        // Safety net. An `async void` handler (every WinUI event handler is one)
        // has no Task to park an exception in, so the runtime rethrows it on the
        // dispatcher and the process dies unless Handled is set here. Log it and
        // keep running — a reader losing an hour of annotations to a transient
        // failure is far worse than a stale error message.
        UnhandledException += (_, e) =>
        {
            ErrorLog.Default.Write("UnhandledException", e.Exception);
            e.Handled = true;
            ReportToUser(e.Exception.Message);
        };

        // The other half: `_ = SomeAsync()` parks a failure in a Task nobody
        // awaits, which is silently swallowed. This surfaces those too.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ErrorLog.Default.Write("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>
    /// Surfaces a background failure in the main window's InfoBar. Never a
    /// dialog: the very bug this net was added for was "a second ContentDialog
    /// was shown", so opening one from the handler could re-trigger it.
    /// </summary>
    private static void ReportToUser(string message)
    {
        try
        {
            (MainWindow as MainWindow)?.ReportBackgroundError(message);
        }
        catch
        {
            // Window may be closing or not built yet — the log already has it.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var state = PDFDesk.MainWindow.SharedState;
        var window = CreateReaderWindow(state.Session, startupWindow: true);
        if (state.Settings.RestoreSession)
            foreach (var session in state.AdditionalWindows.ToArray()) CreateReaderWindow(session);
        window.Activate();
        _ = OpenFilesAsync(window, _initialLaunch);
        while (_pendingLaunches.TryDequeue(out var request))
        {
            Activate(request);
        }
    }

    internal static List<MainWindow> Readers { get; } = [];
    internal static nint OwnerHandle(FrameworkElement element) =>
        Microsoft.UI.Win32Interop.GetWindowFromWindowId(element.XamlRoot.ContentIslandEnvironment.AppWindowId);

    internal static bool Exiting { get; set; }

    internal static MainWindow CreateReaderWindow(SessionState? session = null, bool startupWindow = false)
    {
        var window = new MainWindow(session ?? new SessionState { ActiveIndex = -1 }, startupWindow);
        Readers.Add(window);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "pdfdesk.ico");
        if (File.Exists(icon)) window.AppWindow.SetIcon(icon);
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) MainWindow = window;
        };
        MainWindow = window;
        window.Activate();
        if (Readers.Count == 1) window.ShowSharedTray();
        return window;
    }

    internal static void RemoveReader(MainWindow window)
    {
        bool ownedTray = Readers.FirstOrDefault() == window;
        Readers.Remove(window);
        if (MainWindow == window) MainWindow = Readers.LastOrDefault();
        if (ownedTray && !Exiting) Readers.FirstOrDefault()?.ShowSharedTray();
    }

    internal void Activate(LaunchRequest request)
    {
        if (MainWindow is not MainWindow window)
        {
            _pendingLaunches.Enqueue(request);
            return;
        }

        window.RestoreWindow();
        _ = OpenFilesAsync(window, request);
    }

    private static async Task OpenFilesAsync(MainWindow window, LaunchRequest request)
    {
        foreach (string file in request.Files)
        {
            try
            {
                await window.LoadDocumentAsync(file, request.Page, request.Zoom);
            }
            catch (Exception ex)
            {
                ErrorLog.Default.Write(nameof(OpenFilesAsync), ex);
                ReportToUser(ex.Message);
            }
        }
    }

}
