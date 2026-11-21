using PDFDesk.Engine;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFDesk.Services;

internal static class PdfClipboard
{
    private static string CopyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PDFDesk", "share");

    public static async Task CopyAsync(string path, PdfDocument document, IPdfWorkQueue workQueue)
    {
        string copyPath = await WriteCopyAsync(path, document, workQueue);
        var file = await StorageFile.GetFileFromPathAsync(copyPath);
        var package = new DataPackage();
        package.SetStorageItems([file]);
        Clipboard.SetContent(package);
    }

    // Separate directories preserve the original filename and keep earlier attachments intact.
    private static async Task<string> WriteCopyAsync(
        string path, PdfDocument document, IPdfWorkQueue workQueue)
    {
        // The PDFium lock can be held by a tile render for tens of milliseconds,
        // so the write goes to the render thread like every other document call.
        return await workQueue.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            string root = CopyDirectory;
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SweepOldCopies(root);

            string copy = Path.Combine(directory, Path.GetFileName(path));
            document.SaveSharingCopy(copy);
            return copy;
        });
    }

    /// <summary>
    /// Removes old copies. Files in use are left for the next sweep.
    /// </summary>
    private static void SweepOldCopies(string directory)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (string copyDirectory in Directory.EnumerateDirectories(directory))
            {
                if (Guid.TryParseExact(Path.GetFileName(copyDirectory), "N", out _)
                    && Directory.GetLastWriteTimeUtc(copyDirectory) < cutoff)
                {
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(copyDirectory, "*.pdf"))
                        {
                            File.Delete(file);
                        }
                        Directory.Delete(copyDirectory);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The target application may still be using the file.
                    }
                }
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.pdf"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The target application may still be using the file.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Default.Write(nameof(PdfClipboard), ex);
        }
    }
}
