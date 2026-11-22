using System.Security.Cryptography;
using System.Text;
using PDFDesk.Engine;
using PDFDesk.PdfiumInterop;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PDFDesk.Services;

/// <summary>
/// Renders and disk-caches first-page thumbnails for the recent-documents
/// homepage. The path groups entries for eviction and file metadata identifies each version when available.
/// Everything runs on background threads; results are returned as encoded PNG
/// bytes the caller turns into a bitmap on the UI thread.
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private const int TargetWidthPx = 320;
    private const int MaxCachedFiles = 24;

    private readonly string _dir;
    private static readonly object CacheLock = new();
    // Only in-flight requests need tokens. Removing an entry invalidates its writers.
    private static readonly Dictionary<string, PendingReads> Pending = new(StringComparer.OrdinalIgnoreCase);
    private sealed class PendingReads { public int Count; }

    /// <summary>
    /// A serialized PDFium thread of this cache's own. Homepage thumbnails open
    /// short-lived documents unrelated to any open tab, so they have no viewer
    /// scheduler to borrow — but they must not run on pool threads either: two
    /// threads inside PDFium contend on the global lock, and a homepage render
    /// would stall the render thread mid-scroll. No tiles are ever scheduled
    /// here, so the tile callback is never invoked.
    /// </summary>
    private readonly RenderScheduler _pdfThread = new((_, _) => { });

    public ThumbnailCache()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PDFDesk", "thumbnails");
        Directory.CreateDirectory(_dir);
        // Old names were a single irreversible hash of path and timestamp.
        lock (CacheLock)
        {
            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles("*.png"))
                if (file.Name.Length == 28 && file.Name[..24].All(Uri.IsHexDigit))
                    TryDelete(file);
        }
    }

    /// <summary>
    /// Returns PNG bytes for the file's first-page thumbnail, from cache or
    /// freshly rendered. Returns null if the file can't be opened/rendered.
    /// </summary>
    public async Task<byte[]?> GetAsync(string pdfPath)
    {
        string key = PathKey(pdfPath);
        PendingReads reads;
        lock (CacheLock)
        {
            if (!Pending.TryGetValue(key, out var existing)) Pending[key] = existing = new();
            reads = existing;
            reads.Count++;
        }
        return await Task.Run(async () =>
        {
            try
            {
                string version;
                using (var source = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    version = FileAccessAdapter.ReadThumbnailVersion(source);
                lock (CacheLock)
                {
                    if (Pending.GetValueOrDefault(key) != reads) return null;
                    string prefix = $"{key}-{version}-";
                    foreach (var file in new DirectoryInfo(_dir).EnumerateFiles(prefix + "*.png"))
                    {
                        byte[] bytes = File.ReadAllBytes(file.FullName);
                        string checksum = Convert.ToHexString(SHA256.HashData(bytes));
                        if (file.Name == prefix + checksum + ".png") return bytes;
                        TryDelete(file);
                    }
                }

                var image = await _pdfThread.RunAsync(PdfWorkPriority.Thumbnail, () => RenderRaw(pdfPath));
                byte[] png = EncodePng(image);
                lock (CacheLock)
                {
                    if (Pending.GetValueOrDefault(key) != reads) return null;
                    string checksum = Convert.ToHexString(SHA256.HashData(png));
                    File.WriteAllBytes(Path.Combine(_dir, $"{key}-{image.Version}-{checksum}.png"), png);
                    TrimCache();
                }
                return png;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (CacheLock)
                    if (--reads.Count == 0 && Pending.GetValueOrDefault(key) == reads) Pending.Remove(key);
            }
        });
    }

    /// <summary>
    /// Deletes a file's cached thumbnail.
    ///
    /// Called when a document is removed from recents: the cached PNG is a
    /// picture of that document's first page, so leaving it behind would keep
    /// the contents of a file the user asked PDFDesk to forget.
    /// </summary>
    public void Forget(string pdfPath)
    {
        RemoveCachedFiles(PathKey(pdfPath));
    }

    public void ForgetAll() => RemoveCachedFiles(null);

    private void RemoveCachedFiles(string? key)
    {
        lock (CacheLock)
        {
            if (key is null) Pending.Clear(); else Pending.Remove(key);
            string pattern = key is null ? "*.png" : key + "-*.png";
            foreach (var file in new DirectoryInfo(_dir).EnumerateFiles(pattern))
                TryDelete(file);
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try { file.Delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Default.Write(nameof(ThumbnailCache), ex);
        }
    }

    /// <summary>Raw BGRA pixels detached from the pooled render buffer, safe to hand off the PDFium thread.</summary>
    private readonly record struct RawImage(byte[] Pixels, int Width, int Height, string Version);

    /// <summary>Runs on the PDFium thread. PNG encoding deliberately does not — it needs no PDFium state.</summary>
    private static RawImage RenderRaw(string pdfPath)
    {
        using var doc = PdfDocument.Open(pdfPath);
        var (ptWidth, _) = doc.GetPageSize(0);
        float scale = TargetWidthPx / Math.Max(1f, ptWidth);
        var page = doc.RenderPage(0, scale);
        try
        {
            // The pooled buffer may be longer than the image; copy the exact bytes.
            var pixels = new byte[page.Stride * page.Height];
            Array.Copy(page.Pixels, pixels, pixels.Length);
            return new RawImage(pixels, page.Width, page.Height, doc.ThumbnailVersion);
        }
        finally
        {
            page.Return();
        }
    }

    private static byte[] EncodePng(RawImage image)
    {
        // BitmapEncoder needs a random-access stream; encode in-memory to bytes.
        using var stream = new InMemoryRandomAccessStream();
        var encoder = BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().GetAwaiter().GetResult();

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)image.Width, (uint)image.Height, 96, 96, image.Pixels);
        encoder.FlushAsync().AsTask().GetAwaiter().GetResult();

        stream.Seek(0);
        var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;
        reader.LoadAsync(size).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string PathKey(string pdfPath)
    {
        var path = Path.GetFullPath(pdfPath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];
    }

    private void TrimCache()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles("*.png")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(MaxCachedFiles);
            foreach (var f in files)
            {
                f.Delete();
            }
        }
        catch
        {
            // Pruning is best-effort.
        }
    }

    public void Dispose() => _pdfThread.Dispose();
}
