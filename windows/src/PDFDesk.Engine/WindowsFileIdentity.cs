using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PDFDesk.Engine;

public static class WindowsFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileId
    {
        public ulong Volume, Low, High;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileId info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacyFileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out LegacyFileInfo info);

    private static FileId? Read(string path)
    {
        // Metadata-only access also works on a file opened exclusively for data.
        // BACKUP_SEMANTICS admits directory handles. Follow reparse points.
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            throw new IOException(path, new Win32Exception(error));
        }
        return Read(handle, path);
    }

    private static FileId Read(SafeFileHandle handle, string path)
    {
        if (GetFileInformationByHandleEx(handle, 18 /* FileIdInfo */, out var id, (uint)Marshal.SizeOf<FileId>()))
            return id;
        // Older file systems may only support the 64-bit file index.
        if (GetFileInformationByHandle(handle, out var legacy))
            return new FileId { Volume = legacy.Volume, Low = ((ulong)legacy.IndexHigh << 32) | legacy.IndexLow };
        throw new IOException(path, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    // Navigation keys describe the path at capture time. Comparing them never opens a file.
    public sealed class NavigationKey
    {
        public string Path { get; }
        private readonly FileId? _file, _parent;

        public NavigationKey(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            _file = TryRead(Path);
            _parent = TryRead(System.IO.Path.GetDirectoryName(Path)!);
        }

        private NavigationKey(NavigationKey previous, FileId? file)
        {
            Path = previous.Path;
            _parent = previous._parent;
            _file = file;
        }

        public NavigationKey AfterReplacement(NavigationKey replacement) =>
            SameLocation(replacement) ? new NavigationKey(this, replacement._file) : this;

        internal NavigationKey FromHandle(SafeFileHandle handle)
        {
            try { return new NavigationKey(this, Read(handle, Path)); }
            catch (IOException) { return new NavigationKey(this, null); }
        }

        // Atomic replacement can separate hard links while a symbolic link follows the new file.
        public NavigationKey RecheckAfterReplacement(NavigationKey replacement) =>
            SameLocation(replacement) ? AfterReplacement(replacement) : new NavigationKey(Path);

        private static FileId? TryRead(string path)
        {
            try { return Read(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public bool SameLocation(NavigationKey other) =>
            string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase) ||
            (_parent is { } parent && parent.Equals(other._parent) &&
             string.Equals(System.IO.Path.GetFileName(Path), System.IO.Path.GetFileName(other.Path), StringComparison.OrdinalIgnoreCase));

        public bool Matches(NavigationKey other) =>
            SameLocation(other) || (_file is { } file && file.Equals(other._file));
    }

    public static bool SameFile(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        bool same = string.Equals(source, target, StringComparison.OrdinalIgnoreCase);
        if (!same && Read(target) is { } targetId)
            same = Read(source) is { } sourceId && sourceId.Equals(targetId);
        if (!same && string.Equals(Path.GetFileName(source), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
            same = Read(Path.GetDirectoryName(source)!) is { } parent && parent.Equals(Read(Path.GetDirectoryName(target)!));
        return same;
    }

    internal static void RequireDifferentFile(string source, string target)
    {
        if (SameFile(source, target)) throw new IOException("Choose a different file for the copy.");
    }
}
