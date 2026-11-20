using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PDFDesk.Services;
using Windows.Graphics;

namespace PDFDesk;

public sealed partial class MainWindow
{
    private Window? _trayMenuWindow;

    private void ShowTrayMenu(int x, int y)
    {
        if (_trayMenuWindow is not null)
        {
            return;
        }

        // A menu needs a visible XAML root even while the reader is hidden.
        var anchor = new Grid { RequestedTheme = RootGrid.RequestedTheme, Language = RootGrid.Language };
        var host = new Window { Title = AppTitle, Content = anchor };
        _trayMenuWindow = host;
        host.AppWindow.IsShownInSwitchers = false;
        var presenter = (OverlappedPresenter)host.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        host.AppWindow.MoveAndResize(new RectInt32(x, y, 2, 2));
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(host);
        // The native frame and its DWM shadow can survive an empty window region.
        // Make the owner fully transparent before activation. The unconstrained
        // WinUI flyout has its own popup HWND and keeps its normal opacity.
        const int extendedStyle = -20;
        const int layered = 0x80000;
        SetWindowLongW(hwnd, extendedStyle, GetWindowLongW(hwnd, extendedStyle) | layered);
        SetLayeredWindowAttributes(hwnd, 0, 0, 2);

        const double maxMenuWidth = 300;
        var menuStyle = new Style(typeof(MenuFlyoutPresenter));
        menuStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, maxMenuWidth));
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false, MenuFlyoutPresenterStyle = menuStyle };
        foreach (var window in App.Readers.ToArray())
        {
            string title = window.CurrentView?.DisplayName ?? UiText.Current.Home;
            string prefix = $"{App.Readers.IndexOf(window) + 1}   ";
            var item = AddItem(prefix + title, "\uE8A7", window.RestoreWindow);
            var probe = new TextBlock { FontSize = item.FontSize, FontFamily = item.FontFamily, FontWeight = item.FontWeight };
            string display = FitDocumentName(title, candidate =>
            {
                probe.Text = prefix + candidate;
                probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                // Reserve space for the menu icon, presenter insets and item padding.
                return probe.DesiredSize.Width <= maxMenuWidth - 64;
            });
            if (display != title)
            {
                item.Text = prefix + display;
                ToolTipService.SetToolTip(item, title);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, prefix + title);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(UiText.Current.NewWindow, "\uE78B", () => App.CreateReaderWindow());
        AddItem(UiText.Current.TrayOpenPdf, "\uE8E5", OpenPdfFromTray);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(UiText.Current.TrayExit, "\uE7E8", ExitApplication);

        anchor.Loaded += (_, _) =>
        {
            if (ReferenceEquals(_trayMenuWindow, host))
            {
                menu.ShowAt(anchor, new FlyoutShowOptions
                {
                    Placement = FlyoutPlacementMode.TopEdgeAlignedRight,
                    ShowMode = FlyoutShowMode.Standard,
                });
            }
        };
        menu.Closed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(_trayMenuWindow, host)) CloseTrayMenu();
        });
        host.Closed += (_, _) =>
        {
            if (ReferenceEquals(_trayMenuWindow, host)) _trayMenuWindow = null;
        };
        host.Activate();
        TrayIcon.SetForegroundWindow(hwnd);

        MenuFlyoutItem AddItem(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { MinWidth = 180, Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => DispatcherQueue.TryEnqueue(action.Invoke);
            menu.Items.Add(item);
            return item;
        }
    }

    private void CloseTrayMenu()
    {
        var host = _trayMenuWindow;
        _trayMenuWindow = null;
        host?.Close();
    }

    private async void OpenPdfFromTray()
    {
        RestoreWindow();
        await PickPdfsAsync(copy: false);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(nint window, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowLongW(nint window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
}
