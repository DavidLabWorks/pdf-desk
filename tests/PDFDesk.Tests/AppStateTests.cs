using PDFDesk.Services;

namespace PDFDesk.Tests;

public class AppStateTests
{
    [Fact]
    public void DropIntoFavoriteFolder_FilesReferencesOnceAndPreservesReadingState()
    {
        var state = new AppState();
        Guid folder = Guid.NewGuid();
        state.FavoriteFolders[folder] = "References";
        string a = Path.GetFullPath("a.pdf"), b = Path.GetFullPath("b.PDF");
        state.Remember(a, "a.pdf", 6, 1.5, 0, 0.3);
        Assert.True(state.AddDroppedFavorites([a, b, a, Path.GetFullPath("skip.txt")], folder));
        Assert.Equal(2, state.Recents.Count);
        Assert.All(state.Recents, entry => Assert.Equal(folder, entry.FavoriteFolderId));
        Assert.Equal(6, state.FindRecent(a)!.PageIndex);
        Assert.False(state.CanDropFavorite(a, folder));
        Assert.False(state.AddDroppedFavorites([a, b], folder));
        Assert.True(state.CanDropFavorite(a, Guid.Empty));
        Assert.False(state.AddDroppedFavorites([a], null));
        Assert.False(state.AddDroppedFavorites([a], Guid.NewGuid()));
        Assert.True(state.AddDroppedFavorites([a], Guid.Empty));
        Assert.Null(state.FindRecent(a)!.FavoriteFolderId);
        Assert.True(state.FindRecent(a)!.IsFavorite);
        Assert.Empty(state.Session.OpenPaths);
    }
    [Fact]
    public void FavoriteFolder_PersistsAcrossReadingUpdatesAndDeletionKeepsTheStar()
    {
        string dir = Directory.CreateTempSubdirectory("pdfdesk-favorites-").FullName;
        try
        {
            var store = new AppStateStore(dir);
            var state = store.Load();
            var folder = Guid.NewGuid();
            const string path = @"C:\doc.pdf";
            state.Remember(path, "doc.pdf", 0, 1, 0, 0);
            store.Save(state);
            var readerStore = new AppStateStore(dir);
            var reader = readerStore.Load();
            state.FavoriteFolders[folder] = "研究";
            state.Recents.Single().IsFavorite = true;
            state.Recents.Single().FavoriteFolderId = folder;
            store.Save(state);
            reader.Remember(path, "doc.pdf", 7, 1, 0, 0);
            readerStore.Save(reader);

            var reloaded = store.Load();
            Assert.Equal("研究", reloaded.FavoriteFolders[folder]);
            Assert.Equal(folder, reloaded.Recents.Single().FavoriteFolderId);
            Assert.Equal(7, reloaded.Recents.Single().PageIndex);
            reloaded.Settings.FavoriteFolderFilter = folder;
            reloaded.RemoveFavoriteFolder(folder);
            store.Save(reloaded);
            reloaded = new AppStateStore(dir).Load();
            Assert.Empty(reloaded.FavoriteFolders);
            Assert.True(reloaded.Recents.Single().IsFavorite);
            Assert.Null(reloaded.Recents.Single().FavoriteFolderId);
            Assert.Null(reloaded.Settings.FavoriteFolderFilter);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Remember_MovesFileToTopAndUpdatesPosition()
    {
        var state = new AppState();
        state.Remember(@"C:\a.pdf", "a.pdf", 1, 1.0, 0, 0.0);
        state.Remember(@"C:\b.pdf", "b.pdf", 2, 1.0, 0, 0.0);

        // Re-opening a.pdf should move it to the front and record its new page.
        state.Remember(@"C:\a.pdf", "a.pdf", 5, 1.5, 1, 0.42);

        Assert.Equal(2, state.Recents.Count);
        Assert.Equal(@"C:\a.pdf", state.Recents[0].Path);
        Assert.Equal(5, state.Recents[0].PageIndex);
        Assert.Equal(1.5, state.Recents[0].Zoom);
        Assert.Equal(1, state.Recents[0].Rotation);
        Assert.Equal(0.42, state.Recents[0].ScrollFraction);
    }

    [Fact]
    public void Remember_IsCaseInsensitiveOnPath()
    {
        var state = new AppState();
        state.Remember(@"C:\Docs\File.pdf", "File.pdf", 1, 1.0, 0, 0.0);
        state.Remember(@"c:\docs\file.pdf", "file.pdf", 3, 1.0, 0, 0.0);

        Assert.Single(state.Recents);
        Assert.Equal(3, state.Recents[0].PageIndex);
    }

    [Fact]
    public void Remember_KeepsAllOpenedFiles()
    {
        var state = new AppState();
        for (int i = 0; i < 75; i++)
        {
            state.Remember($@"C:\file{i}.pdf", $"file{i}.pdf", 0, 1.0, 0, 0.0);
        }

        Assert.Equal(75, state.Recents.Count);
        // Most recent insert is at the top.
        Assert.Equal($@"C:\file74.pdf", state.Recents[0].Path);
    }

    [Fact]
    public void FindRecent_ReturnsMatchOrNull()
    {
        var state = new AppState();
        state.Remember(@"C:\x.pdf", "x.pdf", 7, 2.0, 0, 0.1);

        Assert.Equal(7, state.FindRecent(@"C:\X.PDF")?.PageIndex);
        Assert.Null(state.FindRecent(@"C:\missing.pdf"));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsStateThroughDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfdesk-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppStateStore(dir);
            var state = new AppState();
            state.Remember(@"C:\doc.pdf", "doc.pdf", 12, 1.25, 2, 0.33, pageColumns: 3);
            state.Session = new SessionState { OpenPaths = [@"C:\doc.pdf"], ActiveIndex = 0 };
            store.Save(state);

            var reloaded = new AppStateStore(dir).Load();

            Assert.Single(reloaded.Recents);
            Assert.Equal(12, reloaded.Recents[0].PageIndex);
            Assert.Equal(1.25, reloaded.Recents[0].Zoom);
            Assert.Equal(2, reloaded.Recents[0].Rotation);
            Assert.Equal(3, reloaded.Recents[0].LayoutColumns);
            Assert.Equal([@"C:\doc.pdf"], reloaded.Session.OpenPaths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RecentFile_UsesLegacyTwoPageStateWhenColumnCountIsMissing()
    {
        var recent = System.Text.Json.JsonSerializer.Deserialize<RecentFile>("""{"Path":"old.pdf","TwoPage":true}""");

        Assert.Equal(2, recent?.LayoutColumns);
    }

    [Fact]
    public void StaleWindowSaveKeepsOtherWindowsOpensAndRemovals()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var first = new AppStateStore(dir);
            var state = first.Load();
            state.Remember("a.pdf", "a.pdf", 0, 1, 0, 0);
            first.Save(state);
            var second = new AppStateStore(dir);
            var stale = second.Load();
            state.ForgetRecent("a.pdf");
            state.Remember("b.pdf", "b.pdf", 0, 1, 0, 0);
            first.Save(state);
            stale.Remember("c.pdf", "c.pdf", 0, 1, 0, 0);
            second.Save(stale);
            Assert.Equal(["b.pdf", "c.pdf"], first.Load().Recents.Select(r => r.Path).Order());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_CorruptFile_ReturnsFreshState()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfdesk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "PDFDesk.json"), "{ this is not valid json ]");
            var state = new AppStateStore(dir).Load();
            Assert.Empty(state.Recents); // never throws; starts fresh
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Stands in for the migration deliberately not written: a value type with a
    /// property initializer keeps its default when the key is absent, because
    /// System.Text.Json only overwrites properties the file actually carries. If
    /// this ever fails, every settings bool added since needs a MigrateToolStyles
    /// entry after all.
    /// </summary>
    [Fact]
    public void SignatureRemoveBackground_DefaultsWhenAbsentFromAnOlderStateFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pdfdesk-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(
                Path.Combine(dir, "PDFDesk.json"),
                """{"Settings":{"Theme":"Dark","SignatureWidthPt":200}}""");

            var state = new AppStateStore(dir).Load();

            Assert.True(state.Settings.SignatureRemoveBackground);
            Assert.Equal("Dark", state.Settings.Theme); // the file's own keys still win
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ForgetRecent_AlsoDropsItFromTheRestoreSession()
    {
        var state = new AppState();
        state.Remember(@"C:\docs\keep.pdf", "keep", 0, 1, 0, 0);
        state.Remember(@"C:\docs\forget.pdf", "forget", 0, 1, 0, 0);
        state.Session.OpenPaths.AddRange([@"C:\docs\keep.pdf", @"C:\docs\forget.pdf"]);

        Assert.True(state.ForgetRecent(@"C:\docs\forget.pdf"));

        // Leaving it in the session would reopen it as a tab on next launch —
        // which is exactly the file the user asked to be forgotten.
        Assert.DoesNotContain(@"C:\docs\forget.pdf", state.Session.OpenPaths);
        Assert.Null(state.FindRecent(@"C:\docs\forget.pdf"));
        Assert.NotNull(state.FindRecent(@"C:\docs\keep.pdf"));
    }

    [Fact]
    public void ForgetRecent_MatchesPathsCaseInsensitively()
    {
        var state = new AppState();
        state.Remember(@"C:\docs\Report.pdf", "Report", 0, 1, 0, 0);

        // Windows paths are case-insensitive; recents must not keep a duplicate
        // alive just because the casing differs.
        Assert.True(state.ForgetRecent(@"c:\DOCS\report.PDF"));
        Assert.Empty(state.Recents);
    }

    [Fact]
    public void ForgetRecent_DiscardsBookmarks_UnlikeEviction()
    {
        var state = new AppState();
        state.Remember(@"C:\docs\book.pdf", "book", 0, 1, 0, 0);
        state.FindRecent(@"C:\docs\book.pdf")!.Bookmarks.Add(new BookmarkEntry { Name = "Chapter 2", PageIndex = 40 });

        state.ForgetRecent(@"C:\docs\book.pdf");

        // Eviction deliberately preserves bookmarked entries; an explicit
        // "forget this" must not, or the file stays on record after the user
        // asked for it to be gone.
        Assert.Empty(state.Recents);
    }

    [Fact]
    public void ForgetAllRecents_ClearsRecentsAndSession()
    {
        var state = new AppState();
        state.Remember(@"C:\a.pdf", "a", 0, 1, 0, 0);
        state.Remember(@"C:\b.pdf", "b", 0, 1, 0, 0);
        state.Session.OpenPaths.Add(@"C:\a.pdf");

        state.ForgetAllRecents();

        Assert.Empty(state.Recents);
        Assert.Empty(state.Session.OpenPaths);
    }
}
