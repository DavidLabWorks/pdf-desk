using PDFDesk.Engine;

namespace PDFDesk.Tests;

public class AnnotationTests
{
    private static string CorpusPath(string name) => PixelAssert.CorpusPath(name);

    private static int CountNonWhite(PageBitmap bmp) => PixelAssert.CountNonWhite(bmp);

    private static int CountTinted(PageBitmap bmp) => PixelAssert.CountTinted(bmp);

    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"pdfdesk-annot-{Guid.NewGuid():N}.pdf");

    [Fact]
    public void AddHighlight_SaveAs_Reopen_AnnotationPersists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                Assert.False(doc.IsDirty);
                // Rects over the "Hello from PDFDesk!" line (top-left page points).
                doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(70, 50, 240, 30)], 255, 210, 0, 102);
                Assert.True(doc.IsDirty);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var annots = reopened.GetAnnotations(0);
            var highlight = Assert.Single(annots);
            Assert.Equal((int)MarkupKind.Highlight, highlight.Subtype);
            // Rect round-trips to roughly where we put it (top-left origin).
            Assert.InRange(highlight.X, 60, 80);
            Assert.InRange(highlight.Y, 40, 60);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddNote_ContentsRoundTrip()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddNote(0, 300, 400, "Remember to cite this passage.");
                // In-process read-back isolates set-failure from save-loss.
                Assert.Equal("Remember to cite this passage.", doc.GetAnnotations(0)[0].Contents);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var note = Assert.Single(reopened.GetAnnotations(0));
            Assert.True(note.IsNote);
            Assert.Equal("Remember to cite this passage.", note.Contents);
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void RemoveAnnotation_Works()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddMarkup(0, MarkupKind.Underline, [new TextRect(70, 50, 100, 20)], 200, 0, 0, 255);
                doc.AddNote(0, 300, 300, "note");
                doc.SaveAs(saved);
            }

            string resaved = TempPdf();
            try
            {
                using (var doc = PdfDocument.Open(saved))
                {
                    Assert.Equal(2, doc.GetAnnotations(0).Count);
                    Assert.True(doc.RemoveAnnotation(0, 0));
                    Assert.Single(doc.GetAnnotations(0));
                    doc.SaveAs(resaved);
                }

                using var final = PdfDocument.Open(resaved);
                Assert.Single(final.GetAnnotations(0));
            }
            finally
            {
                File.Delete(resaved);
            }
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddInk_SaveAs_Reopen_InkAnnotationPersists()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                // A short diagonal stroke in top-left page points.
                var stroke = new (double X, double Y)[] { (100, 100), (150, 130), (200, 110), (260, 160) };
                doc.AddInk(0, stroke, 226, 34, 34, 255, 2.5f);
                Assert.True(doc.IsDirty);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var ink = Assert.Single(reopened.GetAnnotations(0));
            Assert.Equal(15, ink.Subtype); // FPDF_ANNOT_SUBTYPE_INK
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void AddInk_ShortStroke_IsIgnored()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        doc.AddInk(0, [(10.0, 10.0)], 0, 0, 0, 255, 2f); // single point → no annotation
        Assert.False(doc.IsDirty);
        Assert.Empty(doc.GetAnnotations(0));
    }

    [Fact]
    public void AddInk_ChangesRenderedPixels()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var before = doc.RenderPage(0, 1.0f);
        int beforeInk = CountNonWhite(before);

        var stroke = new (double X, double Y)[] { (60, 400), (200, 405), (340, 402), (480, 408) };
        doc.AddInk(0, stroke, 226, 34, 34, 255, 4f);
        var after = doc.RenderPage(0, 1.0f);
        int afterInk = CountNonWhite(after);

        Assert.True(afterInk > beforeInk + 500, $"expected ink pixels: before={beforeInk}, after={afterInk}");
    }

    [Fact]
    public void CommittedInkRetainsFractionalSamplesAndRoundEndCaps()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        doc.AddInk(0, [(100.25, 400.25), (130.75, 400.25)], 226, 34, 34, 255, 10);
        var spec = Assert.IsType<AnnotationSpec>(doc.CaptureLastAnnotation(0));
        Assert.Equal(30.5f, spec.InkStrokes[0][1].X - spec.InkStrokes[0][0].X, 3);
        var rendered = doc.RenderRegion(0, 4, 0, 380, 1580, 168, 48);
        try
        {
            Assert.True(IsInk(rendered, 4, 21));   // the cap extends beyond its centre
            Assert.False(IsInk(rendered, 4, 4));   // outside the circular cap
            Assert.True(IsInk(rendered, 156, 21));
        }
        finally { rendered.Return(); }
    }

    [Fact]
    public void EraseInk_CutsSparseStrokeAtPageEdgeAndSurvivesSave()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddInk(0, [(0, 310), (0, 500)], 226, 34, 34, 255, 8);
                doc.AddNote(0, 80, 400, "Keep this note");
                Assert.True(IsInk(doc.RenderPage(0, 1), 1, 400));

                // Both pointer samples are outside the painted line. The
                // swept segment still crosses it, even just beyond the edge.
                var changes = doc.EraseInk(new Dictionary<int, (double, double)[]> { [0] = [(-6, 380), (-6, 420)] }, 18);
                var change = Assert.Single(changes);
                Assert.Equal(2, change.After.Count);
                Assert.Contains(doc.GetAnnotations(0), annot => annot.IsNote);
                var edited = doc.RenderPage(0, 1);
                Assert.True(IsInk(edited, 1, 340));
                Assert.False(IsInk(edited, 1, 400));
                Assert.True(IsInk(edited, 1, 460));

                doc.ApplyInkEraseChanges(changes, restore: true);
                Assert.True(IsInk(doc.RenderPage(0, 1), 1, 400));
                doc.ApplyInkEraseChanges(changes, restore: false);
                doc.SaveAs(saved);
            }
            using var reopened = PdfDocument.Open(saved);
            Assert.False(IsInk(reopened.RenderPage(0, 1), 1, 400));
        }
        finally
        {
            File.Delete(saved);
        }
    }

    [Fact]
    public void EraseInk_UsesSelectedWidthAndCanClearAnEntireShortStroke()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        doc.AddInk(0, [(100, 400), (110, 400)], 226, 34, 34, 255, 4);
        var trail = new Dictionary<int, (double, double)[]> { [0] = [(105, 410)] };
        Assert.True(IsInk(doc.RenderPage(0, 1), 105, 400));
        Assert.Empty(doc.EraseInk(trail, width: 4));
        var changes = doc.EraseInk(trail, width: 30);
        Assert.Empty(Assert.Single(changes).After);
        Assert.False(IsInk(doc.RenderPage(0, 1), 105, 400));
        doc.ApplyInkEraseChanges(changes, restore: true);
        Assert.True(IsInk(doc.RenderPage(0, 1), 105, 400));
    }

    [Fact]
    public void SmallEraserCutsAHoleInWideInkAtFourTimesZoom()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(CorpusPath("hello.pdf")))
            {
                doc.AddInk(0, [(100, 400), (180, 400)], 226, 34, 34, 180, 24);
                var trail = new Dictionary<int, (double, double)[]> { [0] = [(140, 400)] };
                var changes = doc.EraseInk(trail, width: 4);
                Assert.Single(changes);
                Assert.Empty(doc.EraseInk(trail, width: 4));
                CheckPixels(doc, erased: true);
                doc.ApplyInkEraseChanges(changes, restore: true);
                CheckPixels(doc, erased: false);
                doc.ApplyInkEraseChanges(changes, restore: false);
                doc.SaveAs(saved);
            }
            using var reopened = PdfDocument.Open(saved);
            CheckPixels(reopened, erased: true);
            var edge = new Dictionary<int, (double, double)[]> { [0] = [(140, 408)] };
            Assert.Single(reopened.EraseInk(edge, width: 4));
            var bitmap = reopened.RenderRegion(0, 4, 0, 130 * 4, 390 * 4, 80, 80);
            try
            {
                Assert.False(IsInk(bitmap, 40, 40));
                Assert.False(IsInk(bitmap, 40, 72));
                Assert.True(IsInk(bitmap, 40, 56));
            }
            finally { bitmap.Return(); }
        }
        finally { File.Delete(saved); }

        static void CheckPixels(PdfDocument doc, bool erased)
        {
            var bitmap = doc.RenderRegion(0, 4, 0, 130 * 4, 390 * 4, 80, 80);
            try
            {
                Assert.Equal(!erased, IsInk(bitmap, 40, 40));
                Assert.True(IsInk(bitmap, 40, 56));
                Assert.True(IsInk(bitmap, 40, 24));
                Assert.True(IsInk(bitmap, 56, 40));
                Assert.InRange(bitmap.Pixels[56 * bitmap.Stride + 40 * 4 + 1], (byte)85, (byte)115);
            }
            finally { bitmap.Return(); }
        }
    }

    private static bool IsInk(PageBitmap bitmap, int x, int y)
    {
        int pixel = y * bitmap.Stride + x * 4;
        return bitmap.Pixels[pixel + 2] > bitmap.Pixels[pixel + 1] + 40;
    }

    [Fact]
    public void Highlight_ActuallyChangesRenderedPixels()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));
        var before = doc.RenderPage(0, 1.0f);
        int beforeYellowish = CountTinted(before);

        doc.AddMarkup(0, MarkupKind.Highlight, [new TextRect(70, 50, 240, 30)], 255, 210, 0, 102);
        var after = doc.RenderPage(0, 1.0f);
        int afterYellowish = CountTinted(after);

        Assert.True(afterYellowish > beforeYellowish + 1000,
            $"expected highlight tint in render: before={beforeYellowish}, after={afterYellowish}");
    }

}
