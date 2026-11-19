using PDFDesk.Engine;

namespace PDFDesk.Tests;

/// <summary>
/// The probe that decides how "type anywhere" is stored.
///
/// Two routes, measured rather than reasoned about: text hung on a stamp
/// annotation, and text inserted into the page's own content. For each, three
/// questions — does it render, does it survive a save and reopen, and is the
/// text reachable by search afterwards. The third is the one that decides
/// whether real text buys anything over a picture of text.
///
/// Assertions are on pixels and on extracted text, never on the object model:
/// the /DA rewrite and the stamp resize before it both read back perfectly
/// while drawing something else.
/// </summary>
public class TextObjectProbeTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"pdfdesk-text-{Guid.NewGuid():N}.pdf");

    private static TextBoxContent Sample(string text = "Hamburgefonstiv") =>
        new(text, PdfStandardFont.Helvetica, 24, 0, 0, 0);

    [Fact]
    public void StandardFontNames_MatchThePostScriptSpelling()
    {
        // Times is the trap: its regular face is "Times-Roman" and its slanted
        // face is "Italic", where Helvetica and Courier both say "Oblique".
        Assert.Equal("Helvetica",
            new TextBoxContent("x", PdfStandardFont.Helvetica, 12, 0, 0, 0).PostScriptName);
        Assert.Equal("Helvetica-BoldOblique",
            new TextBoxContent("x", PdfStandardFont.Helvetica, 12, 0, 0, 0, Bold: true, Italic: true).PostScriptName);
        Assert.Equal("Times-Roman",
            new TextBoxContent("x", PdfStandardFont.Times, 12, 0, 0, 0).PostScriptName);
        Assert.Equal("Times-Italic",
            new TextBoxContent("x", PdfStandardFont.Times, 12, 0, 0, 0, Italic: true).PostScriptName);
        Assert.Equal("Courier-Bold",
            new TextBoxContent("x", PdfStandardFont.Courier, 12, 0, 0, 0, Bold: true).PostScriptName);
    }

    [Fact]
    public void EveryStandardFontLoads()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // A name PDFium does not know throws rather than silently substituting,
        // which is what would otherwise ship text in the wrong face.
        foreach (var family in new[] { PdfStandardFont.Helvetica, PdfStandardFont.Times, PdfStandardFont.Courier })
        {
            foreach (bool bold in new[] { false, true })
            {
                foreach (bool italic in new[] { false, true })
                {
                    var content = new TextBoxContent("A", family, 12, 0, 0, 0, bold, italic);
                    Assert.NotNull(doc.AddTextToPageContent(0, 40, 40, content));
                }
            }
        }
    }

    [Theory]
    [InlineData(PdfStandardFont.MicrosoftYaHei, "msyh.ttc")]
    [InlineData(PdfStandardFont.SimSun, "simsun.ttc")]
    [InlineData(PdfStandardFont.DengXian, "Deng.ttf")]
    public void LocalTextBoxFontRoundTrips(PdfStandardFont family, string file)
    {
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file))) return;
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.pdf");
        try
        {
            var style = new TextBoxContent("中文 PDF 123", family, 16, 0, 0, 0, false, false);
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                Assert.NotNull(doc.AddTextBox(0, 60, 300, style));
                doc.SaveAs(path);
            }
            using var reopened = PdfDocument.Open(path);
            Assert.Equal(style, reopened.TryReadTextBox(0, 0));
        }
        finally { File.Delete(path); }
    }

    // ---- route A: text on a stamp annotation ----

    [Fact]
    public void Annotation_RendersOnThePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        int before = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.NotNull(doc.AddTextBox(0, 60, 300, Sample()));
        int after = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        Assert.True(after > before,
            $"nothing was drawn: {before} dark pixels before, {after} after");
    }

    [Fact]
    public void Annotation_SurvivesSaveAndReopen()
    {
        string saved = TempPdf();
        try
        {
            int expected;
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 300, Sample());
                expected = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            int actual = PixelAssert.CountDark(reopened.RenderPage(0, 1.0f));

            // Same glyphs, same rasteriser: this should be equal, not merely close.
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void Annotation_IsNotSearchable_UntilItIsFlattened()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 300, Sample("Zarquon"));
                doc.SaveAs(saved);
            }

            using (var reopened = PdfDocument.Open(saved))
            {
                // The measured answer, pinned so it cannot change unnoticed: an
                // annotation's appearance stream is not part of the page's text,
                // so while the text is still an editable object it is invisible
                // to search. This is the cost of keeping it grabbable.
                Assert.DoesNotContain("Zarquon", reopened.GetPageText(0).Text);
            }

            // Flatten merges the appearance into the page's content. The glyphs
            // are a real text object drawn in a real font, so what lands there
            // is real text rather than a picture of it.
            string flattened = TempPdf();
            try
            {
                using (var doc = PdfDocument.Open(saved))
                {
                    Assert.Equal(FlattenResult.Flattened, doc.FlattenPage(0));
                    doc.SaveAs(flattened);
                }

                using var final = PdfDocument.Open(flattened);
                Assert.Contains("Zarquon", final.GetPageText(0).Text);
            }
            finally
            {
                if (File.Exists(flattened)) { File.Delete(flattened); }
            }
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void ChineseTextBox_RendersAfterReopenAndSurvivesFlattening()
    {
        var content = Sample("中文测试：你好，世界！ PDF 123 yyyyy") with { Italic = true, Underline = true };
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!OperatingSystem.IsWindows() ||
            !new[] { "msyh.ttc", "msjh.ttc", "Deng.ttf", "simsun.ttc" }.Any(file => File.Exists(Path.Combine(fonts, file))))
        {
            // CJK supplemental fonts are optional on Windows.
            return;
        }

        string saved = TempPdf();
        try
        {
            int withText;
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                int before = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
                Assert.NotNull(doc.AddTextBox(0, 60, 300, content));
                withText = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
                Assert.True(withText > before);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            Assert.Equal(content, reopened.TryReadTextBox(0, 0));
            Assert.Equal(withText, PixelAssert.CountDark(reopened.RenderPage(0, 1.0f)));
            using (var flattened = PdfDocument.Open(saved))
            {
                Assert.Equal(FlattenResult.Flattened, flattened.FlattenPage(0));
                Assert.Contains("PDF 123 yyyyy", flattened.GetPageText(0).Text);
            }
            var original = reopened.GetAnnotations(0)[0];
            var resized = reopened.ResizeTextBox(0, 0, original.X, original.Y, 32);
            Assert.NotNull(resized);
            var reflowed = reopened.GetAnnotations(0)[resized.Value.NewIndex];
            Assert.True(reflowed.Height > original.Height);
            Assert.Equal(content with { WidthPt = 32 }, reopened.TryReadTextBox(0, resized.Value.NewIndex));
            Assert.Equal(FlattenResult.Flattened, reopened.FlattenPage(0));
            Assert.Contains(
                string.Concat(content.Text.Where(c => !char.IsWhiteSpace(c))),
                string.Concat(reopened.GetPageText(0).Text.Where(c => !char.IsWhiteSpace(c))));
        }
        finally
        {
            File.Delete(saved);
        }
    }

    // ---- the underline rule ----
    //
    // Underline is drawn as a filled rectangle PATH hung on the same stamp
    // annotation as the words. FPDFAnnot_AppendObject gates on the annotation's
    // subtype rather than on the object's type, so reading the source says a
    // path is admissible — but that gate is exactly what cost a regression when
    // the rect was set after the objects instead of before, and "reads back
    // perfectly while drawing nothing" is this API's signature failure. So it is
    // measured, in pixels, before anything is built on it.

    [Fact]
    public void Underline_PutsAPathOnThePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        // hello.pdf has words of its own, so each box is measured as the change
        // it makes rather than as a total.
        int bare = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.AddTextBox(0, 60, 300, Sample("Hamburgefonstiv"));
        int withWords = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        doc.AddTextBox(0, 60, 400, Sample("Hamburgefonstiv") with { Underline = true });
        int withBoth = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        int words = withWords - bare;
        int wordsAndRule = withBoth - withWords;

        // The same words twice, so the second box has to cost MORE than the
        // first by the area of the rule.
        Assert.True(wordsAndRule > words,
            $"the rule never reached the page: {words} dark pixels for the words, {wordsAndRule} for words + rule");
    }

    [Fact]
    public void Underline_SurvivesSaveAndReopen()
    {
        string saved = TempPdf();
        try
        {
            int expected;
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 300, Sample() with { Underline = true });
                expected = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);

            // A path that serializes wrong is the failure this catches: it draws
            // in the session that built it and vanishes on the way back in.
            Assert.Equal(expected, PixelAssert.CountDark(reopened.RenderPage(0, 1.0f)));
            Assert.True(reopened.TryReadTextBox(0, 0)!.Underline);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    // ---- route B: text in the page's own content ----

    [Fact]
    public void PageContent_RendersOnThePage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        int before = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));
        Assert.NotNull(doc.AddTextToPageContent(0, 60, 300, Sample()));
        int after = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        Assert.True(after > before,
            $"nothing was drawn: {before} dark pixels before, {after} after");
    }

    [Fact]
    public void PageContent_IsSearchableAfterSaveAndReopen()
    {
        string saved = TempPdf();
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextToPageContent(0, 60, 300, Sample("Zarquon"));
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);

            Assert.True(PixelAssert.CountDark(reopened.RenderPage(0, 1.0f)) > 0);
            Assert.Contains("Zarquon", reopened.GetPageText(0).Text);
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void Colour_ReachesThePixels()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddTextToPageContent(0, 60, 300,
            new TextBoxContent("Wwwwwwww", PdfStandardFont.Helvetica, 36, 220, 0, 0));

        var bmp = doc.RenderPage(0, 1.0f);
        int red = 0;
        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                var (b, g, r) = PixelAssert.Pixel(bmp, x, y);
                if (r > 140 && g < 90 && b < 90) { red++; }
            }
        }
        Assert.True(red > 0, "the fill colour never reached the page");
    }
}
