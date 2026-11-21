using Clipper2Lib;
using PDFDesk.PdfiumInterop;

namespace PDFDesk.Engine;

public enum MarkupKind
{
    Highlight = PdfiumNative.AnnotHighlight,
    Underline = PdfiumNative.AnnotUnderline,
    Strikeout = PdfiumNative.AnnotStrikeout,
}

/// <summary>An annotation summary for hit-testing and listing. Rect is in page points, top-left origin.</summary>
public sealed record AnnotationInfo(int Index, int Subtype, double X, double Y, double Width, double Height, string Contents)
{
    public bool IsNote => Subtype == PdfiumNative.AnnotText;
}

/// <summary>
/// The pixels behind a stamp annotation, straight (non-premultiplied) BGRA.
/// Held so undo can re-create the stamp: reading an image back out of a live
/// annotation would need FPDFImageObj_GetBitmap, which isn't bound.
/// </summary>
public sealed record StampImage(byte[] Bgra, int Width, int Height)
{
    /// <summary>Retained bytes, for the undo stack's memory cap.</summary>
    public long ByteCount => Bgra.LongLength;
}

/// <summary>
/// Everything needed to faithfully re-create one of PDFDesk's annotation
/// subtypes (markup/ink/note/stamp) — captured before a deletion so undo can
/// rebuild it. All geometry is in PDF page space (bottom-left origin).
/// </summary>
/// <param name="Stamp">
/// Set only for stamps. Trailing and defaulted so every existing call site is
/// unaffected.
/// </param>
public sealed record AnnotationSpec(
    int PageIndex,
    int Subtype,
    IReadOnlyList<(float L, float B, float R, float T)> Quads,
    IReadOnlyList<IReadOnlyList<(float X, float Y)>> InkStrokes,
    (float L, float B, float R, float T) Rect,
    (byte R, byte G, byte B, byte A) Color,
    float BorderWidth,
    string Contents,
    StampImage? Stamp = null,
    TextBoxContent? Text = null,
    InkFootprint? Footprint = null);

public sealed partial class PdfDocument
{
    /// <summary>True when annotations were added/removed since open or last save.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>
    /// Adds a text-markup annotation (highlight/underline/strikeout) covering
    /// the given rects, which are in page points with a TOP-LEFT origin (the
    /// same space <see cref="TextSelection"/> rects use).
    /// </summary>
    public void AddMarkup(int pageIndex, MarkupKind kind, IReadOnlyList<TextRect> rects, byte r, byte g, byte b, byte a)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        if (rects.Count == 0)
        {
            return;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, (int)kind);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the annotation.", 1);
                }

                try
                {
                    // Convert every top-left rect to PDF page space (bottom-left
                    // origin) via DeviceToPage so pages with /Rotate map correctly.
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                    int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                    float minL = float.MaxValue, minB = float.MaxValue, maxR = float.MinValue, maxT = float.MinValue;
                    foreach (var rect in rects)
                    {
                        var (x1, y1) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(rect.X), (int)Math.Round(rect.Y));
                        var (x2, y2) = PdfiumNative.DeviceToPage(page, sizeX, sizeY, (int)Math.Round(rect.X + rect.Width), (int)Math.Round(rect.Y + rect.Height));
                        float left = (float)Math.Min(x1, x2);
                        float right = (float)Math.Max(x1, x2);
                        float bottom = (float)Math.Min(y1, y2);
                        float top = (float)Math.Max(y1, y2);

                        PdfiumNative.AppendQuad(annot, left, bottom, right, top);
                        minL = Math.Min(minL, left);
                        minB = Math.Min(minB, bottom);
                        maxR = Math.Max(maxR, right);
                        maxT = Math.Max(maxT, top);
                    }

                    PdfiumNative.SetAnnotRect(annot, minL, minB, maxR, maxT);
                    PdfiumNative.SetAnnotColor(annot, r, g, b, a);
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        IsDirty = true;
    }

    /// <summary>
    /// Adds a freehand ink annotation. <paramref name="stroke"/> is a polyline
    /// in page points with a top-left origin (canvas space). One annotation per
    /// stroke, so each is individually deletable. <paramref name="width"/> is in
    /// points.
    /// </summary>
    public void AddInk(int pageIndex, IReadOnlyList<(double X, double Y)> stroke, byte r, byte g, byte b, byte a, float width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);
        if (stroke.Count < 2)
        {
            return;
        }

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotInk);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the ink annotation.", 1);
                }

                try
                {
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                    int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                    var origin = PdfiumNative.DeviceToPage(page, sizeX, sizeY, 0, 0);
                    var right = PdfiumNative.DeviceToPage(page, sizeX, sizeY, sizeX, 0);
                    var down = PdfiumNative.DeviceToPage(page, sizeX, sizeY, 0, sizeY);
                    var points = new (float X, float Y)[stroke.Count];
                    float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    for (int i = 0; i < stroke.Count; i++)
                    {
                        double x = stroke[i].X / ptW, y = stroke[i].Y / ptH;
                        double px = origin.X + (right.X - origin.X) * x + (down.X - origin.X) * y;
                        double py = origin.Y + (right.Y - origin.Y) * x + (down.Y - origin.Y) * y;
                        points[i] = ((float)px, (float)py);
                        minX = Math.Min(minX, (float)px);
                        minY = Math.Min(minY, (float)py);
                        maxX = Math.Max(maxX, (float)px);
                        maxY = Math.Max(maxY, (float)py);
                    }

                    float pad = width / 2 + 1;
                    PdfiumNative.SetAnnotRect(annot, minX - pad, minY - pad, maxX + pad, maxY + pad);
                    PdfiumNative.SetAnnotColor(annot, r, g, b, a);
                    PdfiumNative.SetAnnotBorderWidth(annot, width);
                    // Match the live pen's round caps and joins, including tight reversals.
                    var path = new PathD(points.Select(p => new PointD(p.X, p.Y)));
                    var outline = InkFootprint.Sweep(path, width / 2);
                    var appearance = new InkFootprint(InkFootprint.Coordinates(outline), r, g, b, a);
                    if (!PdfiumNative.AddInkStroke(annot, points) || !InkFootprint.Write(annot, appearance))
                    {
                        PdfiumNative.RemoveAnnot(page, PdfiumNative.GetAnnotCount(page) - 1);
                        throw new PdfiumException("Could not write the ink stroke.", 1);
                    }
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        IsDirty = true;
    }

    /// <summary>Adds a sticky-note annotation at (x, y) in page points, top-left origin.</summary>
    public void AddNote(int pageIndex, double x, double y, string text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }

            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, PdfiumNative.AnnotText);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not create the note.", 1);
                }

                try
                {
                    var (ptW, ptH) = _pageSizes[pageIndex];
                    var (px, py) = PdfiumNative.DeviceToPage(
                        page,
                        Math.Max(1, (int)MathF.Round(ptW)), Math.Max(1, (int)MathF.Round(ptH)),
                        (int)Math.Round(x), (int)Math.Round(y));

                    // Standard note icon size ~20pt, anchored at the click point.
                    const float size = 20f;
                    PdfiumNative.SetAnnotRect(annot, (float)px, (float)py - size, (float)px + size, (float)py);
                    PdfiumNative.SetAnnotString(annot, "Contents", text);
                    PdfiumNative.SetAnnotColor(annot, 255, 219, 117, 255);
                    WriteNoteIcon(annot, (float)px, (float)py - size, size, size);
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        IsDirty = true;
    }

    // PDF appearance in page space: a warm paper square with a folded corner.
    private static void WriteNoteIcon(IntPtr annot, float x, float y, float width, float height)
    {
        if (width <= 0 || height <= 0) return;
        string graphic = FormattableString.Invariant($"q {width / 20:0.#####} 0 0 {height / 20:0.#####} {x:0.###} {y:0.###} cm ") +
            "1 0 0 -1 0 20 cm 1 J 1 j 1.5 w .99 .90 .58 rg .42 .32 .12 RG " +
            "5 1.8 m 16 1.8 l 17.5 1.8 18.2 2.5 18.2 4 c 18.2 11.8 l 11.8 18.2 l 5 18.2 l " +
            "3 18.2 1.8 17 1.8 15 c 1.8 5 l 1.8 3 3 1.8 5 1.8 c h B " +
            "11.8 18.2 m 11.8 14 l 11.8 12.5 12.5 11.8 14 11.8 c 18.2 11.8 l S " +
            "5.5 6.8 m 13.5 6.8 l S 5.5 10.2 m 9.5 10.2 l S Q";
        PdfiumNative.SetAnnotAppearance(annot, graphic);
    }

    /// <summary>All annotations on a page, rects in page points with top-left origin.</summary>
    public IReadOnlyList<AnnotationInfo> GetAnnotations(int pageIndex) => ReadAnnotations(pageIndex, null);

    public IReadOnlyList<AnnotationInfo> GetNotes(int pageIndex) => ReadAnnotations(pageIndex, PdfiumNative.AnnotText);

    private IReadOnlyList<AnnotationInfo> ReadAnnotations(int pageIndex, int? subtypeFilter)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        var result = new List<AnnotationInfo>();
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                var (ptW, ptH) = _pageSizes[pageIndex];
                int sizeX = Math.Max(1, (int)MathF.Round(ptW));
                int sizeY = Math.Max(1, (int)MathF.Round(ptH));

                int count = PdfiumNative.GetAnnotCount(page);
                for (int i = 0; i < count; i++)
                {
                    IntPtr annot = PdfiumNative.GetAnnot(page, i);
                    if (annot == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        int subtype = PdfiumNative.GetAnnotSubtype(annot);
                        if (subtypeFilter.HasValue && subtype != subtypeFilter.Value) continue;
                        if (subtype == PdfiumNative.AnnotInk && PdfiumNative.GetInkStrokeCount(annot) == 0)
                        {
                            continue;
                        }
                        if (!PdfiumNative.GetAnnotRect(annot, out float l, out float t, out float rr, out float bb))
                        {
                            continue;
                        }
                        // Page rect (bottom-left origin) → top-left points via PageToDevice.
                        var (dx1, dy1) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, l, t);
                        var (dx2, dy2) = PdfiumNative.PageToDevice(page, sizeX, sizeY, 0, rr, bb);
                        double x = Math.Min(dx1, dx2), y = Math.Min(dy1, dy2);
                        result.Add(new AnnotationInfo(
                            i, subtype,
                            x, y, Math.Abs(dx2 - dx1), Math.Abs(dy2 - dy1),
                            PdfiumNative.GetAnnotString(annot, "Contents")));
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
        return result;
    }

    public bool RemoveAnnotation(int pageIndex, int annotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        bool removed;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                removed = PdfiumNative.RemoveAnnot(page, annotIndex);
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        IsDirty |= removed;
        return removed;
    }

    /// <summary>Removes the newest annotation on a page (undo of an add — ours are always appended).</summary>
    public bool RemoveLastAnnotation(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        bool removed = false;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                int count = PdfiumNative.GetAnnotCount(page);
                if (count > 0)
                {
                    removed = PdfiumNative.RemoveAnnot(page, count - 1);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        IsDirty |= removed;
        return removed;
    }

    /// <summary>
    /// Reads everything needed to re-create one of OUR annotation subtypes
    /// (markup/ink/note/stamp) so a deletion can be undone. Returns null for
    /// subtypes we can't faithfully rebuild. All geometry is in page space
    /// (bottom-left origin).
    /// </summary>
    /// <remarks>
    /// A stamp is captured by reading its contents back out of the file, which
    /// costs a second pass over the annotation and is why it is separated out
    /// below. Until v0.7.0 this returned null for stamps, so erasing a signature
    /// put nothing on the undo stack at all and Ctrl+Z did nothing.
    ///
    /// Nesting <see cref="TryReadStampImage"/> and <see cref="TryReadTextBox"/>
    /// inside the lock this already holds is safe: it is a monitor, so it
    /// re-enters on the same thread, and page leases are counted rather than
    /// boolean.
    /// </remarks>
    public AnnotationSpec? CaptureAnnotation(int pageIndex, int annotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                IntPtr annot = PdfiumNative.GetAnnot(page, annotIndex);
                if (annot == IntPtr.Zero)
                {
                    return null;
                }
                try
                {
                    int subtype = PdfiumNative.GetAnnotSubtype(annot);
                    bool isMarkup = subtype is PdfiumNative.AnnotHighlight
                        or PdfiumNative.AnnotUnderline or PdfiumNative.AnnotStrikeout;
                    if (!isMarkup && subtype != PdfiumNative.AnnotInk
                        && subtype != PdfiumNative.AnnotText && subtype != PdfiumNative.AnnotStamp)
                    {
                        return null;
                    }

                    PdfiumNative.GetAnnotRect(annot, out float l, out float t, out float r, out float b);
                    PdfiumNative.GetAnnotColor(annot, out byte cr, out byte cg, out byte cb, out byte ca);

                    var quads = new List<(float L, float B, float R, float T)>();
                    if (isMarkup)
                    {
                        int quadCount = PdfiumNative.CountAnnotQuads(annot);
                        for (int i = 0; i < quadCount; i++)
                        {
                            if (PdfiumNative.GetAnnotQuad(annot, i, out float ql, out float qb, out float qr, out float qt))
                            {
                                quads.Add((ql, qb, qr, qt));
                            }
                        }
                    }

                    var strokes = new List<IReadOnlyList<(float X, float Y)>>();
                    if (subtype == PdfiumNative.AnnotInk)
                    {
                        int strokeCount = PdfiumNative.GetInkStrokeCount(annot);
                        for (int i = 0; i < strokeCount; i++)
                        {
                            var points = PdfiumNative.GetInkStroke(annot, i);
                            if (points.Length > 0)
                            {
                                strokes.Add(points);
                            }
                        }
                    }

                    // A stamp holds neither quads nor strokes: what rebuilds it
                    // is its pixels or its words, and which of the two it is
                    // has to be read back out of the annotation itself.
                    StampImage? stamp = null;
                    TextBoxContent? text = null;
                    if (subtype == PdfiumNative.AnnotStamp)
                    {
                        text = TryReadTextBox(pageIndex, annotIndex);
                        if (text is null)
                        {
                            stamp = TryReadStampImage(pageIndex, annotIndex);
                            if (stamp is null)
                            {
                                return null; // a stamp PDFDesk cannot faithfully rebuild
                            }
                        }
                    }

                    var footprint = subtype == PdfiumNative.AnnotInk ? InkFootprint.Read(annot) : null;
                    if (footprint != null) (cr, cg, cb, ca) = (footprint.R, footprint.G, footprint.B, footprint.A);
                    return new AnnotationSpec(
                        pageIndex, subtype, quads, strokes,
                        (Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b)),
                        (cr, cg, cb, ca == 0 ? (byte)255 : ca),
                        PdfiumNative.GetAnnotBorderWidth(annot),
                        PdfiumNative.GetAnnotString(annot, "Contents"),
                        stamp,
                        text, footprint);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
    }

    /// <summary>Captures the newest annotation on a page (undo of an add — ours are always appended). Null if none/exotic.</summary>
    public AnnotationSpec? CaptureLastAnnotation(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, PageCount);

        int lastIndex;
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(pageIndex);
            if (page == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                lastIndex = PdfiumNative.GetAnnotCount(page) - 1;
            }
            finally
            {
                ReleasePageLocked(pageIndex);
            }
        }
        return lastIndex < 0 ? null : CaptureAnnotation(pageIndex, lastIndex);
    }

    /// <summary>Re-creates an annotation from a captured spec (appended — becomes the page's last annotation).</summary>
    public void AddAnnotationFromSpec(AnnotationSpec spec)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(spec.PageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(spec.PageIndex, PageCount);

        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            IntPtr page = AcquirePageLocked(spec.PageIndex);
            if (page == IntPtr.Zero)
            {
                throw PdfiumNative.LastError();
            }
            try
            {
                IntPtr annot = PdfiumNative.CreateAnnot(page, spec.Subtype);
                if (annot == IntPtr.Zero)
                {
                    throw new PdfiumException("Could not re-create the annotation.", 1);
                }
                try
                {
                    foreach (var (ql, qb, qr, qt) in spec.Quads)
                    {
                        PdfiumNative.AppendQuad(annot, ql, qb, qr, qt);
                    }
                    foreach (var stroke in spec.InkStrokes)
                    {
                        PdfiumNative.AddInkStroke(annot, [.. stroke]);
                    }
                    var (rl, rb, rr, rt) = spec.Rect;
                    PdfiumNative.SetAnnotRect(annot, rl, rb, rr, rt);

                    // Stamps carry pixels rather than geometry; re-attach the
                    // image and scale it back into the captured rect.
                    if (spec.Stamp is { } stamp)
                    {
                        AttachStampImageLocked(page, annot, stamp, rl, rb, rr, rt);
                    }

                    // Text carries its own content rather than pixels, so it is
                    // rebuilt from the words and re-measured into the captured
                    // rect's top-left. Re-rendering rather than replaying a
                    // raster is the whole reason it is stored as text.
                    if (spec.Text is { } text)
                    {
                        AttachTextLocked(annot, text, rl, rt);
                        // Underline, alignment and box width live only in PDFDesk's
                        // own key, so an undo that skipped it would put the words
                        // back with their formatting quietly dropped.
                        WriteTextStyleLocked(annot, text);
                    }

                    PdfiumNative.SetAnnotColor(annot, spec.Color.R, spec.Color.G, spec.Color.B, spec.Color.A);
                    if (spec.BorderWidth > 0)
                    {
                        PdfiumNative.SetAnnotBorderWidth(annot, spec.BorderWidth);
                    }
                    if (spec.Footprint is { } footprint && !InkFootprint.Write(annot, footprint))
                        throw new PdfiumException("Could not restore the erased ink outline.", 1);
                    if (spec.Subtype == PdfiumNative.AnnotText) WriteNoteIcon(annot, rl, rb, rr - rl, rt - rb);
                    if (!string.IsNullOrEmpty(spec.Contents))
                    {
                        PdfiumNative.SetAnnotString(annot, "Contents", spec.Contents);
                    }
                    PdfiumNative.SetAnnotPrintFlag(annot);
                }
                finally
                {
                    PdfiumNative.CloseAnnot(annot);
                }
            }
            finally
            {
                ReleasePageLocked(spec.PageIndex);
            }
        }
        IsDirty = true;
    }

    /// <summary>
    /// Writes a full copy of the document (with all annotation edits) to
    /// <paramref name="path"/>.
    /// </summary>
    public void SaveAs(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            FormKillFocus();
            WritePdfLocked(stream);
        }
    }

    // The live document and its undo history survive any edit or write failure.
    internal PdfDocument SaveEditedCopy(Action<PdfDocument> edit)
    {
        string snapshot = Path.Combine(Path.GetTempPath(), $"pdfdesk-{Guid.NewGuid():N}.pdf");
        PdfDocument? copy = null;
        try
        {
            SaveAs(snapshot);
            copy = Open(snapshot);
            edit(copy);
            copy.FilePath = FilePath;
            copy._savedDiskVersion = _savedDiskVersion;
            copy.SaveInPlace();
            return copy;
        }
        catch
        {
            copy?.Dispose();
            throw;
        }
        finally { TryDeleteSaveFile(snapshot); }
    }

    /// <summary>Saves edits without closing the document used by the viewer.</summary>
    public void SaveInPlace()
    {
        lock (PdfiumLibrary.Lock)
        {
            ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
            FormKillFocus();
            if (!IsDirty)
            {
                return;
            }

            string stagingPath = Path.Combine(Path.GetDirectoryName(FilePath)!, $".pdfdesk-{Guid.NewGuid():N}.saving");
            string backupPath = stagingPath + ".bak";
            try
            {
                using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    WritePdfLocked(output);
                    output.Flush(flushToDisk: true);
                }

                var replacement = ReadFileVersion(stagingPath);
                if (ReadFileVersion(FilePath) != _savedDiskVersion)
                {
                    throw new IOException("The PDF changed on disk. Save a copy to keep your edits.");
                }

                try
                {
                    File.Replace(stagingPath, FilePath, backupPath);
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && File.Exists(backupPath))
                {
                    // A failed replacement can leave the original under its backup name.
                    if (!File.Exists(FilePath))
                    {
                        try
                        {
                            File.Move(backupPath, FilePath);
                        }
                        catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                        {
                            throw new IOException($"The original PDF is available at {backupPath}.", restoreError);
                        }
                    }
                    throw;
                }

                _savedDiskVersion = replacement;
                MarkSaved();
                TryDeleteSaveFile(backupPath);
            }
            finally
            {
                TryDeleteSaveFile(stagingPath);
            }
        }
    }

    private void WritePdfLocked(Stream stream)
    {
        ReleaseAllPagesLocked();
        if (!PdfiumNative.SaveCopy(_handle, stream))
        {
            throw new PdfiumException("Saving the PDF failed.", 1);
        }
    }

    private static (long Length, DateTime LastWriteUtc) ReadFileVersion(string path)
    {
        var file = new FileInfo(path);
        return (file.Length, file.LastWriteTimeUtc);
    }

    private static void TryDeleteSaveFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.ErrorLog.Default.Write(nameof(SaveInPlace), ex);
        }
    }

    /// <summary>Marks the document clean after a successful save-in-place swap.</summary>
    public void MarkSaved() => IsDirty = false;
}
