using System.Security.Cryptography;
using PDFDesk.Engine;

namespace PDFDesk.Tests;

public class PageDraftTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatePreservesInternalAndExternalLinksAfterSave(bool includeTarget)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Copy(PixelAssert.CorpusPath("linked.pdf"), path, true);
            using var doc = PdfDocument.Open(path);
            var draft = new PageDraft(doc.PageCount);
            draft.Copy(includeTarget ? [0, 2] : [0]);
            using var saved = draft.Save(doc);
            using var reopened = PdfDocument.Open(path);
            foreach (int page in new[] { 0, includeTarget ? 3 : 1 })
            {
                var links = reopened.GetLinks(page);
                Assert.Equal(includeTarget ? 2 : 3, Assert.Single(links, l => l.IsInternal).TargetPageIndex);
                Assert.Equal("https://example.com/", Assert.Single(links, l => !l.IsInternal).Uri);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SaveAsAfterConflictExportsDraftAndPreservesExternalFile()
    {
        string path = Path.GetTempFileName(), output = Path.GetTempFileName();
        try
        {
            File.Copy(PixelAssert.CorpusPath("slides.pdf"), path, true);
            using var doc = PdfDocument.Open(path);
            var draft = new PageDraft(doc.PageCount);
            var expected = doc.ExtractText(2);
            draft.Move([2], 0);
            File.Copy(PixelAssert.CorpusPath("hello.pdf"), output, true);
            File.Replace(output, path, null);
            byte[] external = File.ReadAllBytes(path);
            Assert.Throws<IOException>(() => draft.Save(doc));
            Assert.Throws<IOException>(() => draft.SaveAs(doc, path));
            Assert.Throws<IOException>(() => draft.SaveAs(doc, @"\\\\?\" + path));
            draft.SaveAs(doc, output);
            using var reopened = PdfDocument.Open(output);
            Assert.Equal(3, reopened.PageCount);
            Assert.Equal(expected, reopened.ExtractText(0));
            Assert.Equal(external, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public void CopyBookmarksFollowOriginalPagesAndSurviveReopen()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string path = PixelAssert.CorpusPath("slides.pdf");
        string output = Path.Combine(directory, "copy.pdf");
        try
        {
            using var doc = PdfDocument.Open(path);
            var store = new PDFDesk.Services.AppStateStore(directory);
            var state = store.Load();
            List<PDFDesk.Services.BookmarkEntry> bookmarks = [
                new() { PageIndex = 0, Name = "first" }, new() { PageIndex = 1, Name = "removed" },
                new() { PageIndex = 2, Name = "last" }];
            state.StoreCopyBookmarks(path, doc.SavedDigest, bookmarks);
            var draft = new PageDraft(doc.PageCount);
            draft.Remove([1]);
            draft.Move([1], 0);
            draft.Copy([0]);
            var digest = draft.SaveAs(doc, output);
            state.StoreCopyBookmarks(output, digest, draft.MapBookmarks(bookmarks));
            using (File.Open(Path.Combine(directory, "PDFDesk.json.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Assert.False(store.Save(state));
            // Only retry the sidecar state. The finished PDF can even be write-locked.
            using (File.Open(output, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.True(store.Save(state));
            var loaded = new PDFDesk.Services.AppStateStore(directory).Load();
            using var reopened = PdfDocument.Open(output);
            var mapped = loaded.FindRecent(output)!.BookmarksFor(reopened.SavedDigest);
            Assert.Equal([(0, "last"), (2, "first")], mapped.Select(b => (b.PageIndex, b.Name)));
            Assert.Equal([0, 1, 2], loaded.FindRecent(path)!.BookmarksFor(doc.SavedDigest).Select(b => b.PageIndex));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OriginalPageSaveCanRetryBookmarksWithoutRewritingThePdf()
    {
        string directory = Directory.CreateTempSubdirectory().FullName;
        string path = Path.Combine(directory, "original.pdf");
        try
        {
            File.Copy(PixelAssert.CorpusPath("slides.pdf"), path);
            using var original = PdfDocument.Open(path);
            var store = new PDFDesk.Services.AppStateStore(directory);
            var state = store.Load();
            state.Remember(path, "original.pdf", 2, 1, 0, 0);
            var recent = state.FindRecent(path)!;
            List<PDFDesk.Services.BookmarkEntry> initial = [new() { PageIndex = 2, Name = "chapter" }];
            recent.StoreBookmarks(original.SavedDigest, initial, saved: true);
            Assert.True(store.Save(state));

            var draft = new PageDraft(original.PageCount);
            draft.Move([2], 0);
            using var saved = draft.Save(original);
            recent.StoreBookmarks(saved.SavedDigest, draft.MapBookmarks(initial), saved: true);
            using var lockedPdf = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using (File.Open(Path.Combine(directory, "PDFDesk.json.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                Assert.False(store.Save(state));

            Assert.True(store.Save(state));
            var reopened = new PDFDesk.Services.AppStateStore(directory).Load();
            var mark = Assert.Single(reopened.FindRecent(path)!.BookmarksFor(saved.SavedDigest));
            Assert.Equal((0, "chapter"), (mark.PageIndex, mark.Name));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PendingPageEditsMapBookmarkChangesBackToTheSavedDocument()
    {
        var moved = BookmarkRemap.MovePermutation(4, [3], 0);
        Func<int, int?>[] maps = [p => moved[p], p => BookmarkRemap.AfterDelete(p, [1]),
            p => BookmarkRemap.AfterInsert(p, 2, 1)];
        // The working order is [original 3, original 1, new page, original 2].
        Assert.Equal(3, BookmarkRemap.FindSavedPage(0, 4, maps));
        Assert.Equal(1, BookmarkRemap.FindSavedPage(1, 4, maps));
        Assert.Null(BookmarkRemap.FindSavedPage(2, 4, maps));
        Assert.Equal(2, BookmarkRemap.FindSavedPage(3, 4, maps));
    }

    [Fact]
    public void UndoAndRedoRestoreTheEditedSelection()
    {
        var draft = new PageDraft(5);
        draft.Move([1, 3], 0);
        Assert.Equal(new[] { 1, 3 }, draft.Undo());
        Assert.Equal(new[] { 0, 1 }, draft.Redo());
        draft.Copy([0, 2]);
        Assert.Equal(new[] { 0, 2 }, draft.Undo());
        Assert.Equal(new[] { 3, 4 }, draft.Redo());
        draft.Remove([3, 4]);
        Assert.Equal(new[] { 3, 4 }, draft.Undo());
        Assert.Empty(draft.Redo());
    }

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
