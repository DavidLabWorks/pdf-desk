using System.Runtime.InteropServices.WindowsRuntime;
using PDFDesk.Engine;
using PDFDesk.PdfiumInterop;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using Windows.Foundation;
using Microsoft.UI.Xaml.Media;

namespace PDFDesk.Services;

/// <summary>
/// Prints a <see cref="PdfDocument"/> through the Windows print dialog.
/// Pages are rendered by PDFium at ~150 DPI into Image elements that XAML's
/// PrintDocument paginates. Preview pages render on demand; the final job
/// honors the dialog's custom page-range option so huge documents don't have
/// to materialize entirely.
/// </summary>
public sealed class PrintService
{
    private const float PrintDpi = 150f;

    private const string ScaleOption = "PDFDesk.PageScaling";
    private const string ColorOption = "PDFDesk.ColorMode";
    private sealed class JobPreferences
    {
        internal readonly string ScaleLabel = UiText.Translate("Page scaling");
        internal readonly string FitLabel = UiText.Translate("Fit oversized pages");
        internal readonly string ActualLabel = UiText.Translate("Actual size");
        internal readonly string ColorLabel = UiText.Translate("Color mode");
        internal readonly string MonochromeLabel = UiText.Translate("Black and white");
        internal readonly string FullColorLabel = UiText.Translate("Full color");
        internal volatile bool FitOversized = true;
        internal volatile bool Grayscale = true;
    }
    private readonly nint _hwnd;
    private PrintJob? _requestedJob;
    private bool _registered;
    private bool _showing;

    private readonly Action<string> _reportError;

    public PrintService(nint hwnd, Action<string> reportError)
    {
        _hwnd = hwnd;
        _reportError = reportError;
    }

    public static bool IsSupported => PrintManager.IsSupported();

    /// <summary>
    /// Opens the system print dialog for the given document.
    /// <paramref name="workQueue"/> captures current edits. The system print task then owns
    /// the snapshot and its own render queue.
    /// </summary>
    public async Task ShowAsync(PdfDocument document, string jobName, IPdfWorkQueue workQueue)
    {
        if (_showing || _requestedJob is not null) return;
        if (!_registered)
        {
            var manager = PrintManagerInterop.GetForWindow(_hwnd);
            manager.PrintTaskRequested += (_, args) =>
            {
                var requested = _requestedJob;
                _requestedJob = null;
                if (requested is null) return;
                try { requested.CreateTask(args); }
                catch (Exception ex)
                {
                    requested.Dispose();
                    _reportError(UiText.Format("Printing failed: {0}", ex.Message));
                }
            };
            _registered = true;
        }
        _showing = true;
        PdfDocument? snapshot = null;
        bool shown = false;
        try
        {
            snapshot = await workQueue.RunAsync(PdfWorkPriority.Interactive, document.CreatePrintSnapshot);
            _requestedJob = new PrintJob(snapshot, jobName, _reportError);
            snapshot = null;
            shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
        }
        finally
        {
            snapshot?.Dispose();
            if (!shown)
            {
                _requestedJob?.Dispose();
                _requestedJob = null;
            }
            _showing = false;
        }
    }

    private sealed class PrintJob : IDisposable
    {
        private readonly PdfDocument _document;
        private readonly RenderScheduler _workQueue = new((_, _) => { });
        private bool _disposed;
        private readonly string _jobName;
        private readonly Action<string> _reportError;
        private readonly PrintDocument _printDocument = new();
        private readonly IPrintDocumentSource _source;
        private readonly JobPreferences _preferences = new();
        private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        private PrintTaskOptions? _previewOptions;
        private int _layoutVersion;

        internal PrintJob(PdfDocument document, string jobName, Action<string> reportError)
        {
            _document = document;
            _jobName = jobName;
            _reportError = reportError;
            _source = _printDocument.DocumentSource;
            _printDocument.Paginate += PrintDocument_Paginate;
            _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
            _printDocument.AddPages += PrintDocument_AddPages;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _layoutVersion++;
            _printDocument.Paginate -= PrintDocument_Paginate;
            _printDocument.GetPreviewPage -= PrintDocument_GetPreviewPage;
            _printDocument.AddPages -= PrintDocument_AddPages;
            _workQueue.Dispose();
            _document.Dispose();
        }

        internal void CreateTask(PrintTaskRequestedEventArgs args)
        {
            var task = args.Request.CreatePrintTask(_jobName, sourceArgs => sourceArgs.SetSource(_source));
            task.Completed += (_, _) => _dispatcher.TryEnqueue(Dispose);
            var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
            var color = details.CreateItemListOption(ColorOption, _preferences.ColorLabel);
            color.AddItem("grayscale", _preferences.MonochromeLabel);
            color.AddItem("color", _preferences.FullColorLabel);
            color.TrySetValue("grayscale");
            var scale = details.CreateItemListOption(ScaleOption, _preferences.ScaleLabel);
            scale.AddItem("fit", _preferences.FitLabel);
            scale.AddItem("actual", _preferences.ActualLabel);
            scale.TrySetValue("fit");

            void SetDriverColor(bool inColor)
            {
                if (details.Options.TryGetValue(StandardPrintTaskOptions.ColorMode, out var option))
                    option.TrySetValue(inColor ? PrintColorMode.Color : PrintColorMode.Grayscale);
            }
            SetDriverColor(false);
            task.Options.PageRangeOptions.AllowAllPages = true;
            task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
            var displayed = details.DisplayedOptions;
            displayed.Clear();
            displayed.Add(StandardPrintTaskOptions.CustomPageRanges);
            displayed.Add(StandardPrintTaskOptions.Copies);
            displayed.Add(StandardPrintTaskOptions.Orientation);
            displayed.Add(StandardPrintTaskOptions.MediaSize);
            displayed.Add(ColorOption);
            displayed.Add(ScaleOption);
            displayed.Add(StandardPrintTaskOptions.Duplex);
            displayed.Add(StandardPrintTaskOptions.Collation);

            details.OptionChanged += (_, change) =>
            {
                switch (change.OptionId as string)
                {
                    case ColorOption:
                        bool inColor = color.Value as string == "color";
                        _preferences.Grayscale = !inColor;
                        SetDriverColor(inColor);
                        break;
                    case ScaleOption:
                        _preferences.FitOversized = scale.Value as string != "actual";
                        break;
                    default:
                        return;
                }
                _dispatcher.TryEnqueue(() =>
                {
                    _layoutVersion++;
                    _printDocument.InvalidatePreview();
                });
            };

        }

        private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
        {
            _previewOptions = (PrintTaskOptions)e.PrintTaskOptions;
            _layoutVersion++;
            _printDocument.SetPreviewPageCount(_document.PageCount, PreviewPageCountType.Final);
        }

        // Both handlers are async void: the rasterization they need belongs on the
        // render thread, and the print system explicitly allows SetPreviewPage /
        // AddPage to be called after the handler returns.
        private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
        {
            if (_previewOptions is not { } options || e.PageNumber < 1 || e.PageNumber > _document.PageCount)
            {
                return;
            }

            int pageNumber = e.PageNumber; // read before awaiting; the args are not ours to keep
            try
            {
                // Preview at screen-ish resolution; cheap and rendered on demand.
                int version = _layoutVersion;
                var paper = options.GetPageDescription((uint)(pageNumber - 1));
                var raster = await RenderAsync(pageNumber - 1, 96f, paper, _preferences.FitOversized, _preferences.Grayscale);
                if (version == _layoutVersion) _printDocument.SetPreviewPage(pageNumber, BuildPageElement(raster, paper));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfiumException)
            {
                // Document closed or page unreadable — that preview page stays blank.
            }
        }

        private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
        {
            // Honor a custom range from the dialog; otherwise print everything.
            // Materialize before the first await — the event args are only valid
            // for the synchronous part of the callback.
            List<int> pageNumbers = [];
            int submitted = 0;
            try
            {
                var options = e.PrintTaskOptions;
                var fit = _preferences.FitOversized;
                var grayscale = _preferences.Grayscale;
                int count = _document.PageCount;
                var ranges = options.CustomPageRanges;
                pageNumbers = ranges.Count > 0
                    ? [.. ranges.SelectMany(r =>
                        {
                            int first = Math.Clamp(r.FirstPageNumber, 1, Math.Max(1, count));
                            int last = Math.Min(r.LastPageNumber, count);
                            return Enumerable.Range(first, r.FirstPageNumber > count ? 0 : Math.Max(0, last - first + 1));
                        }).Distinct().OrderBy(n => n)]
                    : [.. Enumerable.Range(1, count)];
                foreach (int pageNumber in pageNumbers)
                {
                    var paper = options.GetPageDescription((uint)(pageNumber - 1));
                    var raster = await RenderAsync(pageNumber - 1, PrintDpi, paper, fit, grayscale);
                    _printDocument.AddPage(BuildPageElement(raster, paper));
                    submitted++;
                }
            }
            catch (OperationCanceledException) when (_disposed) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ErrorLog.Default.Write(nameof(PrintDocument_AddPages), ex);
                if (submitted >= pageNumbers.Count)
                {
                    _reportError(ex.Message);
                    return;
                }
                int failed = pageNumbers[submitted];
                int remaining = pageNumbers.Count - submitted;
                _reportError(UiText.Current.LanguageTag == "zh-CN"
                    ? $"打印在第 {failed} 页中断，所选页面中有 {remaining} 页未提交。"
                    : $"Printing stopped at page {failed}. {remaining} selected pages were not submitted.");
            }
            finally
            {
                try { _printDocument.AddPagesComplete(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    ErrorLog.Default.Write(nameof(PrintDocument_AddPages), ex);
                    _reportError(ex.Message);
                }
            }
        }

        /// <summary>Rasterizes one page on the render thread and copies it out of the pooled buffer.</summary>
        private async Task<RasterPage> RenderAsync(int pageIndex, float dpi, PrintPageDescription paper, bool fit, bool grayscale)
        {
            return await _workQueue.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                var (ptWidth, ptHeight) = _document.GetPageSize(pageIndex);
                double width = ptWidth * 96 / 72, height = ptHeight * 96 / 72;
                var area = paper.ImageableRect;
                double ratio = fit ? Math.Min(1, Math.Min(area.Width / width, area.Height / height)) : 1;
                // Bound the raster even when printing an unusually large sheet at actual size.
                double scale = Math.Min(dpi / 72.0 * ratio, 8192 / Math.Max(ptWidth, ptHeight));
                var page = _document.RenderPage(pageIndex, scale: (float)scale);
                try
                {
                    var pixels = new byte[page.Stride * page.Height];
                    Array.Copy(page.Pixels, pixels, pixels.Length);
                    if (grayscale)
                    {
                        // PDFium provides BGRA. Preserve alpha and use luminance weights.
                        for (int i = 0; i < pixels.Length; i += 4)
                        {
                            byte gray = (byte)((19 * pixels[i] + 183 * pixels[i + 1] + 54 * pixels[i + 2] + 128) >> 8);
                            pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
                        }
                    }
                    return new RasterPage(pixels, page.Width, page.Height, width * ratio, height * ratio);
                }
                finally
                {
                    page.Return();
                }
            });
        }

    }

    private readonly record struct RasterPage(byte[] Pixels, int Width, int Height, double WidthDip, double HeightDip);

    /// <summary>UI-thread half: WriteableBitmap and Image must be created here.</summary>
    private static Canvas BuildPageElement(RasterPage page, PrintPageDescription paper)
    {
        var bitmap = new WriteableBitmap(page.Width, page.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(page.Pixels, 0, page.Pixels.Length);
        }
        bitmap.Invalidate();

        var image = new Image
        {
            Source = bitmap,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            Width = page.WidthDip,
            Height = page.HeightDip,
        };
        var area = paper.ImageableRect;
        var sheet = new Canvas
        {
            Width = paper.PageSize.Width, Height = paper.PageSize.Height,
            Clip = new RectangleGeometry { Rect = area },
        };
        Canvas.SetLeft(image, area.X + (area.Width - page.WidthDip) / 2);
        Canvas.SetTop(image, area.Y + (area.Height - page.HeightDip) / 2);
        sheet.Children.Add(image);
        sheet.Measure(paper.PageSize);
        sheet.Arrange(new Rect(new Point(), paper.PageSize));
        return sheet;
    }
}
