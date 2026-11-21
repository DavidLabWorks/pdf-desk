using PDFDesk.Engine;
using PDFDesk.Services;

namespace PDFDesk.Tests;

public class DocumentIntegrityTests
{
    [Fact]
    public void LoadedDocumentIdentityComesFromTheOpenHandleAcrossPathReplacements()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(dir.FullName, "document.pdf");
            string next = Path.Combine(dir.FullName, "new.pdf");
            string backup = Path.Combine(dir.FullName, "opened.pdf");
            File.Copy(PixelAssert.CorpusPath("slides.pdf"), path);
            var reserved = new WindowsFileIdentity.NavigationKey(path);
            File.Copy(path, next);
            File.Replace(next, path, null);
            using var document = PdfDocument.Open(path);
            File.Copy(path, next);
            File.Replace(next, path, backup);
            var opened = document.GetOpenedNavigationKey(reserved);
            var backupKey = new WindowsFileIdentity.NavigationKey(backup);
            Assert.False(reserved.Matches(backupKey));
            Assert.True(opened.Matches(backupKey));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void NavigationLookupUsesCapturedIdentityAfterOriginalPathDisappears()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string original = Path.Combine(directory.FullName, "old.pdf");
            string moved = Path.Combine(directory.FullName, "moved.pdf");
            File.WriteAllText(original, "original");
            var tab = new WindowsFileIdentity.NavigationKey(original);
            File.Move(original, moved);
            var movedFile = new WindowsFileIdentity.NavigationKey(moved);
            Assert.True(tab.Matches(movedFile));
            File.WriteAllText(original, "replacement");
            var replacement = new WindowsFileIdentity.NavigationKey(original);
            var updated = tab.AfterReplacement(replacement);
            Assert.True(updated.Matches(replacement));
            Assert.False(updated.Matches(movedFile));
            Assert.True(movedFile.AfterReplacement(replacement).Matches(tab));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void SnapshotRetainsSavedBytesAndRejectsEditsAfterEditorSaves()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string original = PixelAssert.CorpusPath("slides.pdf");
            string path = Path.Combine(dir.FullName, "reader.pdf");
            string copy = Path.Combine(dir.FullName, "copy.pdf");
            File.Copy(original, path);
            using var writer = PdfDocument.Open(path);
            using var snapshot = PdfDocument.OpenReadOnly(path);
            writer.DeletePages([0]);
            writer.SaveInPlace();
            Assert.Equal(writer.PageCount + 1, snapshot.PageCount);
            Assert.NotEqual(writer.SavedDigest, snapshot.SavedDigest);
            Assert.Throws<InvalidOperationException>(() => snapshot.DeletePages([0]));
            Assert.Throws<InvalidOperationException>(() => snapshot.AddNote(0, 5, 5, "note"));
            Assert.Throws<InvalidOperationException>(snapshot.SaveInPlace);
            snapshot.SaveSharingCopy(copy);
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(copy));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void MissingSourceBlocksFurtherEditsAndPreservesPendingChangesInCopy()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string source = Path.Combine(dir.FullName, "reader.pdf");
            string moved = Path.Combine(dir.FullName, "moved.pdf");
            string copy = Path.Combine(dir.FullName, "copy.pdf");
            File.Copy(PixelAssert.CorpusPath("hello.pdf"), source);
            using var reader = PdfDocument.Open(source);
            reader.AddNote(0, 40, 40, "keep this edit");
            File.Move(source, moved);
            reader.SetSourceMissing(true);
            Assert.Throws<InvalidOperationException>(() => reader.AddNote(0, 80, 80, "later edit"));
            reader.SaveSharingCopy(copy);
            using var saved = PdfDocument.Open(copy);
            Assert.Contains(saved.GetAnnotations(0), a => a.Contents == "keep this edit");
            Assert.False(File.Exists(source));
            saved.AddNote(0, 80, 80, "new edit");
            saved.SaveInPlace();
            reader.MakeReadOnly();
            reader.SetSourceMissing(false);
            Assert.True(reader.IsReadOnlySnapshot);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void SnapshotBookmarksRemainAvailableAfterEditorSaves()
    {
        var recent = new RecentFile { Path = "reader.pdf" };
        recent.StoreBookmarks("first", [new() { PageIndex = 2, Name = "three" }], true);
        recent.StoreBookmarks("second", [new() { PageIndex = 1, Name = "three" }], true);
        Assert.Equal(2, Assert.Single(recent.BookmarksFor("first")).PageIndex);
        recent.StoreBookmarks("first", [], false);
        Assert.Equal(1, Assert.Single(recent.BookmarksFor("second")).PageIndex);
    }

    [Fact]
    public void InsertionRedoPreservesSubsequentFormEntries()
    {
        using var reader = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        var bytes = File.ReadAllBytes(PixelAssert.CorpusPath("form.pdf"));
        var insertion = new InsertedPages(1, reader.InsertPages(bytes, 1));
        foreach (char letter in "ZX")
        {
            var field = reader.GetFormFields(1).First(f => f.Name == "name");
            Assert.True(reader.FormClick(1, field.X + field.Width / 2, field.Y + field.Height / 2));
            Assert.True(reader.FormChar(1, letter));
            reader.FormKillFocus();
            var value = reader.GetFormFieldValue(1, "name");
            Assert.Contains(letter.ToString(), value);
            insertion.Undo(reader);
            Assert.True(insertion.SnapshotBytes > 0);
            insertion.Redo(reader);
            Assert.Equal(value, reader.GetFormFieldValue(1, "name"));
            Assert.Equal(0, insertion.SnapshotBytes);
        }
    }

    [Fact]
    public void SharingCopyKeepsOpenBytesAfterPathReplacementAndIncludesSubsequentEdits()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            string original = PixelAssert.CorpusPath("hello.pdf");
            string path = Path.Combine(dir.FullName, "reader.pdf");
            string copy = Path.Combine(dir.FullName, "share.pdf");
            string replacement = Path.Combine(dir.FullName, "other.pdf");
            File.Copy(original, path);
            using var reader = PdfDocument.Open(path);
            File.Copy(PixelAssert.CorpusPath("slides.pdf"), replacement);
            File.Replace(replacement, path, null);
            reader.SaveSharingCopy(copy);
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(copy));
            reader.AddNote(0, 50, 50, "annotation");
            reader.SaveSharingCopy(copy);
            using var shared = PdfDocument.Open(copy);
            Assert.Equal(reader.PageCount, shared.PageCount);
            Assert.Contains(shared.GetAnnotations(0), a => a.Contents == "annotation");
            Assert.True(reader.IsDirty);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("legacy")]
    [InlineData("tree")]
    public void SubsetAndPasteKeepLinksToSelectedPages(string kind)
    {
        string path = Path.GetTempFileName(), exported = Path.GetTempFileName();
        try
        {
            var encoding = System.Text.Encoding.Latin1;
            string contents = File.ReadAllText(PixelAssert.CorpusPath("linked.pdf"), encoding);
            if (kind != "direct")
            {
                string key = kind == "legacy" ? "/Jump" : "(Jump)";
                string names = kind == "legacy" ? "/Dests << /Jump [5 0 R /Fit] >>" :
                    "/Names << /Dests << /Names [(Jump) [5 0 R /Fit]] >> >>";
                contents = contents.Replace("/Outlines 6 0 R", "/Outlines 6 0 R " + names)
                    .Replace("/Border [0 0 0] /Dest [5 0 R /Fit]", "/Border [0 0 0] /A << /S /GoTo /D " + key + " >>");
            }
            File.WriteAllText(path, contents, encoding);
            using var source = PdfDocument.Open(path);
            Assert.Equal(2, Assert.Single(source.GetLinks(0), link => link.IsInternal).TargetPageIndex);
            source.ExportPagesToFile([0, 2], exported);
            using var subset = PdfDocument.Open(exported);
            Assert.Equal(1, Assert.Single(subset.GetLinks(0), link => link.IsInternal).TargetPageIndex);
            using var target = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
            target.InsertPages(File.ReadAllBytes(exported), 1);
            Assert.Equal(2, Assert.Single(target.GetLinks(1), link => link.IsInternal).TargetPageIndex);
            Assert.Equal("https://example.com/", Assert.Single(target.GetLinks(1), link => !link.IsInternal).Uri);
            target.InsertPagesFromFile(path, 0);
            Assert.Equal(2, Assert.Single(target.GetLinks(0), link => link.IsInternal).TargetPageIndex);
        }
        finally { File.Delete(path); File.Delete(exported); }
    }

    [Fact]
    public void PrintSnapshotRendersUnsavedEditsAfterSourceCloses()
    {
        using var source = PdfDocument.Open(PixelAssert.CorpusPath("slides.pdf"));
        source.AddInk(0, [(10, 10), (40, 40)], 0, 0, 0, 255, 2);
        using var snapshot = source.CreatePrintSnapshot();
        source.AddInk(0, [(20, 10), (50, 40)], 0, 0, 0, 255, 2);
        source.Dispose();

        Assert.Single(snapshot.GetAnnotations(0));
        Assert.True(snapshot.PageCount > 1);
        for (int i = 0; i < snapshot.PageCount; i++)
        {
            var page = snapshot.RenderPage(i, 0.5f);
            try { Assert.True(page.Width > 0 && page.Height > 0); }
            finally { page.Return(); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackMovePreservesLinksAndOutlineAfterSave(bool includeTarget)
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("linked.pdf"));
        doc.MovePagesWithQpdf(includeTarget ? [0, 2] : [0], 1);
        string path = Path.GetTempFileName();
        try
        {
            doc.SaveAs(path);
            using var reopened = PdfDocument.Open(path);
            var links = reopened.GetLinks(1);
            Assert.Equal(2, Assert.Single(links, l => l.IsInternal).TargetPageIndex);
            Assert.Equal("https://example.com/", Assert.Single(links, l => !l.IsInternal).Uri);
            Assert.Equal(1, reopened.GetOutline()[0].PageIndex);
            Assert.Equal(2, reopened.GetOutline()[1].PageIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedCrossPageInkRedoCanRetryWithoutDuplicatingTheFirstPage()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        AnnotationSpec[] pieces = Enumerable.Range(0, 2).Select(page =>
        {
            doc.AddInk(page, [(10, 10), (40, 40)], 0, 0, 0, 255, 2);
            return doc.CaptureLastAnnotation(page)!;
        }).ToArray();
        doc.ReplayInkStroke(pieces, undo: true);
        long revision = doc.UnsavedRevision;
        var broken = pieces[1] with { Footprint = new InkFootprint([[1]], 0, 0, 0, 255) };
        Assert.Throws<InvalidOperationException>(() => doc.ReplayInkStroke([pieces[0], broken], undo: false));
        Assert.Equal(revision, doc.UnsavedRevision);
        Assert.Empty(doc.GetAnnotations(0));
        Assert.Empty(doc.GetAnnotations(1));
        doc.ReplayInkStroke(pieces, undo: false);
        string path = Path.GetTempFileName();
        try
        {
            doc.SaveAs(path);
            using var reopened = PdfDocument.Open(path);
            Assert.Single(reopened.GetAnnotations(0));
            Assert.Single(reopened.GetAnnotations(1));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BookmarkReplayPreservesChangesOnSurvivingAndReintroducedPages()
    {
        static BookmarkEntry Mark(int page, string name) => new() { PageIndex = page, Name = name };
        static int? Delete(int p) => BookmarkRemap.AfterDelete(p, [1]);
        static int? Restore(int p) => BookmarkRemap.AfterInsert(p, 1, 1);
        BookmarkEntry[] before = [Mark(0, "removed"), Mark(1, "deleted page"), Mark(2, "old")];
        BookmarkEntry[] current = [Mark(1, "renamed"), Mark(2, "new")];
        var restored = BookmarkRemap.ReplayPageEdit(current, before, Restore, Delete);
        Assert.Equal([(1, "deleted page"), (2, "renamed"), (3, "new")], restored.Select(b => (b.PageIndex, b.Name)));
        restored[0].Name = "changed after undo";
        var redone = BookmarkRemap.ReplayPageEdit(restored, current, Delete, Restore);
        var again = BookmarkRemap.ReplayPageEdit(redone, restored, Restore, Delete);
        Assert.Equal("changed after undo", again[0].Name);
        Assert.Equal(["renamed", "new"], redone.Select(b => b.Name));
    }

    [Fact]
    public void UndoMoveKeepsBookmarksWithTheirPages()
    {
        var map = BookmarkRemap.MovePermutation(3, [2], 0);
        int? Forward(int page) => map[page];
        int? Reverse(int page) => Array.IndexOf(map, page);
        BookmarkEntry[] original = [new() { PageIndex = 2, Name = "old" }];
        BookmarkEntry[] afterMove = [new() { PageIndex = 0, Name = "renamed" }, new() { PageIndex = 1, Name = "new" }];
        var undo = BookmarkRemap.ReplayPageEdit(afterMove, original, Reverse, Forward);
        Assert.Equal([(0, "new"), (2, "renamed")], undo.Select(b => (b.PageIndex, b.Name)));
        var redo = BookmarkRemap.ReplayPageEdit(undo, afterMove, Forward, Reverse);
        Assert.Equal([(0, "renamed"), (1, "new")], redo.Select(b => (b.PageIndex, b.Name)));
    }

    [Fact]
    public void TextHistoryRejectsMismatchAndCanRetry()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddTextBox(0, 20, 20, new TextBoxContent("original", PdfStandardFont.Helvetica, 12, 0, 0, 0));
        var original = doc.TryReadTextBox(0, 0)!;
        var edited = original with { Text = "edited" };
        doc.ReplayTextBoxEdit(0, 0, original, edited);
        Assert.Throws<InvalidOperationException>(() => doc.ReplayTextBoxEdit(0, 0, original, edited));
        Assert.Equal(edited, doc.TryReadTextBox(0, 0));
        doc.ReplayTextBoxEdit(0, 0, edited, original);
        Assert.Equal(original, doc.TryReadTextBox(0, 0));
    }

    [Fact]
    public void UndoResizeRestoresOriginalOrderAndCanRetryAfterFailure()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddStamp(0, 10, 10, 20, 20, [0, 0, 0, 255], 1, 1);
        doc.AddNote(0, 80, 80, "keep");
        var resized = doc.ResizeStamp(0, 0, 30, 30, 50, 50)!.Value;
        var beforeUndo = doc.GetAnnotations(0).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            doc.RestoreDeletedAnnotation(resized.Before, 0, replacingIndex: 99));
        Assert.Equal(beforeUndo, doc.GetAnnotations(0).ToArray());
        doc.RestoreDeletedAnnotation(resized.Before, 0, replacingIndex: resized.NewIndex);
        Assert.NotNull(doc.TryReadStampImage(0, 0));
        Assert.Equal(1, Assert.Single(doc.GetNotes(0)).Index);
        Assert.Equal(2, doc.GetAnnotations(0).Count);
    }

    [Fact]
    public void InvalidInkGeometryLeavesNoAppendedAnnotation()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddNote(0, 10, 10, "keep");
        var note = doc.CaptureAnnotation(0, 0)!;
        var invalid = note with { InkStrokes = [[(1f, 1f), (2f, 2f)]] };
        Assert.Throws<PDFDesk.PdfiumInterop.PdfiumException>(() => doc.AddAnnotationFromSpec(invalid));
        Assert.Equal(0, Assert.Single(doc.GetAnnotations(0)).Index);
    }

    [Fact]
    public void UndoDeleteRestoresInternalAndOutlineDestinationsAfterReopen()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("linked.pdf"));
        var snapshot = doc.CapturePageTree();
        doc.DeletePages([2]);
        doc.RestorePageTree(snapshot);
        string path = Path.GetTempFileName();
        try
        {
            doc.SaveAs(path);
            using var copy = PdfDocument.Open(path);
            Assert.Equal(2, copy.GetOutline()[1].PageIndex);
            Assert.Equal(2, Assert.Single(copy.GetLinks(0), link => link.IsInternal).TargetPageIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void StaleWindowKeepsItsOwnBookmarkCoordinatesAfterSave()
    {
        var file = new RecentFile { Path = "test.pdf" };
        file.StoreBookmarks("old", [new() { PageIndex = 2, Name = "chapter" }], false);
        file.StoreBookmarks("new", [new() { PageIndex = 1, Name = "chapter" }], true);
        file.StoreBookmarks("old", [new() { PageIndex = 2, Name = "renamed" }], false);
        Assert.Equal(1, Assert.Single(file.BookmarksFor("new")).PageIndex);
        Assert.Equal("chapter", Assert.Single(file.Bookmarks).Name);
        Assert.Equal("renamed", Assert.Single(file.BookmarksFor("old")).Name);
    }

    [Fact]
    public void ContiguousFormSelectionsUseCompactPageRanges()
    {
        Assert.Equal("1-7000", PdfDocument.FormatPageRanges(Enumerable.Range(0, 7000).ToArray()));
        Assert.Equal("1,3-4,7", PdfDocument.FormatPageRanges([0, 2, 3, 6]));
    }

    [Fact]
    public void FormPageCanBeExtractedInsertedEditedAndSaved()
    {
        using var form = PdfDocument.Open(PixelAssert.CorpusPath("form.pdf"));
        using var target = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        string output = Path.GetTempFileName();
        try
        {
            form.ExportPagesToFile([0], output);
            using (var extracted = PdfDocument.Open(output)) Assert.Equal(4, extracted.GetFormFields(0).Count);
            target.InsertPagesFromFile(output, 1);
            Assert.Equal(4, target.GetFormFields(1).Count);
            var field = target.GetFormFields(1).Single(f => f.Name == "name");
            Assert.True(target.FormClick(1, field.X + 4, field.Y + 4));
            Assert.True(target.FormChar(1, 'Z'));
            target.FormKillFocus();
            target.SaveAs(output);
            using var reopened = PdfDocument.Open(output);
            Assert.Equal("Z", reopened.GetFormFieldValue(1, "name"));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void UndoingNoteDeletionRestoresStackingOrder()
    {
        using var doc = PdfDocument.Open(PixelAssert.CorpusPath("hello.pdf"));
        doc.AddNote(0, 10, 10, "A");
        doc.AddNote(0, 80, 80, "B");
        var first = doc.CaptureAnnotation(0, 0)!;
        doc.RemoveAnnotation(0, 0);
        doc.RestoreDeletedAnnotation(first, 0);
        Assert.Equal(new[] { "A", "B" }, doc.GetNotes(0).Select(n => n.Contents));
        doc.RemoveLastAnnotation(0);
        Assert.Equal("A", Assert.Single(doc.GetNotes(0)).Contents);
    }

    [Fact]
    public void SaveRejectsReplacementWithOriginalLengthAndTimestamp()
    {
        string path = Path.GetTempFileName(), replacement = Path.GetTempFileName();
        try
        {
            File.Copy(PixelAssert.CorpusPath("hello.pdf"), path, true);
            using var doc = PdfDocument.Open(path);
            var encoding = System.Text.Encoding.Latin1;
            string contents = encoding.GetString(File.ReadAllBytes(path));
            Assert.Contains("Page two", contents);
            byte[] edited = encoding.GetBytes(contents.Replace("Page two", "Page NEW"));
            File.WriteAllBytes(replacement, edited);
            File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(path));
            File.Replace(replacement, path, null);
            doc.AddNote(0, 10, 10, "local");
            Assert.False(doc.MatchesFileOnDisk());
            Assert.Throws<IOException>(doc.SaveInPlace);
            Assert.Equal(edited, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); File.Delete(replacement); }
    }

    [Fact]
    public void ConcurrentBookmarkEditsMergeInNewPageOrder()
    {
        static BookmarkEntry B(int page, string name) => new() { PageIndex = page, Name = name };
        var merged = BookmarkRemap.Merge([B(0, "removed"), B(2, "old")],
            [B(1, "added"), B(2, "remote")], [B(1, "local")],
            [p => p == 0 ? null : p - 1]);
        Assert.Equal(new[] { (0, "added"), (1, "local") }, merged.Select(b => (b.PageIndex, b.Name)));
    }
}
