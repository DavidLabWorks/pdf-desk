using PDFDesk.Services;

namespace PDFDesk.Tests;

public class AppStateTests
{
    [Fact]
    public void LegacyWindowsMergeProgressWithConcurrentAddCloseAndPromotion()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "PDFDesk.json"),
                """{"Session":{"OpenPaths":["main.pdf"]},"AdditionalWindows":[{"OpenPaths":["a.pdf"]},{"OpenPaths":["closed.pdf"]}]}""");
            var first = new AppStateStore(dir.FullName);
            var second = new AppStateStore(dir.FullName);
            var a = first.Load();
            var b = second.Load();
            var primary = a.Session;
            a.Session = a.AdditionalWindows[0];
            a.AdditionalWindows = [primary, new() { OpenPaths = ["new.pdf"] }];
            Assert.True(first.Save(a));
            b.AdditionalWindows[0].Positions["a.pdf"] = new(7, 1, 0, 0, true, 1);
            Assert.True(second.Save(b));
            var result = first.Load();
            Assert.Equal("a.pdf", Assert.Single(result.Session.OpenPaths));
            Assert.Equal(7, result.Session.Positions["a.pdf"].PageIndex);
            Assert.Equal(new[] { "main.pdf", "new.pdf" }, result.AdditionalWindows.Select(w => Assert.Single(w.OpenPaths)));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void TwoStoresRetainIndependentSessionPositions()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var a = new AppStateStore(dir.FullName);
            var b = new AppStateStore(dir.FullName);
            var local = a.Load();
            local.Session.OpenPaths = ["a.pdf", "b.pdf"];
            var start = new TabPosition(0, 1, 0, 0, true, 1);
            local.Session.Positions["a.pdf"] = local.Session.Positions["b.pdf"] = start;
            Assert.True(a.Save(local));
            var remote = b.Load();
            local.Session.Positions["a.pdf"] = start with { PageIndex = 3 };
            remote.Session.Positions["b.pdf"] = start with { PageIndex = 7 };
            Assert.True(a.Save(local));
            Assert.True(b.Save(remote));
            var result = a.Load().Session;
            Assert.Equal(3, result.Positions["a.pdf"].PageIndex);
            Assert.Equal(7, result.Positions["b.pdf"].PageIndex);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void FailedMergeDoesNotTurnRemoteSettingsIntoLocalEdits()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var a = new AppStateStore(dir.FullName);
            var b = new AppStateStore(dir.FullName);
            var local = a.Load();
            Assert.True(a.Save(local));
            var remote = b.Load();
            local.Settings.Theme = "Dark";
            Assert.True(a.Save(local));
            remote.Settings.RecentUsesList = true;
            var blocked = dir.CreateSubdirectory("PDFDesk.json.tmp");
            Assert.False(b.Save(remote));
            blocked.Delete();
            local.Settings.Theme = "Light";
            Assert.True(a.Save(local));
            Assert.True(b.Save(remote));
            var result = a.Load().Settings;
            Assert.Equal("Light", result.Theme);
            Assert.True(result.RecentUsesList);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void InvalidRowsAreDroppedWithoutLosingValidSessionAndRecents()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<AppState>(
            """{"Recents":[null,{"Path":"book.pdf"}],"AdditionalWindows":null,"Session":{"OpenPaths":[null,"book.pdf"],"ActiveIndex":1}}""")!;
        Assert.Equal("book.pdf", Assert.Single(state.Recents).Path);
        Assert.Equal("book.pdf", Assert.Single(state.Session.OpenPaths));
        Assert.Equal(0, state.Session.ActiveIndex);
        Assert.Empty(state.AdditionalWindows);
    }

    [Fact]
    public void FolderCommitRejectsAConcurrentDuplicateAndDeletedTarget()
    {
        var state = new AppState();
        Assert.True(state.TrySetFolderName(null, "Reports", out var reports));
        Assert.False(state.TrySetFolderName(null, " reports ", out _));
        Assert.True(state.TrySetFolderName(null, "Notes", out var notes));
        Assert.False(state.TrySetFolderName(notes, "Reports", out _));
        state.RemoveFavoriteFolder(reports);
        Assert.False(state.TrySetFolderName(reports, "Renamed", out _));
        Assert.Equal("Notes", Assert.Single(state.FavoriteFolders).Value);
    }

    [Fact]
    public void NullRecentsPreservesOtherSettingsAndAcceptsNewDocuments()
    {
        var state = System.Text.Json.JsonSerializer.Deserialize<AppState>(
            """{"Recents":null,"Session":{"ActiveIndex":2}}""")!;
        Assert.Empty(state.Recents);
        Assert.Equal(2, state.Session.ActiveIndex);
        state.Remember("book.pdf", "book", 1, 1, 0, 0);
        Assert.Equal("book.pdf", Assert.Single(state.Recents).Path);
    }

    [Fact]
    public void WindowSessionsRestoreSeparatePositionsForTheSameFile()
    {
        const string path = "sample.pdf";
        var state = System.Text.Json.JsonSerializer.Deserialize<AppState>(
            """{"Session":{"OpenPaths":["sample.pdf"],"ActiveIndex":0}}""")!;
        state.Remember(path, path, 4, 1, 0, 0.2);
        Assert.Empty(state.AdditionalWindows);
        Assert.Equal(4, state.Session.RestorePosition(path, state.FindRecent(path))!.PageIndex);
        state.Session.Positions[path] = new(12, 1.5, 90, 0.3, false, 2);
        state.AdditionalWindows.Add(new() { OpenPaths = [path], Positions = { [path] = new(75, 2, 0, 0.8, true, 1) } });
        var copy = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(state))!;
        Assert.Equal(12, copy.Session.RestorePosition(path, null)!.PageIndex);
        Assert.Equal(75, copy.AdditionalWindows.Single().RestorePosition(path, null)!.PageIndex);
        Assert.Single(copy.Recents);
    }

    [Fact]
    public void RelocatingAndForgettingFilesUpdatesEveryWindowIndependently()
    {
        var state = new AppState();
        state.AdditionalWindows.Add(new());
        int page = 4;
        foreach (var window in state.AdditionalWindows.Prepend(state.Session))
        {
            window.OpenPaths.AddRange(["first.pdf", "book.pdf"]);
            window.ActiveIndex = 1;
            window.Positions["book.pdf"] = new(page++, 1, 0, 0.2, true, 1);
        }
        state = System.Text.Json.JsonSerializer.Deserialize<AppState>(System.Text.Json.JsonSerializer.Serialize(state))!;
        state.RelocateSessionPath("BOOK.pdf", "new.pdf");
        state.ForgetRecent("FIRST.pdf");
        Assert.Equal(4, state.Session.RestorePosition("NEW.pdf", null)!.PageIndex);
        Assert.Equal(5, state.AdditionalWindows.Single().RestorePosition("NEW.pdf", null)!.PageIndex);
        foreach (var window in state.AdditionalWindows.Prepend(state.Session))
        {
            Assert.Equal("new.pdf", Assert.Single(window.OpenPaths));
            Assert.Equal(0, window.ActiveIndex);
            Assert.False(window.Positions.ContainsKey("book.pdf"));
        }
        state.ForgetAllRecents();
        foreach (var window in state.AdditionalWindows.Prepend(state.Session))
        {
            Assert.Empty(window.Positions);
            Assert.Empty(window.OpenPaths);
            Assert.Equal(-1, window.ActiveIndex);
        }
    }

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
    [Fact]
    public void IndependentStoresMergePositionSettingsAndBookmarks()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var first = new AppStateStore(dir.FullName);
            var second = new AppStateStore(dir.FullName);
            var a = first.Load();
            const string path = "sample.pdf";
            a.Remember(path, "sample", 0, 1, 0, 0);
            Assert.True(first.Save(a));
            var b = second.Load();
            a.Recents[0].StoreBookmarks("version", [new() { PageIndex = 1, Name = "First" }], true);
            a.Settings.RestoreSession = false;
            Assert.True(first.Save(a));
            b.Remember(path, "sample", 4, 1, 0, 0);
            b.Settings.Theme = "Dark";
            Assert.True(second.Save(b));
            var saved = first.Load();
            Assert.False(saved.Settings.RestoreSession);
            Assert.Equal("Dark", saved.Settings.Theme);
            Assert.Equal(4, saved.Recents[0].PageIndex);
            Assert.Equal("First", Assert.Single(saved.Recents[0].BookmarksFor("version")).Name);
            saved.Recents[0].Bookmarks[0].Name = "Renamed";
            b.Recents[0].Bookmarks.Add(new() { PageIndex = 2, Name = "Second" });
            Assert.True(first.Save(saved));
            Assert.True(second.Save(b));
            Assert.Equal(new[] { "Renamed", "Second" }, first.Load().Recents[0].Bookmarks.Select(x => x.Name));
        }
        finally { dir.Delete(true); }
    }
}
