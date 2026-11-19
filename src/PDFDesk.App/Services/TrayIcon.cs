using System.Runtime.InteropServices;

namespace PDFDesk.Services;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint AddIcon = 0, DeleteIcon = 2, SetVersion = 4;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly SubclassProc _proc;
    private readonly uint _taskbarCreated;
    private readonly Action _restore;
    private readonly Action<int, int> _showMenu;
    private readonly bool _subclassed;
    private bool _visible;

    internal TrayIcon(nint window, Action restore, Action<int, int> showMenu)
    {
        _window = window;
        _restore = restore;
        _showMenu = showMenu;
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        _icon = LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Assets", "pdfdesk.ico"),
            1, GetSystemMetrics(49), GetSystemMetrics(50), 0x10);
        _proc = WindowProc;
        _subclassed = SetWindowSubclass(_window, _proc, 2, 0);
    }

    internal bool Show()
    {
        if (_visible) return true;
        if (!_subclassed || _icon == 0) return false;

        var data = CreateData();
        _visible = Shell_NotifyIconW(AddIcon, ref data);
        if (!_visible) return false;
        if (Shell_NotifyIconW(SetVersion, ref data)) return true;

        Hide();
        return false;
    }

    internal void Hide()
    {
        if (!_visible) return;
        var data = CreateData();
        Shell_NotifyIconW(DeleteIcon, ref data);
        _visible = false;
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == CallbackMessage && _visible)
        {
            switch ((ushort)lParam)
            {
                case 0x0400: // NIN_SELECT
                case 0x0401: // NIN_KEYSELECT
                    _restore();
                    break;
                case 0x007B: // WM_CONTEXTMENU, with the tray icon's screen coordinates
                    _showMenu((short)wParam, (short)(wParam >> 16));
                    break;
            }
            return 0;
        }
        if (message == _taskbarCreated && _visible)
        {
            _visible = false;
            if (!Show()) _restore();
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private IconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<IconData>(),
        Window = _window,
        Id = 1,
        Flags = 0x87, // NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP
        Message = CallbackMessage,
        Icon = _icon,
        Tip = "PDF Desk",
        Version = 4,
    };

    public void Dispose()
    {
        Hide();
        if (_subclassed) RemoveWindowSubclass(_window, _proc, 2);
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Message;
        internal nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
        internal uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string InfoTitle;
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern bool Shell_NotifyIconW(uint message, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern nint LoadImageW(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
}
