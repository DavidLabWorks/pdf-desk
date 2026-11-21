using PDFDesk.PdfiumInterop;

namespace PDFDesk.Services;

public sealed class PdfLibrary(string directory)
{
    private const string PdfExtension = ".pdf";
    private const int MaxFileNameLength = 255;

    public bool Contains(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);

    public async Task<string> ImportAsync(string source)
    {
        if (Contains(source) && File.Exists(source)) return source;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output).ConfigureAwait(false);

            return Publish(staging, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source));
        }
        finally { File.Delete(staging); }
    }

    public static bool IsValidDocumentName(string name)
    {
        string stem = DocumentStem(name);
        if (stem.Length == 0 || stem.Length + PdfExtension.Length > MaxFileNameLength
            || stem.EndsWith('.') || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        string device = stem.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(device[3]));
    }

    public string CreateBlank(string name)
    {
        if (!IsValidDocumentName(name))
        {
            throw new ArgumentException("Invalid PDF document name.", nameof(name));
        }

        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteBlankPdf(output);
            }
            return Publish(staging, DocumentStem(name), PdfExtension);
        }
        finally { File.Delete(staging); }
    }

    private static string DocumentStem(string name)
    {
        string trimmed = name.Trim();
        return trimmed.EndsWith(PdfExtension, StringComparison.OrdinalIgnoreCase) ? trimmed[..^PdfExtension.Length].TrimEnd() : trimmed;
    }

    private string Publish(string staging, string name, string extension)
    {
        for (int number = 1; ; number++)
        {
            string suffix = number == 1 ? "" : $" ({number})";
            string destination = Path.Combine(directory, FitFileName(name, suffix + extension));
            try { File.Move(staging, destination); return destination; }
            catch (IOException) when (File.Exists(destination) || Directory.Exists(destination)) { }
        }
    }

    public static string FitFileName(string stem, string ending)
    {
        int room = MaxFileNameLength - ending.Length;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(room);
        if (stem.Length > room)
            stem = stem[..(char.IsHighSurrogate(stem[room - 1]) ? room - 1 : room)];
        return stem + ending;
    }

    private static void WriteBlankPdf(Stream output)
    {
        PdfiumLibrary.EnsureInitialized();
        lock (PdfiumLibrary.Lock)
        {
            IntPtr document = PdfiumNative.CreateNewDocument();
            if (document == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }
            try
            {
                // A4 portrait. PDF dimensions are points.
                IntPtr page = PdfiumNative.CreatePage(document, 0, 595.28, 841.89);
                if (page == IntPtr.Zero)
                {
                    throw PdfiumNative.LastError();
                }
                PdfiumNative.ClosePage(page);
                if (!PdfiumNative.SaveCopy(document, output))
                {
                    throw new IOException("Could not write the blank PDF.");
                }
            }
            finally { PdfiumNative.CloseDocument(document); }
        }
    }
}
