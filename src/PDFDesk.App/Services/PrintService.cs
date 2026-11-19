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
    // PrintTaskRequested and OptionChanged can run off the XAML thread.
    private sealed class ScalingOptions
    {
        internal readonly string Label = UiText.Translate("Page scaling");
        internal readonly string Fit = UiText.Translate("Fit oversized pages");
        internal readonly string Actual = UiText.Translate("Actual size");
        internal volatile bool FitOversized = true;
    }
    private ScalingOptions _scaling = null!;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private PrintTaskOptions? _previewOptions;
    private int _layoutVersion;
    private readonly nint _hwnd;
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printSource;
    private PdfDocument? _document;
    private IPdfWorkQueue? _workQueue;
    private string _jobName = "PDF Desk";
    private bool _registered;

    public PrintService(nint hwnd) => _hwnd = hwnd;

    public static bool IsSupported => PrintManager.IsSupported();

    /// <summary>
    /// Opens the system print dialog for the given document.
    /// <paramref name="workQueue"/> is the document's render thread — page
    /// rasterization is scheduled there, never run inline in the print
    /// callbacks (which arrive on the UI thread).
    /// </summary>
    public async Task ShowAsync(PdfDocument document, string jobName, IPdfWorkQueue workQueue)
    {
        _scaling = new ScalingOptions();
        _previewOptions = null;
        _layoutVersion++;
        _document = document;
        _workQueue = workQueue;
        _jobName = jobName;

        if (!_registered)
        {
            var manager = PrintManagerInterop.GetForWindow(_hwnd);
            manager.PrintTaskRequested += Manager_PrintTaskRequested;
            _registered = true;
        }

        // A fresh PrintDocument per job: reusing one across jobs breaks preview.
        _printDocument = new PrintDocument();
        _printDocument.Paginate += PrintDocument_Paginate;
        _printDocument.GetPreviewPage += PrintDocument_GetPreviewPage;
        _printDocument.AddPages += PrintDocument_AddPages;
        _printSource = _printDocument.DocumentSource;

        await PrintManagerInterop.ShowPrintUIForWindowAsync(_hwnd);
    }

    private void Manager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var task = args.Request.CreatePrintTask(_jobName, sourceArgs =>
        {
            sourceArgs.SetSource(_printSource);
        });
        var job = _printDocument!;
        var scaling = _scaling;
        var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
        var scale = details.CreateItemListOption(ScaleOption, scaling.Label);
        scale.AddItem("fit", scaling.Fit);
        scale.AddItem("actual", scaling.Actual);
        scale.TrySetValue("fit");
        details.DisplayedOptions.Add(ScaleOption);
        details.OptionChanged += (_, change) =>
        {
            if (change.OptionId as string == ScaleOption)
            {
                scaling.FitOversized = scale.Value as string != "actual";
                _dispatcher.TryEnqueue(() =>
                {
                    if (_printDocument != job) return;
                    _layoutVersion++;
                    job.InvalidatePreview();
                });
            }
        };
        // Let the dialog offer "All pages" vs a custom range.
        task.Options.PageRangeOptions.AllowAllPages = true;
        task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
    }

    private void PrintDocument_Paginate(object sender, PaginateEventArgs e)
    {
        _previewOptions = (PrintTaskOptions)e.PrintTaskOptions;
        _layoutVersion++;
        _printDocument?.SetPreviewPageCount(_document?.PageCount ?? 0, PreviewPageCountType.Final);
    }

    // Both handlers are async void: the rasterization they need belongs on the
    // render thread, and the print system explicitly allows SetPreviewPage /
    // AddPage to be called after the handler returns.
    private async void PrintDocument_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (_previewOptions is not { } options || _document is not { } doc || e.PageNumber < 1 || e.PageNumber > doc.PageCount)
        {
            return;
        }

        int pageNumber = e.PageNumber; // read before awaiting; the args are not ours to keep
        try
        {
            // Preview at screen-ish resolution; cheap and rendered on demand.
            int version = _layoutVersion;
            var paper = options.GetPageDescription((uint)(pageNumber - 1));
            var raster = await RenderAsync(doc, pageNumber - 1, 96f, paper, _scaling.FitOversized);
            if (version == _layoutVersion) _printDocument?.SetPreviewPage(pageNumber, BuildPageElement(raster, paper));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfiumException)
        {
            // Document closed or page unreadable — that preview page stays blank.
        }
    }

    private async void PrintDocument_AddPages(object sender, AddPagesEventArgs e)
    {
        if (_document is not { } doc || _printDocument is null)
        {
            return;
        }

        // Honor a custom range from the dialog; otherwise print everything.
        // Materialize before the first await — the event args are only valid
        // for the synchronous part of the callback.
        var job = _printDocument;
        var options = e.PrintTaskOptions;
        var fit = _scaling.FitOversized;
        var ranges = options.CustomPageRanges;
        List<int> pageNumbers = ranges.Count > 0
            ? [.. ranges.SelectMany(r => Enumerable.Range(r.FirstPageNumber, r.LastPageNumber - r.FirstPageNumber + 1))
                        .Where(n => n >= 1 && n <= doc.PageCount)
                        .Distinct()
                        .OrderBy(n => n)]
            : [.. Enumerable.Range(1, doc.PageCount)];

        try
        {
            foreach (int pageNumber in pageNumbers)
            {
                var paper = options.GetPageDescription((uint)(pageNumber - 1));
                var raster = await RenderAsync(doc, pageNumber - 1, PrintDpi, paper, fit);
                job.AddPage(BuildPageElement(raster, paper));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or PdfiumException)
        {
            // Fall through: the job still has to be completed or the dialog hangs.
        }
        finally
        {
            job.AddPagesComplete();
        }
    }

    /// <summary>A rendered page detached from the pooled buffer, plus its physical size in DIPs.</summary>
    private readonly record struct RasterPage(byte[] Pixels, int Width, int Height, double WidthDip, double HeightDip);

    /// <summary>Rasterizes one page on the render thread and copies it out of the pooled buffer.</summary>
    private async Task<RasterPage> RenderAsync(PdfDocument doc, int pageIndex, float dpi, PrintPageDescription paper, bool fit)
    {
        if (_workQueue is not { } queue)
        {
            throw new InvalidOperationException("PrintService was used before ShowAsync supplied a work queue.");
        }

        return await queue.RunAsync(PdfWorkPriority.Interactive, () =>
        {
            var (ptWidth, ptHeight) = doc.GetPageSize(pageIndex);
            double width = ptWidth * 96 / 72, height = ptHeight * 96 / 72;
            var area = paper.ImageableRect;
            double ratio = fit ? Math.Min(1, Math.Min(area.Width / width, area.Height / height)) : 1;
            // Bound the raster even when printing an unusually large sheet at actual size.
            double scale = Math.Min(dpi / 72.0 * ratio, 8192 / Math.Max(ptWidth, ptHeight));
            var page = doc.RenderPage(pageIndex, scale: (float)scale);
            try
            {
                var pixels = new byte[page.Stride * page.Height];
                Array.Copy(page.Pixels, pixels, pixels.Length);
                return new RasterPage(pixels, page.Width, page.Height, width * ratio, height * ratio);
            }
            finally
            {
                page.Return();
            }
        });
    }

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
