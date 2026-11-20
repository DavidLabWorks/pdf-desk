using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PDFDesk.Services;

internal static class ClipboardText
{
    public static async Task WriteAsync(nint owner, string text)
    {
        const uint movable = 2, unicodeText = 13;
        nint block = GlobalAlloc(movable, checked((nuint)(text.Length + 1) * 2));
        if (block == 0) throw new Win32Exception();
        try
        {
            nint data = GlobalLock(block);
            if (data == 0) throw new Win32Exception();
            try { Marshal.Copy((text + '\0').ToCharArray(), 0, data, text.Length + 1); }
            finally { GlobalUnlock(block); }

            for (int attempt = 0; !OpenClipboard(owner); attempt++)
            {
                if (attempt >= 9) throw new Win32Exception();
                await Task.Delay(30);
            }
            try
            {
                if (!EmptyClipboard() || SetClipboardData(unicodeText, block) == 0) throw new Win32Exception();
                block = 0; // Windows now owns the memory.
            }
            finally { CloseClipboard(); }
        }
        finally { if (block != 0) GlobalFree(block); }
    }

    [DllImport("user32", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32")] private static extern bool CloseClipboard();
    [DllImport("user32", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint memory);
}
