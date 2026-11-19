using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PDFDesk.Services;

/// <summary>Routes the snap-layout target to Windows while XAML draws the buttons.</summary>
internal sealed class CaptionHitTest : IDisposable
{
    private const int MaxButton = 9;
    private readonly nint _window, _content;
    private readonly FrameworkElement _root;
    private readonly Button _maximize;
    private readonly SubclassProc _proc;

    internal CaptionHitTest(nint window, FrameworkElement root, Button maximize)
    {
        _window = window;
        _root = root;
        _maximize = maximize;
        _proc = WindowProc;
        _content = FindWindowExW(window, 0, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        SetWindowSubclass(window, _proc, 1, 0);
        if (_content != 0) SetWindowSubclass(_content, _proc, 1, 0);
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0084 && _root.XamlRoot is { } root) // WM_NCHITTEST
        {
            var point = new NativePoint { X = (short)(long)lParam, Y = (short)((long)lParam >> 16) };
            ScreenToClient(_window, ref point);
            var logical = new Point(point.X / root.RasterizationScale, point.Y / root.RasterizationScale);
            if (logical.Y >= 3 && Contains(_maximize, logical))
                return hwnd == _content ? -1 : MaxButton;
        }
        // Windows owns the maximize target so it can show the Snap Layouts flyout.
        if (hwnd == _window && message == 0x00A0 && wParam == MaxButton)
        {
            VisualStateManager.GoToState(_maximize, "PointerOver", false);
            var tracking = new MouseTracking { Size = (uint)Marshal.SizeOf<MouseTracking>(), Flags = 0x12, Window = hwnd };
            TrackMouseEvent(ref tracking);
        }
        else if (hwnd == _window && (message == 0x02A2 || message == 0x00A0))
        {
            VisualStateManager.GoToState(_maximize, "Normal", false);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool Contains(FrameworkElement element, Point point)
    {
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
        return element.TransformToVisual(_root).TransformBounds(bounds).Contains(point);
    }

    public void Dispose()
    {
        RemoveWindowSubclass(_window, _proc, 1);
        if (_content != 0) RemoveWindowSubclass(_content, _proc, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseTracking { public uint Size, Flags; public nint Window; public uint HoverTime; }

    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint FindWindowExW(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref MouseTracking track);
}
