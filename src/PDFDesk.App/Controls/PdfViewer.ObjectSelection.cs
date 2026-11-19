using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PDFDesk.Engine;
using PDFDesk.Styles;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace PDFDesk.Controls;

// Selecting something already placed on the page, so it can be moved or resized
// instead of erased and put back.
//
// Only stamps are selectable, and PDFDesk writes two kinds of them: a picture (a
// signature, or an image the user placed) and a text box. Markup is anchored to
// the words it covers and dragging it off them would be wrong, and ink is a path
// rather than a rect.
//
// The two kinds select, move and delete identically. They differ in one place
// only, and it is the interesting one: a picture is resized by scaling its
// pixels, while a text box is resized by RE-FLOWING its words into the new
// width at the same point size. That is the whole reason for keeping text as
// text rather than as a picture of text.
//
// Which means only a picture is aspect-locked. Stretching a photograph looks
// wrong immediately, so its corner drag moves width and height together; a text
// box has no aspect to keep, because its height is not a thing anyone chooses —
// it is however tall the words come out once they have wrapped. So the drag
// sets the width and the height is read back from the document afterwards.
public sealed partial class PdfViewer
{
    /// <summary>
    /// What is selected: which page, which annotation, where it sits (page
    /// points), and which kind it is.
    /// </summary>
    private (int Page, int Index, Rect Local, StampKind Kind)? _selected;

    private bool _draggingStamp;
    private Point _stampDragOffset;
    private Rect _stampDragTarget;
    private bool _stampMovePending;
    private int _dragPreviewVersion;
    private bool _dragPreviewPending;
    private bool _dragPreviewCommitted;
    private (int Page, CanvasBitmap Source, Rect SourceBounds, CanvasBitmap Target, Rect TargetBounds)? _dragPreview;

    /// <summary>
    /// Where the object sat before a move or a resize started. Both previews
    /// mutate <c>_selected</c> live, so a gesture the system cancels — which on
    /// touch is routine — has to have something to put back, or the selection
    /// frame is left claiming a size and position the document never took.
    /// </summary>
    private Rect _dragBefore;
    private Rect _resizeBefore;

    // ---- resize ----

    /// <summary>Which corner is being dragged (0 TL, 1 TR, 2 BR, 3 BL), or -1.</summary>
    private int _resizeCorner = -1;

    /// <summary>The corner that stays put for the duration of a resize, in page points.</summary>
    private Point _resizeAnchor;
    private double _resizeTextOffsetX;

    /// <summary>Width ÷ height at the moment the resize started, so the drag cannot distort it.</summary>
    private double _resizeAspect = 1;

    /// <summary>
    /// Handle size in DIPs, as DRAWN. Big enough to grab with a mouse, small
    /// enough not to swamp a short signature. What a finger may grab it from is
    /// a separate and much larger number — see <see cref="TouchMetrics"/>.
    /// </summary>
    private const double HandleSizeDip = TouchMetrics.HandleDrawDip;

    /// <summary>No dimension below this, in page points — an object dragged to nothing cannot be grabbed again.</summary>
    private const double MinStampPt = 12;

    private bool IsResizingStamp => _resizeCorner >= 0;

    internal bool HasSelectedObject => _selected is not null;

    /// <summary>FPDF_ANNOT_SUBTYPE_STAMP.</summary>
    private const int StampSubtype = 13;

    /// <summary>
    /// Abandons a corner drag and puts the frame back where the document still
    /// has it. Nothing was written, so there is nothing to undo.
    /// </summary>
    private void CancelStampResize()
    {
        if (!IsResizingStamp)
        {
            return;
        }
        _resizeCorner = -1;
        ClearStampDragPreview();
        if (_selected is { } selected)
        {
            _selected = (selected.Page, selected.Index, _resizeBefore, selected.Kind);
            Canvas.Invalidate();
        }
    }

    /// <summary>Abandons a move, same as above.</summary>
    private void CancelStampDrag()
    {
        if (!_draggingStamp)
        {
            return;
        }
        _draggingStamp = false;
        ClearStampDragPreview();
        if (_selected is { } selected)
        {
            _selected = (selected.Page, selected.Index, _dragBefore, selected.Kind);
            Canvas.Invalidate();
        }
    }

    /// <summary>Drops the selection. Returns true when there was one, so Esc can be consumed.</summary>
    public bool ClearObjectSelection()
    {
        if (_selected is null)
        {
            return false;
        }
        _selected = null;
        _draggingStamp = false;
        ClearStampDragPreview();
        _resizeCorner = -1;
        Canvas.Invalidate();
        return true;
    }

    // ---- selection ----

    /// <summary>
    /// Handles a press with no tool armed: selects a placed object under the
    /// pointer, or starts dragging one that is already selected. Returns true
    /// when the press was consumed.
    ///
    /// Runs before links and text selection but is deliberately narrow — only
    /// a press actually inside an object's rect is taken, so ordinary text
    /// selection everywhere else on the page is untouched.
    /// </summary>
    private bool TryHandleStampPress(Point docPoint, Pointer pointer, bool touch)
    {
        if (_stampMovePending)
        {
            return true;
        }
        if (_layout is null || _document is null)
        {
            return false;
        }

        // Dragging the current selection takes priority: no round-trip, and it
        // keeps the grab responsive on the very first move.
        if (_selected is { } selected)
        {
            int page = _layout.PageAt(docPoint.X, docPoint.Y);
            var local = ToPageLocal(page, docPoint);

            // Handles are checked before the body: they overlap the corners, and
            // a press there means resize, not move.
            int corner = page == selected.Page
                ? HitTestStampHandle(StampSelectionBounds(selected.Local, selected.Kind), local, touch)
                : -1;
            if (corner >= 0)
            {
                ClearStampDragPreview();
                Scroller.Focus(FocusState.Programmatic);
                _resizeCorner = corner;
                _resizeBefore = selected.Local;
                _stampDragTarget = selected.Local;
                _resizeAspect = selected.Local.Width / Math.Max(1e-6, selected.Local.Height);
                _resizeTextOffsetX = selected.Kind == StampKind.Text
                    ? local.X - (corner is 0 or 3 ? selected.Local.Left : selected.Local.Right)
                    : 0;
                // The corner diagonally opposite is what stays still.
                _resizeAnchor = corner switch
                {
                    0 => new Point(selected.Local.Right, selected.Local.Bottom),
                    1 => new Point(selected.Local.X, selected.Local.Bottom),
                    2 => new Point(selected.Local.X, selected.Local.Y),
                    _ => new Point(selected.Local.Right, selected.Local.Y),
                };
                BeginExclusiveGesture(pointer);
                if (selected.Kind == StampKind.Image) _ = PrepareImagePreviewAsync(selected.Page, selected.Index, selected.Local);
                return true;
            }

            // The body is widened for a finger the same way the handles are, so
            // grabbing a thin signature to move it does not need mouse aim.
            double slop = TouchSlopPt(touch);
            var body = new Rect(
                selected.Local.X - slop, selected.Local.Y - slop,
                selected.Local.Width + (slop * 2), selected.Local.Height + (slop * 2));
            if (page == selected.Page && body.Contains(new Point(local.X, local.Y)))
            {
                ClearStampDragPreview();
                Scroller.Focus(FocusState.Programmatic);
                _draggingStamp = true;
                _dragBefore = selected.Local;
                _stampDragTarget = selected.Local;
                _stampDragOffset = new Point(local.X - selected.Local.X, local.Y - selected.Local.Y);
                BeginExclusiveGesture(pointer);
                if (selected.Kind == StampKind.Image) _ = PrepareImagePreviewAsync(selected.Page, selected.Index, selected.Local);
                return true;
            }
        }

        SelectStampAt(docPoint, TouchSlopPt(touch));

        // Never consume the press itself. The hit-test is asynchronous, so
        // claiming it here would swallow a click that turns out to have landed
        // on nothing — and with it the text selection the user meant to start.
        return false;
    }

    private async void SelectStampAt(Point docPoint, double slop = 0)
    {
        if (_layout is null || _document is not { } document)
        {
            return;
        }

        int page = _layout.PageAt(docPoint.X, docPoint.Y);
        var local = ToPageLocal(page, docPoint);

        // The kind comes back on the same hop as the hit test. Asking for it
        // separately would be a second round-trip on every click on a page.
        (AnnotationInfo? Hit, StampKind Kind) found;
        try
        {
            found = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                var hit = document.GetAnnotations(page).LastOrDefault(a =>
                    a.Subtype == StampSubtype &&
                    local.X >= a.X - slop && local.X <= a.X + a.Width + slop &&
                    local.Y >= a.Y - slop && local.Y <= a.Y + a.Height + slop);
                return (hit, hit is null ? StampKind.None : document.GetStampKind(page, hit.Index));
            });
        }
        catch
        {
            return;
        }
        if (_document != document || IsEditingText)
        {
            return;
        }

        // A stamp PDFDesk cannot edit is not worth selecting: the handles would
        // draw and then refuse every drag.
        var next = found.Hit is { } hit && found.Kind != StampKind.None
            ? (page, hit.Index, new Rect(hit.X, hit.Y, hit.Width, hit.Height), found.Kind)
            : ((int Page, int Index, Rect Local, StampKind Kind)?)null;

        if (next?.Index != _selected?.Index || next?.Page != _selected?.Page)
        {
            if (next is not null)
            {
                KillFormFocus();
                Scroller.Focus(FocusState.Programmatic);
            }
            _selected = next;
            Canvas.Invalidate();
        }
    }

    // ---- resize ----

    /// <summary>
    /// Which corner handle a page-local point is on, or -1.
    ///
    /// The handles are drawn at a fixed size in DIPs so they stay grabbable at
    /// any zoom, which means the hit area has to be converted back into page
    /// points rather than being a constant. The tolerance is generous — a
    /// signature is often only a few points tall, and missing the handle to
    /// start a move instead is a worse outcome than the reverse.
    /// </summary>
    private int HitTestStampHandle(Rect local, (double X, double Y) point, bool touch)
    {
        double reach = TouchMetrics.HandleReachPt(touch, _zoom);

        var corners = new[]
        {
            new Point(local.X, local.Y),
            new Point(local.Right, local.Y),
            new Point(local.Right, local.Bottom),
            new Point(local.X, local.Bottom),
        };

        for (int i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(point.X - corners[i].X) <= reach && Math.Abs(point.Y - corners[i].Y) <= reach)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Live preview of a corner drag.
    ///
    /// A picture is aspect-locked: the pointer's distance from the anchor sets
    /// the width and the height follows, so it keeps its proportions however the
    /// corner is dragged. A text box is not, because it has no proportions to
    /// keep — the drag sets the width alone and the preview holds the height it
    /// already had, since what the words will actually wrap to is not known
    /// until the document has re-laid them out on release.
    ///
    /// Committing happens on release either way.
    /// </summary>
    private void UpdateStampResize(Point docPoint)
    {
        if (!IsResizingStamp || _selected is not { } selected || _layout is null)
        {
            return;
        }

        var local = ToPageLocal(selected.Page, docPoint);
        local.X -= _resizeTextOffsetX;
        var size = _pageSizes[selected.Page];
        bool isText = selected.Kind == StampKind.Text;

        // Keep the whole object on the page. The anchor is fixed, so the room
        // available is the distance from it to whichever edge the drag is
        // heading for.
        double roomX = local.X >= _resizeAnchor.X ? size.Width - _resizeAnchor.X : _resizeAnchor.X;
        double roomY = local.Y >= _resizeAnchor.Y ? size.Height - _resizeAnchor.Y : _resizeAnchor.Y;

        double width = Math.Min(Math.Abs(local.X - _resizeAnchor.X), roomX);
        double height;
        if (isText)
        {
            height = _resizeBefore.Height;
        }
        else
        {
            // Width drives; height follows the original aspect, then whichever
            // of the two runs out of room first pulls the other back.
            height = Math.Min(width / Math.Max(1e-6, _resizeAspect), roomY);
            width = height * _resizeAspect;
        }

        width = Math.Max(MinStampPt, width);
        if (!isText)
        {
            height = Math.Max(MinStampPt, height);
        }

        // The rect grows away from the anchor, in whichever direction the
        // pointer went. A text box grows only sideways, so its top edge stays
        // where it was rather than chasing the pointer up the page.
        double x = local.X >= _resizeAnchor.X ? _resizeAnchor.X : _resizeAnchor.X - width;
        double y = isText
            ? selected.Local.Y
            : local.Y >= _resizeAnchor.Y ? _resizeAnchor.Y : _resizeAnchor.Y - height;

        _stampDragTarget = new Rect(x, y, width, height);
        if (isText)
        {
            _selected = (selected.Page, selected.Index, _stampDragTarget, selected.Kind);
            Canvas.Invalidate();
        }
        else UpdateImagePreviewBounds(selected.Page, selected.Index);
    }

    private async void CommitStampResize()
    {
        _resizeCorner = -1;

        if (_selected is not { } selected || _document is not { } document)
        {
            return;
        }

        int page = selected.Page;
        int index = selected.Index;
        var box = _stampDragTarget;
        var kind = selected.Kind;

        // A picture is re-created from its pixels; text is re-created from its
        // words, re-wrapped to the new width at the size it already had. Both
        // come back as the same pair — where it now lives, and how to put the
        // original back. Text passes no height: the wrap decides that.
        _stampMovePending = true;
        (int NewIndex, AnnotationSpec Before)? result;
        try
        {
            result = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () => kind == StampKind.Text
                ? document.ResizeTextBox(page, index, box.X, box.Y, box.Width)
                : document.ResizeStamp(page, index, box.X, box.Y, box.Width, box.Height));
        }
        catch
        {
            result = null;
        }
        finally
        {
            _stampMovePending = false;
        }
        if (_document != document) return;
        if (result is not { } resized)
        {
            // Nothing changed in the file, so put the on-screen box back rather
            // than leaving a selection that lies about the object's size.
            ClearStampDragPreview();
            RefreshSelectedFromDocument(page, index, kind);
            return;
        }

        _selected = (page, resized.NewIndex, box, kind);

        if (kind == StampKind.Text)
        {
            // The height is whatever the words wrapped to, which the drag never
            // knew. Re-read rather than leave the selection frame sitting off
            // the block it belongs to.
            RefreshSelectedFromDocument(page, resized.NewIndex, kind);
        }

        RefreshPageTiles(page);
        _dragPreviewCommitted = _dragPreview.HasValue;
        RetireCommittedDragPreview(page);
        Canvas.Invalidate();
        DocumentEdited?.Invoke(this, EventArgs.Empty);

        // A resize is a remove-and-re-create, so undo is the same shape: drop
        // what is there now and rebuild the original from its spec. A picture's
        // pixels ride along in that spec, which is why SnapshotBytes is charged
        // for them here, unlike a move; text carries only its words.
        var before = resized.Before;
        int newIndex = resized.NewIndex;
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = kind == StampKind.Text ? "resize text" : "resize image",
            PageIndex = page,
            SnapshotBytes = before.Stamp?.Bgra.Length ?? 0,
            UndoAction = d =>
            {
                d.RemoveAnnotation(page, newIndex);
                d.AddAnnotationFromSpec(before);
            },
            RedoAction = d =>
            {
                // Undo appended the original, so the thing to resize again is
                // the last annotation rather than the index it used to have.
                int last = d.GetAnnotations(page).Count - 1;
                if (kind == StampKind.Text)
                {
                    d.ResizeTextBox(page, last, box.X, box.Y, box.Width);
                }
                else
                {
                    d.ResizeStamp(page, last, box.X, box.Y, box.Width, box.Height);
                }
            },
        });
    }

    /// <summary>
    /// Re-reads the selected object's rect from the document, for when an edit
    /// changed it or did not go through and the preview has to be walked back.
    /// </summary>
    private async void RefreshSelectedFromDocument(int page, int index, StampKind kind)
    {
        if (_document is not { } document)
        {
            return;
        }
        IReadOnlyList<AnnotationInfo> annotations;
        try
        {
            annotations = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.GetAnnotations(page));
        }
        catch
        {
            return;
        }
        if (_document != document || annotations.ElementAtOrDefault(index) is not { } current)
        {
            ClearObjectSelection();
            return;
        }
        _selected = (page, index, new Rect(current.X, current.Y, current.Width, current.Height), kind);
        Canvas.Invalidate();
    }

    // ---- drag ----

    private void UpdateStampDrag(Point docPoint, bool showPreview = true)
    {
        if (!_draggingStamp || _selected is not { } selected || _layout is null)
        {
            return;
        }

        var local = ToPageLocal(selected.Page, docPoint);
        // The object's rect is in the file's own axes, so the limits are the
        // page's unrotated size — the layout rect has them swapped on a
        // quarter turn, which would let a drag run off the short edge.
        var size = _pageSizes[selected.Page];
        double maxX = size.Width - selected.Local.Width;
        double maxY = size.Height - selected.Local.Height;

        // Keep it on the page: anything dragged past the edge would be clipped
        // by every reader that opens the file.
        double x = Math.Clamp(local.X - _stampDragOffset.X, 0, Math.Max(0, maxX));
        double y = Math.Clamp(local.Y - _stampDragOffset.Y, 0, Math.Max(0, maxY));

        _stampDragTarget = new Rect(x, y, _dragBefore.Width, _dragBefore.Height);
        if (selected.Kind == StampKind.Image) UpdateImagePreviewBounds(selected.Page, selected.Index);
        else if (showPreview && !_dragPreviewPending && _stampDragTarget != selected.Local)
        {
            _ = UpdateStampPreviewAsync(selected.Page, selected.Index, selected.Kind, _dragBefore);
        }
    }

    private async Task PrepareImagePreviewAsync(int page, int index, Rect original)
    {
        if (_document is not { } document || _layout is null) return;
        int version = _dragPreviewVersion;
        int rotation = _rotation;
        double zoom = _zoom;
        var drawn = ToDrawnRect(RotationFor(page), original);
        float scale = Math.Min(RenderScale, (float)((TileMath.MaxSingleTilePx - 8) / Math.Max(drawn.Width, drawn.Height)));
        var window = DragPixelWindow(drawn, scale, document.GetPagePixelSize(page, scale, rotation));
        try
        {
            var result = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
            {
                if (document.CaptureImagePreview(page, index, scale, rotation, window) is not { } layers)
                    return ((PageBitmap Background, StampImage Image)?)null;
                try { return (layers.Background, PreviewPixels(layers.Image, layers.Rotation)); }
                catch { layers.Background.Return(); throw; }
            });
            if (result is not { } pixels) return;
            try
            {
                if (version != _dragPreviewVersion || _document != document || _layout == null
                    || rotation != _rotation || zoom != _zoom || !(_draggingStamp || IsResizingStamp)) return;
                var rect = _layout.GetPageRect(page);
                var background = CreateDragBitmap(pixels.Background);
                try
                {
                    var image = CanvasBitmap.CreateFromBytes(Canvas, pixels.Image.Bgra, pixels.Image.Width,
                        pixels.Image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
                    _dragPreview = (page, background, new Rect(rect.X + window.X * zoom / scale,
                        rect.Y + window.Y * zoom / scale, window.Width * zoom / scale, window.Height * zoom / scale), image, default);
                }
                catch { background.Dispose(); throw; }
                UpdateImagePreviewBounds(page, index);
            }
            finally { pixels.Background.Return(); }
        }
        catch (Exception ex) { PDFDesk.Services.ErrorLog.Default.Write(nameof(PrepareImagePreviewAsync), ex); }
    }

    private static StampImage PreviewPixels(StampImage image, int rotation)
    {
        double scale = Math.Min(1, TileMath.MaxSingleTilePx / (double)Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)Math.Round(image.Width * scale));
        int h = Math.Max(1, (int)Math.Round(image.Height * scale));
        int width = rotation % 2 == 0 ? w : h;
        var data = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            var (dx, dy) = rotation switch { 1 => (h - 1 - y, x), 2 => (w - 1 - x, h - 1 - y),
                3 => (y, w - 1 - x), _ => (x, y) };
            int src = ((int)((long)y * image.Height / h) * image.Width + (int)((long)x * image.Width / w)) * 4;
            int dst = (dy * width + dx) * 4;
            byte alpha = image.Bgra[src + 3];
            for (int c = 0; c < 3; c++) data[dst + c] = (byte)((image.Bgra[src + c] * alpha + 127) / 255);
            data[dst + 3] = alpha;
        }
        return new StampImage(data, width, data.Length / 4 / width);
    }

    private void UpdateImagePreviewBounds(int page, int index)
    {
        if (_dragPreview is not { } preview || _layout == null) return;
        var target = HighlightRect(page, _layout.GetPageRect(page),
            new TextRect(_stampDragTarget.X, _stampDragTarget.Y, _stampDragTarget.Width, _stampDragTarget.Height));
        var old = preview.TargetBounds;
        preview.TargetBounds = target;
        _dragPreview = preview;
        _selected = (page, index, _stampDragTarget, StampKind.Image);
        if (old.Width == 0) InvalidateDocumentRegion(preview.SourceBounds);
        else InvalidateDocumentRegion(new Rect(old.X - HandleSizeDip, old.Y - HandleSizeDip,
            old.Width + 2 * HandleSizeDip, old.Height + 2 * HandleSizeDip));
        InvalidateDocumentRegion(new Rect(target.X - HandleSizeDip, target.Y - HandleSizeDip,
            target.Width + 2 * HandleSizeDip, target.Height + 2 * HandleSizeDip));
    }

    private async Task UpdateStampPreviewAsync(int page, int index, StampKind kind, Rect original)
    {
        if (_document is not { } document)
        {
            return;
        }

        int version = _dragPreviewVersion;
        _dragPreviewPending = true;
        try
        {
            Rect target;
            do
            {
                target = _stampDragTarget;
                int rotation = _rotation;
                var transform = RotationFor(page);
                Rect sourceDrawn = ToDrawnRect(transform, original);
                Rect targetDrawn = ToDrawnRect(transform, target);
                // Limit each patch independently so a long drag never needs a full-page raster.
                double longestEdge = Math.Max(Math.Max(sourceDrawn.Width, sourceDrawn.Height), Math.Max(targetDrawn.Width, targetDrawn.Height));
                float scale = Math.Min(RenderScale, (float)((TileMath.MaxSingleTilePx - 8) / longestEdge));
                var pagePixels = document.GetPagePixelSize(page, scale, rotation);
                var sourceWindow = DragPixelWindow(sourceDrawn, scale, pagePixels);
                var targetWindow = DragPixelWindow(targetDrawn, scale, pagePixels);
                var result = await _scheduler.RunAsync(PdfWorkPriority.Interactive, () =>
                    document.RenderAnnotationPreview(page, index, new TextRect(target.X, target.Y, target.Width, target.Height), scale, rotation,
                        sourceWindow, targetWindow));
                if (result is not { } preview)
                {
                    return;
                }

                try
                {
                    if (version != _dragPreviewVersion || !(_draggingStamp || IsResizingStamp) || _document != document
                        || _layout is null || _rotation != rotation)
                    {
                        return;
                    }

                    var pageRect = _layout.GetPageRect(page);
                    Rect Bounds((int X, int Y, int Width, int Height) window) =>
                        new(pageRect.X + window.X * _zoom / scale, pageRect.Y + window.Y * _zoom / scale,
                            window.Width * _zoom / scale, window.Height * _zoom / scale);

                    var source = CreateDragBitmap(preview.Source);
                    CanvasBitmap destination;
                    try
                    {
                        destination = CreateDragBitmap(preview.Target);
                    }
                    catch
                    {
                        source.Dispose();
                        throw;
                    }
                    DisposeDragBitmaps();
                    _dragPreview = (page, source, Bounds(sourceWindow), destination, Bounds(targetWindow));
                    _selected = (page, index, target, kind);
                    Canvas.Invalidate();
                }
                finally
                {
                    preview.Source.Return();
                    preview.Target.Return();
                }
            }
            while (target != _stampDragTarget);
        }
        catch
        {
            // Device loss or a closed tab can interrupt a preview. The drop can still commit.
        }
        finally
        {
            if (version == _dragPreviewVersion)
            {
                _dragPreviewPending = false;
            }
        }
    }

    private static Rect ToDrawnRect(PageRotationTransform transform, Rect rect)
    {
        var drawn = transform.ToDrawn(new TextRect(rect.X, rect.Y, rect.Width, rect.Height));
        return new Rect(drawn.X, drawn.Y, drawn.Width, drawn.Height);
    }

    private static (int X, int Y, int Width, int Height) DragPixelWindow(
        Rect rect, float scale, (int Width, int Height) page)
    {
        const int padding = 2;
        int left = Math.Clamp((int)Math.Floor(rect.Left * scale) - padding, 0, page.Width - 1);
        int top = Math.Clamp((int)Math.Floor(rect.Top * scale) - padding, 0, page.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(rect.Right * scale) + padding, left + 1, page.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom * scale) + padding, top + 1, page.Height);
        return (left, top, right - left, bottom - top);
    }

    private CanvasBitmap CreateDragBitmap(PageBitmap bitmap) => CanvasBitmap.CreateFromBytes(
        Canvas, bitmap.Pixels, bitmap.Width, bitmap.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private void ClearStampDragPreview()
    {
        _dragPreviewVersion++;
        _dragPreviewPending = false;
        DisposeDragBitmaps();
    }

    private void DisposeDragBitmaps()
    {
        _dragPreviewCommitted = false;
        if (_dragPreview is { } preview)
        {
            preview.Source.Dispose();
            preview.Target.Dispose();
            _dragPreview = null;
        }
    }

    private async void CommitStampDrag()
    {
        _draggingStamp = false;

        if (_selected is not { } selected || _document is not { } document)
        {
            return;
        }

        int page = selected.Page;
        int index = selected.Index;
        var target = _stampDragTarget;
        var original = _dragBefore;
        if (target == original)
        {
            ClearStampDragPreview();
            _selected = (page, index, original, selected.Kind);
            Canvas.Invalidate();
            return;
        }

        _stampMovePending = true;
        (float L, float B, float R, float T)? previous = null;
        try
        {
            previous = await _scheduler.RunAsync(
                PdfWorkPriority.Interactive, () => document.MoveAnnotation(page, index, target.X, target.Y));
        }
        catch
        {
            // Restore the frame if the move could not be written.
        }
        finally
        {
            _stampMovePending = false;
        }
        if (_document != document)
        {
            return;
        }
        if (_selected is { } current && current.Page == page && current.Index == index)
        {
            _selected = (page, index, previous.HasValue ? target : original, selected.Kind);
        }
        if (previous is not { } before)
        {
            ClearStampDragPreview();
            Canvas.Invalidate();
            return;
        }

        // Keep the last drag frame on screen until the updated tiles cover it.
        RefreshPageTiles(page);
        _dragPreviewCommitted = _dragPreview.HasValue;
        RetireCommittedDragPreview(page);
        Canvas.Invalidate();
        DocumentEdited?.Invoke(this, EventArgs.Empty);

        // Undo restores the rect the move captured; redo simply replays the
        // move. Neither retains a bitmap — unlike an erase, which has to keep
        // the pixels to rebuild a picture — so SnapshotBytes stays 0.
        AnnotationEdited?.Invoke(this, new AnnotationEditEventArgs
        {
            Label = "move",
            PageIndex = page,
            UndoAction = d => d.RestoreAnnotationRect(page, index, before),
            RedoAction = d => d.MoveAnnotation(page, index, target.X, target.Y),
        });
    }

    /// <summary>Deletes the selected annotation by index so overlapping annotations do not intercept it.</summary>
    internal bool TryDeleteSelectedObject()
    {
        if (_selected is not { } selected || _document is not { } document)
        {
            return false;
        }

        ClearObjectSelection();
        _ = RemoveAnnotationAsync(document, selected.Page, selected.Index, "erase");
        return true;
    }

    // ---- painting ----

    private void RetireCommittedDragPreview(int page)
    {
        if (!_dragPreviewCommitted || _dragPreview is not { } preview || preview.Page != page
            || _layout is null || _document is null || _stalePreviews.Contains(page))
        {
            return;
        }

        var pageRect = _layout.GetPageRect(page);
        float scale = RenderScale;
        int scaleKey = TileKey.ToScaleKey(scale);
        var (pageWidth, pageHeight) = _document.GetPagePixelSize(page, scale, _rotation);
        var source = new DipRect(preview.SourceBounds.X, preview.SourceBounds.Y,
            preview.SourceBounds.Width, preview.SourceBounds.Height);
        var target = new DipRect(preview.TargetBounds.X, preview.TargetBounds.Y,
            preview.TargetBounds.Width, preview.TargetBounds.Height);
        double displayScale = DisplayScale;
        foreach (var key in _staleTiles)
        {
            if (key.PageIndex != page || key.ScaleKey != scaleKey || key.Rotation != _rotation)
            {
                continue;
            }

            var (x, y, width, height) = TileMath.TileWindow(pageWidth, pageHeight, key.Row, key.Col);
            var tile = new DipRect(pageRect.X + x / displayScale, pageRect.Y + y / displayScale,
                width / displayScale, height / displayScale);
            if (tile.Intersects(source) || tile.Intersects(target))
            {
                return;
            }
        }

        ClearStampDragPreview();
        InvalidateDocumentRegion(preview.SourceBounds);
        InvalidateDocumentRegion(preview.TargetBounds);
    }

    private void DrawStampDragPreview(CanvasDrawingSession session, int page)
    {
        if (_dragPreview is { } preview && preview.Page == page)
        {
            DrawPageImage(session, preview.Source, preview.SourceBounds);
            DrawPageImage(session, preview.Target, preview.TargetBounds);
        }
    }

    private Rect StampSelectionBounds(Rect bounds, StampKind kind)
    {
        if (kind != StampKind.Text)
        {
            return bounds;
        }

        // Keep the handles and their outline clear of descenders and other ink.
        double padding = (HandleSizeDip / 2 + 2) / _zoom;
        return new Rect(bounds.X - padding, bounds.Y - padding,
            bounds.Width + 2 * padding, bounds.Height + 2 * padding);
    }

    /// <summary>
    /// Frames the selected object. Drawn after the page so it is never inverted
    /// by night mode along with the tiles.
    /// </summary>
    private void DrawStampSelection(CanvasDrawingSession session)
    {
        if (_selected is not { } selected || _layout is null)
        {
            return;
        }

        var pageRect = _layout.GetPageRect(selected.Page);
        var bounds = StampSelectionBounds(selected.Local, selected.Kind);
        var box = HighlightRect(selected.Page, pageRect, new TextRect(
            bounds.X, bounds.Y, bounds.Width, bounds.Height));

        session.DrawRectangle(box, PDFDeskColors.SelectedStampBorder(_nightMode), 1.5f,
            new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });

        // Corner handles, in DIPs rather than page points so they stay the same
        // size on screen at any zoom. Drawn from the box's own corners, which
        // have already been through the view rotation, so they land on the
        // visible corners rather than the file's.
        var border = PDFDeskColors.SelectedStampBorder(_nightMode);
        var fill = PDFDeskColors.SelectedStampHandle(_nightMode);
        float half = (float)(HandleSizeDip / 2);
        foreach (var corner in new[]
        {
            new Point(box.Left, box.Top),
            new Point(box.Right, box.Top),
            new Point(box.Right, box.Bottom),
            new Point(box.Left, box.Bottom),
        })
        {
            var handle = new Rect(corner.X - half, corner.Y - half, HandleSizeDip, HandleSizeDip);
            session.FillRectangle(handle, fill);
            session.DrawRectangle(handle, border, 1f);
        }
    }
}
