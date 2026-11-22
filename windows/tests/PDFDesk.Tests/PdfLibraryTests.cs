using PDFDesk.Engine;
using PDFDesk.Services;

namespace PDFDesk.Tests;

public class PdfLibraryTests
{
    [Theory]
    [InlineData("-balanced.pdf")]
    [InlineData("-均衡压缩 (12).pdf")]
    public void LongCopyNamesKeepSuffixAndWholeUnicodeCharacters(string ending)
    {
        string prefix = new('x', 254 - ending.Length);
        string name = PdfLibrary.FitFileName(prefix + "📄book", ending);
        Assert.Equal(prefix + ending, name);
    }

    [Fact]
    public void CreateBlank_SavesAnEditableA4PdfWithoutOverwriting()
    {
        var directory = Directory.CreateTempSubdirectory("PDFDesk-blank-");
        string original = Path.Combine(directory.FullName, "会议记录.pdf");
        string duplicate = Path.Combine(directory.FullName, "会议记录 (2).pdf");
        try
        {
            var library = new PdfLibrary(directory.FullName);
            Assert.Equal(original, library.CreateBlank("会议记录"));
            Assert.Equal(duplicate, library.CreateBlank("会议记录.pdf"));

            using var document = PdfDocument.Open(original);
            Assert.Equal(1, document.PageCount);
            var (width, height) = document.GetPageSize(0);
            Assert.Equal(595.28f, width, 2);
            Assert.Equal(841.89f, height, 2);
            Assert.Empty(document.GetPageText(0).Text);
            Assert.Empty(document.GetAnnotations(0));
            document.AddNote(0, 80, 80, "First edit");
            document.SaveInPlace();
            using var reopened = PdfDocument.Open(original);
            Assert.Equal("First edit", Assert.Single(reopened.GetAnnotations(0)).Contents);
            using var untouched = PdfDocument.Open(duplicate);
            Assert.Empty(untouched.GetAnnotations(0));
        }
        finally
        {
            File.Delete(original);
            File.Delete(duplicate);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(".pdf")]
    [InlineData("../outside.pdf")]
    [InlineData("CON.txt")]
    [InlineData("LPT1.pdf")]
    public void InvalidBlankDocumentNamesAreRejected(string name)
    {
        Assert.False(PdfLibrary.IsValidDocumentName(name));
    }

    [Fact]
    public async Task ImportsAreIndependentAndNeverOverwriteAnotherCopy()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new AppStateStore(root);
            var library = new PdfLibrary(store.LibraryDirectory);
            string source = Path.Combine(root, "sample.pdf");
            await File.WriteAllTextAsync(source, "original");
            string[] copies = await Task.WhenAll(library.ImportAsync(source), library.ImportAsync(source));
            Assert.NotEqual(copies[0], copies[1]);
            Assert.All(copies, file => Assert.True(library.Contains(file)));
            Assert.False(library.Contains(source));
            await File.WriteAllTextAsync(copies[0], "edited copy");
            Assert.Equal("original", await File.ReadAllTextAsync(source));
            File.Delete(source);
            Assert.Equal("original", await File.ReadAllTextAsync(copies[1]));
            Assert.Equal(copies[1], await library.ImportAsync(copies[1]));
            await Assert.ThrowsAsync<FileNotFoundException>(() => library.ImportAsync(source));
            Assert.Empty(Directory.GetFiles(store.LibraryDirectory, "*.partial"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
