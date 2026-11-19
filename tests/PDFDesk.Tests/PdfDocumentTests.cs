using PDFDesk.Engine;
using PDFDesk.PdfiumInterop;

namespace PDFDesk.Tests;

public class PdfDocumentTests
{
    private static string CorpusPath(string name)
    {
        // tests/corpus is copied next to the test assembly via the csproj.
        return Path.Combine(AppContext.BaseDirectory, "corpus", name);
    }

    [Fact]
    public void Open_ValidPdf_ReportsPageCountAndSizes()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Assert.Equal(2, doc.PageCount);

        var (width, height) = doc.GetPageSize(0);
        Assert.Equal(612f, width, precision: 1);   // US Letter in PDF points
        Assert.Equal(792f, height, precision: 1);
    }

    [Fact]
    public void SaveInPlace_CanSaveTwiceAndKeepReadingFromTheSameDocument()
    {
        using var fixture = new SaveFixture(CorpusPath("hello.pdf"));
        using var document = PdfDocument.Open(fixture.Path);

        document.AddNote(0, 80, 80, "First edit");
        document.SaveInPlace();
        Assert.False(document.IsDirty);

        Assert.Contains("Page two", document.GetPageText(1).Text);
        document.AddNote(1, 80, 80, "Second edit");
        document.SaveInPlace();
        Assert.False(document.IsDirty);

        using var reopened = PdfDocument.Open(fixture.Path);
        Assert.Equal("First edit", Assert.Single(reopened.GetAnnotations(0)).Contents);
        Assert.Equal("Second edit", Assert.Single(reopened.GetAnnotations(1)).Contents);
    }

    [Fact]
    public void SaveInPlace_WhenFileIsLocked_KeepsEditsAndAllowsRetry()
    {
        using var fixture = new SaveFixture(CorpusPath("hello.pdf"));
        using var document = PdfDocument.Open(fixture.Path);
        document.AddNote(0, 80, 80, "Keep me");

        using (File.Open(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(document.SaveInPlace);
            Assert.True(document.IsDirty);
            Assert.Equal("Keep me", Assert.Single(document.GetAnnotations(0)).Contents);
        }

        document.SaveInPlace();
        using var reopened = PdfDocument.Open(fixture.Path);
        Assert.Equal("Keep me", Assert.Single(reopened.GetAnnotations(0)).Contents);
    }

    [Fact]
    public void SaveInPlace_DoesNotOverwriteAnExternalReplacement()
    {
        using var fixture = new SaveFixture(CorpusPath("hello.pdf"));
        using var document = PdfDocument.Open(fixture.Path);
        document.AddNote(0, 80, 80, "Unsaved edit");

        string incoming = fixture.Path + ".incoming";
        File.Copy(CorpusPath("slides.pdf"), incoming);
        File.Replace(incoming, fixture.Path, null);

        Assert.Throws<IOException>(document.SaveInPlace);
        Assert.True(document.IsDirty);
        using var external = PdfDocument.Open(fixture.Path);
        Assert.Equal(3, external.PageCount);
        Assert.Empty(external.GetAnnotations(0));
    }

    private sealed class SaveFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("PDFDesk-save-");

        public SaveFixture(string source)
        {
            Path = System.IO.Path.Combine(_directory.FullName, "test.pdf");
            File.Copy(source, Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            File.Delete(Path);
            _directory.Delete();
        }
    }

    [Fact]
    public void RenderPage_ProducesInkOnWhiteBackground()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var bmp = doc.RenderPage(0, scale: 1.0f);

        Assert.Equal(612, bmp.Width);
        Assert.Equal(792, bmp.Height);
        Assert.True(bmp.Pixels.Length >= bmp.Stride * bmp.Height); // pooled buffers may be larger

        // Count non-white pixels: the "Hello from PDFDesk!" text must have
        // produced some ink, but most of the page must remain white.
        int nonWhite = 0;
        for (int i = 0; i < bmp.Stride * bmp.Height; i += 4)
        {
            if (bmp.Pixels[i] != 0xFF || bmp.Pixels[i + 1] != 0xFF || bmp.Pixels[i + 2] != 0xFF)
            {
                nonWhite++;
            }
        }

        int totalPixels = bmp.Width * bmp.Height;
        Assert.InRange(nonWhite, 100, totalPixels / 2);
    }

    [Fact]
    public void RenderPage_AtDoubleScale_DoublesPixelDimensions()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        var bmp = doc.RenderPage(0, scale: 2.0f);

        Assert.Equal(1224, bmp.Width);
        Assert.Equal(1584, bmp.Height);
    }

    [Fact]
    public void RenderRegion_RequestPastBudget_ThrowsInsteadOfOverflowing()
    {
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        // width × 4 × height silently overflows int here (32768 × 4 × 32768 =
        // 4.3e9), which used to surface as an ArgumentOutOfRangeException from
        // ArrayPool.Rent on a *negative* length. The guard has to fire first,
        // and has to do so before anything is allocated.
        var ex = Assert.Throws<PdfiumException>(
            () => doc.RenderRegion(0, scale: 1.0f, rotation: 0, srcX: 0, srcY: 0, width: 32768, height: 32768));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void ClampScaleToBudget_OrdinaryPage_LeavesScaleAlone()
    {
        // US Letter at print resolution: ~2 Mpx, nowhere near the cap.
        Assert.Equal(150f / 72f, PdfDocument.ClampScaleToBudget(612f, 792f, 150f / 72f));
    }

    [Fact]
    public void ClampScaleToBudget_MaximumSizedPage_FitsTheBudget()
    {
        // 14400 pt is the largest page the PDF spec allows, and a hostile file
        // can declare it. At 150 DPI that is 30000 × 30000 px — 900 Mpx.
        const float side = 14400f;
        float clamped = PdfDocument.ClampScaleToBudget(side, side, 150f / 72f);

        Assert.True(clamped < 150f / 72f, "an oversized page must have its scale reduced");

        // Check the whole-pixel dimensions, not the real-valued area: those are
        // what RenderRegion's guard sees, and rounding up to them is exactly how
        // a clamp aimed at the cap would still throw.
        long side_px = (int)MathF.Round(side * clamped);
        Assert.True(side_px * side_px <= PdfDocument.MaxRenderPixels,
            $"clamped render is {side_px}×{side_px}, past the {PdfDocument.MaxRenderPixels} budget");
    }

    [Fact]
    public void Open_CorruptFile_ThrowsPdfiumException()
    {
        var ex = Assert.Throws<PdfiumException>(() => PdfDocument.Open(CorpusPath("corrupt.pdf")));
        Assert.NotEqual(0u, ex.ErrorCode);
    }

    [Fact]
    public void Open_MissingFile_ThrowsFileNotFound()
    {
        Assert.ThrowsAny<IOException>(() => PdfDocument.Open(CorpusPath("does-not-exist.pdf")));
    }

    [Fact]
    public void RenderPage_ConcurrentCalls_DoNotCrash()
    {
        // PDFium itself is single-threaded; our lock must make concurrent
        // engine calls safe. Hammer it from several threads.
        using var doc = PdfDocument.Open(CorpusPath("hello.pdf"));

        Parallel.For(0, 16, i =>
        {
            var bmp = doc.RenderPage(i % doc.PageCount, scale: 0.5f);
            Assert.True(bmp.Width > 0);
        });
    }
}
