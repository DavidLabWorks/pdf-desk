using System.Security.Cryptography;
using PDFDesk.Engine;

namespace PDFDesk.Tests;

public class PageDraftTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveIsTheOnlyWriteAndDetectsConflicts(bool externalChange)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pdfdesk-draft-{Guid.NewGuid():N}.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "corpus", "slides.pdf"), path);
        try
        {
            using var original = PdfDocument.Open(path);
            var originalText = Enumerable.Range(0, 3).Select(i => original.ExtractText(i)).ToArray();
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var draft = new PageDraft(3);
            draft.Move([2], 0);
            draft.Copy([0, 2]);
            draft.Remove([1]);
            draft.Undo();
            draft.Redo();
            Assert.Equal(new int?[] { null, 1, 0 }, draft.OriginalPositions());
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
            Assert.False(original.IsDirty);
            if (externalChange)
            {
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddHours(-1));
                Assert.Throws<IOException>(() => draft.Save(original));
                Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
                Assert.Equal(3, original.PageCount);
                Assert.True(draft.Changed);
            }
            else
            {
                using var saved = draft.Save(original);
                using var reopened = PdfDocument.Open(path);
                Assert.Equal(4, reopened.PageCount);
                Assert.Equal(new[] { 2, 1, 2, 1 }.Select(i => originalText[i]),
                    Enumerable.Range(0, 4).Select(i => reopened.ExtractText(i)));
                Assert.True(saved.MatchesFileOnDisk());
                Assert.False(saved.IsDirty);
            }
        }
        finally { File.Delete(path); }
    }
}
