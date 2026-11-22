using System.Runtime.InteropServices;

namespace PDFDesk.Services;

internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOperation
    {
        public nint Owner;
        public uint Operation;
        public nint From, To;
        public ushort Flags;
        public int Aborted;
        public nint Mappings, ProgressTitle;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHFileOperationW(ref FileOperation operation);

    internal static unsafe bool DeletePdf(string path, nint owner)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['*', '?', '\0']) >= 0 ||
            !Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new IOException(UiText.Current.InvalidDocumentName);
        if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new IOException(UiText.Current.InvalidDocumentName);

        // The shell expects a double-NUL terminated list even for one file.
        fixed (char* source = path + "\0")
        {
            const ushort allowUndo = 0x40, noConfirmation = 0x10, warnOnPermanentDelete = 0x4000;
            const ushort silent = 4, noErrorUi = 0x400;
            var operation = new FileOperation
            {
                Owner = owner, Operation = 3, From = (nint)source,
                Flags = allowUndo | noConfirmation | warnOnPermanentDelete | silent | noErrorUi
            };
            int result = SHFileOperationW(ref operation);
            if (operation.Aborted != 0) return false;
            if (result != 0) throw new IOException($"{UiText.Current.DeleteFiles} (0x{result:X})");
            return true;
        }
    }
}
