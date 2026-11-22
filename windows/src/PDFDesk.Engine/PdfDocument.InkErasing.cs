using System.Numerics;
using Clipper2Lib;
using PDFDesk.PdfiumInterop;

namespace PDFDesk.Engine;

public sealed record InkEraseChange(
    int PageIndex,
    int AnnotationIndex,
    IReadOnlyList<IReadOnlyList<(float X, float Y)>> Before,
    IReadOnlyList<IReadOnlyList<(float X, float Y)>> After)
{
    public InkFootprint? BeforeFootprint { get; init; }
    public InkFootprint? AfterFootprint { get; init; }
    public long SnapshotBytes => (BeforeFootprint?.Bytes ?? 0) + (AfterFootprint?.Bytes ?? 0) + (Before.Sum(path => (long)path.Count) + After.Sum(path => (long)path.Count)) * 8;
}

public sealed partial class PdfDocument
{
    /// <summary>Erases freehand ink along trails expressed in top-left page points.</summary>
    public IReadOnlyList<InkEraseChange> EraseInk(
        IReadOnlyDictionary<int, (double X, double Y)[]> trails, double width)
    {
        RequireWritable();
        if (!double.IsFinite(width) || width <= 0 || width > float.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var changes = new List<InkEraseChange>();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            foreach (var (pageIndex, trail) in trails)
            {
                if (trail.Length == 0)
                {
                    continue;
                }
                FindInkEraseChangesLocked(pageIndex, trail, (float)(width / 2), changes);
            }
            ApplyInkEraseChanges(changes, restore: false);
        }
        return changes;
    }

    public void ApplyInkEraseChanges(IReadOnlyList<InkEraseChange> changes, bool restore)
    {
        RequireWritable();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            int applied = 0;
            try
            {
                foreach (var change in changes)
                {
                    UpdateInkPathsLocked(change, restore);
                    applied++;
                }
            }
            catch
            {
                for (int i = applied - 1; i >= 0; i--)
                {
                    UpdateInkPathsLocked(changes[i], !restore);
                }
                throw;
            }
            if (applied != 0) IsDirty = true;
        }
    }

    private void FindInkEraseChangesLocked(int pageIndex, (double X, double Y)[] trail,
        float radius, List<InkEraseChange> changes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        IntPtr page = AcquirePageLocked(pageIndex);
        if (page == IntPtr.Zero) throw PdfiumNative.LastError();
        try
        {
            // Use PDFium's axes so cropped and intrinsically rotated pages
            // use the same coordinates as their stored ink paths.
            var (width, height) = _pageSizes[pageIndex];
            int w = Math.Max(1, (int)MathF.Round(width));
            int h = Math.Max(1, (int)MathF.Round(height));
            var origin = PdfiumNative.DeviceToPage(page, w, h, 0, 0);
            var right = PdfiumNative.DeviceToPage(page, w, h, w, 0);
            var down = PdfiumNative.DeviceToPage(page, w, h, 0, h);
            var paths = trail.Select(p => new Vector2(
                (float)(origin.X + (right.X - origin.X) * p.X / width + (down.X - origin.X) * p.Y / height),
                (float)(origin.Y + (right.Y - origin.Y) * p.X / width + (down.Y - origin.Y) * p.Y / height))).ToArray();
            var eraser = new InkEraser(paths, radius);

            var footprint = InkFootprint.Sweep(new PathD(paths.Select(p => new PointD(p.X, p.Y))), radius);
            var sweepBounds = Clipper.GetBounds(footprint);
            int annotationCount = PdfiumNative.GetAnnotCount(page);
            for (int index = 0; index < annotationCount; index++)
            {
                IntPtr annot = PdfiumNative.GetAnnot(page, index);
                if (annot == IntPtr.Zero) continue;
                try
                {
                    if (PdfiumNative.GetAnnotSubtype(annot) != PdfiumNative.AnnotInk) continue;
                    if (!PdfiumNative.GetAnnotRect(annot, out float left, out float top, out float rightEdge, out float bottom)
                        || left > sweepBounds.right || rightEdge < sweepBounds.left || bottom > sweepBounds.bottom || top < sweepBounds.top) continue;
                    var strokes = new List<IReadOnlyList<(float X, float Y)>>();
                    int count = PdfiumNative.GetInkStrokeCount(annot);
                    for (int i = 0; i < count; i++)
                    {
                        var stroke = PdfiumNative.GetInkStroke(annot, i);
                        if (stroke.Length > 0) strokes.Add(stroke);
                    }
                    float inkWidth = PdfiumNative.GetAnnotBorderWidth(annot);
                    inkWidth = inkWidth > 0 ? inkWidth : 1;
                    var previous = InkFootprint.Read(annot);
                    PathsD painted;
                    if (previous != null) painted = previous.Polygons();
                    else
                    {
                        painted = new PathsD();
                        foreach (var stroke in strokes)
                            painted.AddRange(InkFootprint.Sweep(new PathD(stroke.Select(p => new PointD(p.X, p.Y))), inkWidth / 2));
                    }
                    if (Clipper.Intersect(painted, footprint, FillRule.NonZero, 3).Count == 0) continue;
                    var remaining = Clipper.Difference(painted, footprint, FillRule.NonZero, 3);
                    // InkList holds the editable centre lines. The appearance also
                    // preserves ink next to a narrow cut that InkList cannot express.
                    var trimmed = remaining.Count == 0 ? [] : eraser.Trim(strokes, inkWidth) ?? strokes;
                    var afterPaths = remaining.Count > 0 && trimmed.Count == 0 ? strokes : trimmed;
                    changes.Add(new InkEraseChange(pageIndex, index, strokes, afterPaths)
                    {
                        BeforeFootprint = previous,
                        AfterFootprint = remaining.Count == 0 ? null : new InkFootprint(InkFootprint.Coordinates(remaining),
                            previous?.R ?? 0, previous?.G ?? 0, previous?.B ?? 0, previous?.A ?? 255),
                    });
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
        }
        finally
        {
            ReleasePageLocked(pageIndex);
        }
    }

    private void UpdateInkPathsLocked(InkEraseChange change, bool restore)
    {
        IntPtr page = AcquirePageLocked(change.PageIndex);
        if (page == IntPtr.Zero) throw PdfiumNative.LastError();
        try
        {
            IntPtr annot = PdfiumNative.GetAnnot(page, change.AnnotationIndex);
            if (annot == IntPtr.Zero) throw new InvalidOperationException("Ink annotation no longer exists.");
            try
            {
                if (PdfiumNative.GetAnnotSubtype(annot) != PdfiumNative.AnnotInk)
                {
                    throw new InvalidOperationException("The annotation is no longer ink.");
                }
                // Updating in place preserves annotation order and undo indices.
                // Drop the old appearance so PDFium paints the new paths.
                if (!WritePaths(restore))
                {
                    WritePaths(!restore);
                    throw new PdfiumException("Could not update the ink paths.", 1);
                }

                bool WritePaths(bool before)
                {
                    var paths = before ? change.Before : change.After;
                    var shape = before ? change.BeforeFootprint : change.AfterFootprint;
                    if (!PdfiumNative.ClearInkStrokes(annot)) return false;
                    foreach (var stroke in paths)
                    {
                        if (!PdfiumNative.AddInkStroke(annot, [.. stroke])) return false;
                    }
                    if (shape != null)
                    {
                        if (!PdfiumNative.ClearAnnotAppearance(annot) ||
                            !PdfiumNative.GetAnnotColor(annot, out var r, out var g, out var b, out var a)) return false;
                        shape = shape with { R = r, G = g, B = b, A = a };
                    }
                    return InkFootprint.Write(annot, shape);
                }
            }
            finally
            {
                PdfiumNative.CloseAnnot(annot);
            }
        }
        finally
        {
            ReleasePageLocked(change.PageIndex);
        }
    }
}
