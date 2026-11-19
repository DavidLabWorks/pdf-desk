using PDFDesk.Engine;

namespace PDFDesk.Tests;

/// <summary>
/// Editing a text box that is already in the file: reading its style back out,
/// resizing it by re-rendering, and capturing it so an erase can be undone.
///
/// The read-back is deliberately not a session cache. Everything here therefore
/// has to survive a save and a reopen, which is what proves a text box placed
/// last week is as editable as one placed a second ago.
/// </summary>
public class TextBoxEditTests
{
    private static string TempPdf() => Path.Combine(Path.GetTempPath(), $"pdfdesk-tbe-{Guid.NewGuid():N}.pdf");

    private static TextBoxContent Text(string text, double size = 24) =>
        new(text, PdfStandardFont.Helvetica, size, 0, 0, 0);

    [Fact]
    public void EditReopenedText_PreservesWidthStyleAndNeighborIndex()
    {
        var path = TempPdf();
        var before = Text("Original text") with { Bold = true, WidthPt = 140 };
        var after = before with { Text = "Changed text", Italic = true };
        try
        {
            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 250, before);
                doc.AddTextBox(0, 60, 400, Text("Neighbor"));
                doc.SaveAs(path);
            }
            using (var doc = PdfDocument.Open(path))
            {
                Assert.True(doc.UpdateTextBox(0, 0, before, after));
                Assert.Equal(after, doc.TryReadTextBox(0, 0));
                Assert.True(doc.UpdateTextBox(0, 0, after, before));
                Assert.True(doc.UpdateTextBox(0, 0, before, after));
                Assert.Equal("Neighbor", doc.TryReadTextBox(0, 1)?.Text);
                var edited = path + ".pdf";
                try
                {
                    doc.SaveAs(edited);
                    using var saved = PdfDocument.Open(edited);
                    Assert.Equal(after, saved.TryReadTextBox(0, 0));
                }
                finally { File.Delete(edited); }
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MoveText_DrawsAtThePreviewAndDropPositions()
    {
        string saved = TempPdf();
        try
        {
            using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
            var content = Text("Drag this text");
            doc.AddTextBox(0, 60, 230, content);
            var original = Assert.Single(doc.GetAnnotations(0));
            const int width = 200, height = 32;
            var preview = doc.RenderAnnotationPreview(0, 0, new TextRect(300, 480, original.Width, original.Height), 1, 0,
                (58, 228, width, height), (298, 478, width, height));
            Assert.NotNull(preview);
            try
            {
                Assert.Equal(0, PixelAssert.CountDark(preview.Value.Source));
                Assert.True(PixelAssert.CountDark(preview.Value.Target) > 0);
            }
            finally
            {
                preview.Value.Source.Return();
                preview.Value.Target.Return();
            }
            Assert.Equal(original.X, Assert.Single(doc.GetAnnotations(0)).X);
            Assert.Equal(original.Y, Assert.Single(doc.GetAnnotations(0)).Y);

            var before = doc.MoveAnnotation(0, 0, 300, 480);
            Assert.NotNull(before);
            var moved = doc.RenderRegion(0, 1, 0, 298, 478, width, height);
            Assert.True(PixelAssert.CountDark(moved) > 0);
            moved.Return();
            Assert.True(doc.RestoreAnnotationRect(0, 0, before.Value));
            Assert.Equal(original.Y, Assert.Single(doc.GetAnnotations(0)).Y);

            doc.MoveAnnotation(0, 0, 300, 480);
            doc.SaveAs(saved);
            using var reopened = PdfDocument.Open(saved);
            Assert.Equal(content, reopened.TryReadTextBox(0, 0));
            var persisted = reopened.RenderRegion(0, 1, 0, 298, 478, width, height);
            Assert.True(PixelAssert.CountDark(persisted) > 0);
            persisted.Return();
        }
        finally
        {
            File.Delete(saved);
        }
    }

    // ---- the name round-trip ----

    [Fact]
    public void PostScriptName_RoundTripsForEveryFacePDFDeskCanWrite()
    {
        foreach (var font in Enum.GetValues<PdfStandardFont>())
        {
            foreach (bool bold in new[] { false, true })
            {
                foreach (bool italic in new[] { false, true })
                {
                    string name = TextBoxContent.PostScriptNameFor(font, bold, italic);
                    var parsed = TextBoxContent.TryParsePostScriptName(name);

                    Assert.NotNull(parsed);
                    Assert.Equal((font, bold, italic), parsed!.Value);
                }
            }
        }
    }

    [Fact]
    public void TryParsePostScriptName_HandlesASubsetTagAndRefusesAStranger()
    {
        // Six capitals and a plus is an embedded subset. The standard 14 are
        // never subset, but a file that has been through another tool is not
        // PDFDesk's to assume about.
        Assert.Equal((PdfStandardFont.Helvetica, true, false),
            TextBoxContent.TryParsePostScriptName("ABCDEF+Helvetica-Bold"));

        Assert.Null(TextBoxContent.TryParsePostScriptName("ArialMT"));
        Assert.Null(TextBoxContent.TryParsePostScriptName("Helvetica-Italic")); // Oblique, in this family
        Assert.Null(TextBoxContent.TryParsePostScriptName(""));
        Assert.Null(TextBoxContent.TryParsePostScriptName(null));
    }

    // ---- telling the two kinds of stamp apart ----

    [Fact]
    public void GetStampKind_SeparatesTextFromPixelsAndFromEverythingElse()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        doc.AddTextBox(0, 60, 100, Text("Words"));
        Assert.Equal(StampKind.Text, doc.GetStampKind(0, 0));

        var opaque = new byte[40 * 20 * 4];
        for (int i = 3; i < opaque.Length; i += 4) { opaque[i] = 255; }
        doc.AddStamp(0, 60, 300, 80, 40, opaque, 40, 20);
        Assert.Equal(StampKind.Image, doc.GetStampKind(0, 1));

        doc.AddNote(0, 200, 200, "not a stamp");
        Assert.Equal(StampKind.None, doc.GetStampKind(0, 2));
    }

    // ---- reading the style back ----

    [Fact]
    public void TryReadTextBox_RecoversEveryPartOfTheStyleAfterAReopen()
    {
        string saved = TempPdf();
        try
        {
            var written = new TextBoxContent(
                "Read me back\nboth lines", PdfStandardFont.Times, 19, 200, 40, 60,
                Bold: true, Italic: true);

            using (var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf")))
            {
                doc.AddTextBox(0, 60, 200, written);
                doc.SaveAs(saved);
            }

            using var reopened = PdfDocument.Open(saved);
            var read = reopened.TryReadTextBox(0, 0);

            Assert.NotNull(read);
            Assert.Equal(written.Text, read!.Text);
            Assert.Equal(written.PostScriptName, read.PostScriptName);
            Assert.Equal(written.FontSize, read.FontSize, 2);
            Assert.Equal((written.R, written.G, written.B), (read.R, read.G, read.B));
        }
        finally
        {
            if (File.Exists(saved)) { File.Delete(saved); }
        }
    }

    [Fact]
    public void TryReadTextBox_ReturnsNullForAPictureAndForANote()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));

        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        doc.AddStamp(0, 60, 100, 40, 40, pixels, 16, 16);
        doc.AddNote(0, 200, 200, "a note");

        Assert.Null(doc.TryReadTextBox(0, 0));
        Assert.Null(doc.TryReadTextBox(0, 1));
    }

    // ---- resizing ----

    [Fact]
    public void ResizeTextBox_ReflowsTheWordsAndLeavesTheSizeAlone()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Reflow me into several lines when the box gets narrow", size: 12));

        var before = doc.GetAnnotations(0)[0];
        var resized = doc.ResizeTextBox(0, 0, 60, 200, 120);

        Assert.NotNull(resized);
        var after = doc.GetAnnotations(0)[resized!.Value.NewIndex];

        // The point size is the one thing a corner drag must not touch: that is
        // what the size picker and Ctrl+Shift+</> are for.
        Assert.Equal(12, doc.TryReadTextBox(0, resized.Value.NewIndex)!.FontSize, 1);

        // Narrower box, and the words that no longer fit went onto new lines,
        // so the block is taller than the single line it started as.
        Assert.InRange(after.Width, 119, 121);
        Assert.True(after.Width < before.Width, "the box did not get narrower");
        Assert.True(after.Height > before.Height, "the words did not reflow onto more lines");
    }

    [Fact]
    public void ResizeTextBox_RemembersTheWidthForTheNextRead()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Wide enough to wrap somewhere in here", size: 12));

        Assert.NotNull(doc.ResizeTextBox(0, 0, 60, 200, 150));

        // Auto-width before the drag, an explicit box after it — otherwise the
        // second drag would start from scratch and re-wrap from nothing.
        Assert.Equal(150, doc.TryReadTextBox(0, 0)!.WidthPt, 1);
    }

    [Fact]
    public void ResizeTextBox_UndoesBackToThePixelsItStartedWith()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Undo me after a reflow onto two lines", size: 18));
        int original = PixelAssert.CountDark(doc.RenderPage(0, 1.0f));

        var resized = doc.ResizeTextBox(0, 0, 60, 200, 140);
        Assert.NotNull(resized);
        Assert.NotEqual(original, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));

        // The undo pair, exactly as the viewer plays it back.
        Assert.True(doc.RemoveAnnotation(0, resized!.Value.NewIndex));
        doc.AddAnnotationFromSpec(resized.Value.Before);

        // Rebuilt from the words rather than from a raster, so it comes back
        // pixel-identical rather than merely close.
        Assert.Equal(original, PixelAssert.CountDark(doc.RenderPage(0, 1.0f)));
    }

    [Fact]
    public void ResizeTextBox_WillNotSqueezeTheBoxNarrowerThanTheType()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 60, 200, Text("Tiny", size: 24));

        var resized = doc.ResizeTextBox(0, 0, 60, 200, 1);

        // A box a point wide wraps to one character a line and cannot be picked
        // up again, so the floor is the type size itself.
        Assert.NotNull(resized);
        Assert.Equal(24, doc.TryReadTextBox(0, resized!.Value.NewIndex)!.WidthPt, 1);
        Assert.Equal(24, doc.TryReadTextBox(0, resized.Value.NewIndex)!.FontSize, 1);
    }

    [Fact]
    public void ResizeTextBox_RefusesAPictureAndLeavesItAlone()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        var pixels = new byte[16 * 16 * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        doc.AddStamp(0, 60, 100, 40, 40, pixels, 16, 16);

        Assert.Null(doc.ResizeTextBox(0, 0, 60, 100, 200));
        Assert.Single(doc.GetAnnotations(0));
    }

}
