using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using PDFDesk.Engine;
using PDFDesk.Services;
using Windows.Foundation;

namespace PDFDesk.Controls;

public sealed partial class PdfViewer
{
    public const double MinEraserWidthPt = 4;
    public const double MaxEraserWidthPt = 64;

    private Pointer? _eraserPointer;
    private uint? _eraserPointerId => _eraserPointer?.PointerId;
    private Point? _eraserLocation;
    private double _eraserWidth = AppSettings.DefaultEraserWidthPt;
    private EraserGesture? _eraserGesture;

    private sealed class EraserGesture(PdfDocument document, double width, PageLayout layout, double zoom,
        int rotation, (float Width, float Height)[] pageSizes)
    {
        public PdfDocument Document { get; } = document;
        public double Width { get; } = width;
        public PageLayout Layout { get; } = layout;
        public double Zoom { get; } = zoom;
        public int Rotation { get; } = rotation;
        public (float Width, float Height)[] PageSizes { get; } = pageSizes;
        public List<Point> Trail { get; } = [];
        public Dictionary<(int Page, int Annotation), InkEraseChange> Changes { get; } = [];
        public bool HasPendingPoints { get; set; }
        public bool Finished { get; set; }
        public Task PreviousEdit { get; init; } = Task.CompletedTask;
        public Task? Worker { get; set; }
    }

    public void SetEraserWidth(double width)
    {
        var location = _eraserLocation;
        SetEraserLocation(null);
        _eraserWidth = Math.Clamp(width, MinEraserWidthPt, MaxEraserWidthPt);
        SetEraserLocation(location);
    }

    private void BeginEraser(Point location, Pointer pointer)
    {
        if (_eraserGesture is { Finished: false } || _layout is not { } layout || _document is not { } document)
        {
            return;
        }

        var gesture = new EraserGesture(document, _eraserWidth, layout, _zoom, _rotation, _pageSizes)
        {
            PreviousEdit = _eraserGesture?.Worker ?? Task.CompletedTask
        };
        _eraserGesture = gesture;
        _eraserPointer = pointer;
        RecordEraserPoint(gesture, location);
        SetEraserLocation(location);
        BeginExclusiveGesture(pointer);
        ScheduleEraserBatch(gesture);
    }

    private void UpdateEraser(PointerRoutedEventArgs e, Point location)
    {
        if (_eraserPointerId is { } pointerId && _eraserGesture is { } gesture)
        {
            if (e.Pointer.PointerId != pointerId)
            {
                return;
            }
            var points = e.GetIntermediatePoints(DocumentExtent);
            for (int i = points.Count - 1; i >= 0; i--)
            {
                RecordEraserPoint(gesture, points[i].Position);
            }
            RecordEraserPoint(gesture, location);
            ScheduleEraserBatch(gesture);
        }
        SetEraserLocation(location);
    }

    private static void RecordEraserPoint(EraserGesture gesture, Point point)
    {
        if (gesture.Trail.Count == 0 || gesture.Trail[^1] != point)
        {
            gesture.Trail.Add(point);
            gesture.HasPendingPoints = true;
        }
    }

    private void ScheduleEraserBatch(EraserGesture gesture)
    {
        if (gesture.Worker is null && (gesture.HasPendingPoints || gesture.Finished))
        {
            gesture.Worker = ProcessEraserBatchesAsync(gesture);
        }
    }

    private async Task ProcessEraserBatchesAsync(EraserGesture gesture)
    {
        // Publish the task before any synchronous completion or close path.
        await Task.Yield();
        try
        {
            await gesture.PreviousEdit;
            while (_document == gesture.Document && gesture.HasPendingPoints)
            {
                var points = gesture.Trail.ToArray();
                gesture.Trail.Clear();
                gesture.Trail.Add(points[^1]); // join the next batch to this one
                gesture.HasPendingPoints = false;

                var trails = GetEraserPageTrails(points, gesture);
                if (trails.Count > 0)
                {
                    var changes = await _scheduler.RunAsync(PdfWorkPriority.Interactive,
                        () => gesture.Document.EraseInk(trails, gesture.Width));
                    foreach (var change in changes)
                    {
                        var key = (change.PageIndex, change.AnnotationIndex);
                        gesture.Changes[key] = gesture.Changes.TryGetValue(key, out var original)
                            ? original with { After = change.After, AfterFootprint = change.AfterFootprint }
                            : change;
                    }
                    if (_document == gesture.Document)
                    {
                        foreach (int page in changes.Select(change => change.PageIndex).Distinct())
                        {
                            var trail = trails[page];
                            double left = trail.Min(point => point.X) - gesture.Width / 2;
                            double top = trail.Min(point => point.Y) - gesture.Width / 2;
                            RefreshPageTiles(page, new TextRect(left, top,
                                trail.Max(point => point.X) + gesture.Width / 2 - left,
                                trail.Max(point => point.Y) + gesture.Width / 2 - top));
                        }
                    }
                }
                if (!gesture.Finished && gesture.HasPendingPoints)
                {
                    // Leave time to render between editing batches and group
                    // the pointer samples that arrive during that interval.
                    await Task.Delay(8);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (PDFDesk.PdfiumInterop.PdfiumException ex)
        {
            ErrorLog.Default.Write(nameof(ProcessEraserBatchesAsync), ex);
        }
        finally
        {
            gesture.Worker = null;
            if (gesture.Finished)
            {
                if (ReferenceEquals(_eraserGesture, gesture)) _eraserGesture = null;
                PublishEraserEdit(gesture);
            }
        }
    }

    private static Dictionary<int, (double X, double Y)[]> GetEraserPageTrails(Point[] points, EraserGesture gesture)
    {
        var trails = new Dictionary<int, (double X, double Y)[]>();
        double radius = gesture.Width * gesture.Zoom / 2;
        double left = points.Min(point => point.X) - radius;
        double top = points.Min(point => point.Y) - radius;
        var bounds = new DipRect(left, top, points.Max(point => point.X) + radius - left,
            points.Max(point => point.Y) + radius - top);
        var (first, last) = gesture.Layout.PagesInVerticalRange(bounds.Y, bounds.Bottom);
        for (int page = first; page <= last; page++)
        {
            var rect = gesture.Layout.GetPageRect(page);
            if (bounds.Intersects(rect))
            {
                // Keep points outside the page. The edge of the eraser can
                // still overlap ink even when its centre is in the margin.
                var (width, height) = gesture.PageSizes[page];
                var transform = new PageRotationTransform(gesture.Rotation, width, height);
                trails[page] = points.Select(point => transform.ToUnrotated(
                    (point.X - rect.X) / gesture.Zoom, (point.Y - rect.Y) / gesture.Zoom)).ToArray();
            }
        }
        return trails;
    }

    private void PublishEraserEdit(EraserGesture gesture)
    {
        if (_document != gesture.Document || gesture.Changes.Count == 0)
        {
            return;
        }

        var changes = gesture.Changes.Values.ToArray();
        int[] pages = changes.Select(change => change.PageIndex).Distinct().ToArray();
        foreach (int page in pages) RefreshPageTiles(page);
        DocumentEdited?.Invoke(this, EventArgs.Empty);
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = "erase",
            PageIndex = pages[0],
            InkPages = pages,
            SnapshotBytes = changes.Sum(change => change.SnapshotBytes),
            UndoAction = pdf => pdf.ApplyInkEraseChanges(changes, restore: true),
            RedoAction = pdf => pdf.ApplyInkEraseChanges(changes, restore: false),
        });
    }

    private void CommitEraser(Point? end)
    {
        if (_eraserGesture is not { Finished: false } gesture) return;
        if (end is { } location) RecordEraserPoint(gesture, location);
        SetEraserLocation(null);
        _eraserPointer = null;
        if (end is { } hover) SetEraserLocation(hover);
        gesture.Finished = true;
        ScheduleEraserBatch(gesture);
    }

    internal Task FinishEraserAsync()
    {
        var pointer = _eraserPointer;
        CommitEraser(null);
        if (pointer is not null)
        {
            EndExclusiveGesture(pointer);
        }
        return _eraserGesture?.Worker ?? Task.CompletedTask;
    }

    private void CancelEraser()
    {
        _ = FinishEraserAsync();
        SetEraserLocation(null);
    }

    private float EraserRadius => (float)((_eraserPointer is null ? _eraserWidth : _eraserGesture!.Width) * _zoom / 2);

    private void SetEraserLocation(Point? location)
    {
        if (_eraserLocation == location)
        {
            return;
        }
        var previous = _eraserLocation;
        _eraserLocation = location;
        float pad = EraserRadius + 3;
        if (previous is { } old)
        {
            InvalidateDocumentRegion(new Rect(old.X - pad, old.Y - pad, pad * 2, pad * 2));
        }
        if (location is { } active)
        {
            InvalidateDocumentRegion(new Rect(active.X - pad, active.Y - pad, pad * 2, pad * 2));
        }
    }

    private void DrawEraser(CanvasDrawingSession session)
    {
        if (_eraserLocation is { } point)
        {
            float radius = EraserRadius;
            session.DrawCircle((float)point.X, (float)point.Y, radius, Colors.White, 3);
            session.DrawCircle((float)point.X, (float)point.Y, radius, Colors.Black, 1);
        }
    }
}
