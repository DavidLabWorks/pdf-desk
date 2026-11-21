using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;

namespace PDFDesk.Services;

/// <summary>A user bookmark inside one document.</summary>
public sealed class BookmarkEntry
{
    public int PageIndex { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>A file the user has opened, with the reading position to restore.</summary>
public sealed class RecentFile
{
    public required string Path { get; set; }
    public string DisplayName { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }
    public DateTime? AddedUtc { get; set; }
    public bool SidebarOpen { get; set; } = true;
    public bool TwoPage { get; set; }
    public int? PageColumns { get; set; }

    [JsonIgnore]
    public int LayoutColumns => PageColumns is >= 1 and <= 3 ? PageColumns.Value : TwoPage ? 2 : 1;
    public bool IsLibraryCopy { get; set; }
    public bool IsFavorite { get; set; }
    public Guid? FavoriteFolderId { get; set; }
    public int PageIndex { get; set; }
    public double Zoom { get; set; }
    public int Rotation { get; set; }
    public double ScrollFraction { get; set; }

    /// <summary>User bookmarks (Ctrl+B); old state files deserialize to empty.</summary>
    public List<BookmarkEntry> Bookmarks { get; set; } = [];
    public string? BookmarkDigest { get; set; }
    public Dictionary<string, List<BookmarkEntry>> OtherVersionBookmarks { get; set; } = [];

    public IReadOnlyList<BookmarkEntry> BookmarksFor(string digest) =>
        BookmarkDigest is null || BookmarkDigest == digest ? Bookmarks : OtherVersionBookmarks.GetValueOrDefault(digest) ?? [];

    public void StoreBookmarks(string digest, List<BookmarkEntry> bookmarks, bool saved)
    {
        if (saved || BookmarkDigest is null || BookmarkDigest == digest)
        {
            if (BookmarkDigest is { } previous && previous != digest) OtherVersionBookmarks[previous] = Bookmarks;
            BookmarkDigest = digest;
            Bookmarks = bookmarks;
            OtherVersionBookmarks.Remove(digest);
        }
        else OtherVersionBookmarks[digest] = bookmarks;
    }
}

/// <summary>What was open last time, so a restart can reopen it.</summary>
public sealed record TabPosition(int PageIndex, double Zoom, int Rotation, double ScrollFraction, bool SidebarOpen, int PageColumns)
{
    public RecentFile ForPath(string path) => new()
    {
        Path = path, PageIndex = PageIndex, Zoom = Zoom, Rotation = Rotation,
        ScrollFraction = ScrollFraction, SidebarOpen = SidebarOpen, PageColumns = PageColumns
    };

    public static TabPosition From(RecentFile entry) => new(entry.PageIndex, entry.Zoom, entry.Rotation,
        entry.ScrollFraction, entry.SidebarOpen, entry.LayoutColumns);
}

public sealed class SessionState : IJsonOnDeserializing
{
    public string WindowId { get; set; } = Guid.NewGuid().ToString("N");
    void IJsonOnDeserializing.OnDeserializing() => WindowId = "";

    private Dictionary<string, TabPosition> _positions = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TabPosition> Positions
    {
        get => _positions;
        set
        {
            _positions = new(StringComparer.OrdinalIgnoreCase);
            if (value != null)
                foreach (var pair in value) _positions[pair.Key] = pair.Value;
        }
    }
    public RecentFile? RestorePosition(string path, RecentFile? recent) =>
        Positions.TryGetValue(path, out var position) ? position.ForPath(path) : recent;

    public bool? FavoritesSelected { get; set; }
    public Guid? FolderFilter { get; set; }
    public List<string> ReadOnlyPaths { get; set; } = [];
    public List<string> OpenPaths { get; set; } = [];
    public int ActiveIndex { get; set; }
}

/// <summary>User preferences, persisted with the rest of the app state.</summary>
public sealed class AppSettings
{
    /// <summary>"System", "Light", or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public string? LibraryFolder { get; set; }

    public string Language { get; set; } = "System";

    public bool RecentUsesList { get; set; }
    public bool FavoritesUseList { get; set; }
    public bool FavoritesTabSelected { get; set; }
    // null shows every favorite; Guid.Empty shows only unfiled documents.
    public Guid? FavoriteFolderFilter { get; set; }

    /// <summary>Invert page colors (night reading mode).</summary>
    public bool NightMode { get; set; }

    /// <summary>Reopen last session's tabs on launch.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Keep the window and documents alive in the notification area when closed.</summary>
    public bool CloseToTray { get; set; } = true;
    public bool SuppressDefaultPdfReminder { get; set; }

    // AutoCheckUpdates / LastUpdateCheckUtc used to live here. PDFDesk no longer
    // updates itself — the Store does — so both are gone. Existing state.json
    // files still carry the keys; System.Text.Json ignores unknown properties,
    // so no migration is needed.

    /// <summary>Ink pen color as #RRGGBB (default red).</summary>
    /// <remarks>
    /// Superseded by <see cref="Pen"/>. Kept so an existing state.json still
    /// carries the user's chosen pen forward — see <see cref="MigrateToolStyles"/>.
    /// </remarks>
    public string InkColor { get; set; } = "#E22222";

    /// <summary>Ink pen width in points. Superseded by <see cref="Pen"/>.</summary>
    public double InkWidth { get; set; } = 2.5;

    /// <summary>Freehand pen style.</summary>
    public ToolStyle? Pen { get; set; }

    /// <summary>Text-markup style: which mark, its colour, and its opacity.</summary>
    public ToolStyle? Highlighter { get; set; }

    public const double DefaultEraserWidthPt = 16;
    public double EraserWidthPt { get; set; } = DefaultEraserWidthPt;

    /// <summary>Width in points a click-placed signature uses; remembered from the last placement.</summary>
    public double SignatureWidthPt { get; set; } = 180;

    /// <summary>
    /// The same for a placed picture, kept apart from the signature's because
    /// the two are wanted at very different sizes. Zero means "not chosen yet",
    /// which is what makes the first placement size itself from the image.
    /// </summary>
    public double ImageWidthPt { get; set; }

    /// <summary>
    /// Key the paper out of an imported signature photo, so only the ink lands
    /// on the page. On by default: a photo of a signature is the common import,
    /// and without it the paper stamps as an opaque rectangle.
    /// </summary>
    /// <remarks>
    /// No <see cref="MigrateToolStyles"/> entry, deliberately. That method exists
    /// only because Pen and Highlighter are nullable REFERENCE types, which
    /// deserialize to null when the key is absent and would then NRE. A bool with
    /// a property initializer just keeps its default, because System.Text.Json
    /// only overwrites a property when the key is present — the same reason
    /// <see cref="SignatureWidthPt"/> needs no migration either.
    /// </remarks>
    public bool SignatureRemoveBackground { get; set; } = true;

    /// <summary>How the last text box on a page was styled, so the next one matches.</summary>
    public TextBoxSettings? TextBox { get; set; }

    /// <summary>
    /// Fills in the per-tool styles the first time a pre-v0.6 state file is
    /// loaded, seeding the pen from the old flat InkColor/InkWidth so a user's
    /// chosen pen isn't silently reset.
    /// </summary>
    public void MigrateToolStyles()
    {
        Pen ??= new ToolStyle { Color = InkColor, Size = InkWidth, Opacity = 1.0 };
        Highlighter ??= new ToolStyle { Color = "#FFD200", Size = 0, Opacity = 0.4, Markup = "Highlight" };
    }
}

/// <summary>
/// One annotation tool's remembered style. A class rather than a record so it
/// round-trips through the plain-POCO JSON the rest of the settings use, and so
/// a missing key in an older file deserializes to null (see
/// <see cref="AppSettings.MigrateToolStyles"/>) rather than throwing.
/// </summary>
/// <summary>
/// The look of a text box typed onto a page. Stored as plain fields rather than
/// the engine's <c>TextBoxStyle</c> so the settings file stays a settings file:
/// a rename in the model must not silently invalidate what people have saved.
/// </summary>
public sealed class TextBoxSettings
{
    /// <summary>Name of a <c>PdfStandardFont</c>; anything unrecognised falls back to Helvetica.</summary>
    public string Font { get; set; } = "Helvetica";

    public double Size { get; set; } = 14;
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    /// <summary>Name of a <c>TextAlign</c>; anything unrecognised falls back to Left.</summary>
    public string Align { get; set; } = nameof(Engine.TextAlign.Left);
}

public sealed class ToolStyle
{
    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#E22222";

    /// <summary>Stroke width in points. Unused by tools that don't draw a stroke.</summary>
    public double Size { get; set; } = 2.5;

    /// <summary>0–1. The engine has always accepted a full alpha byte; only the viewer hardcoded it.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>For the markup tool: "Highlight", "Underline" or "Strikeout".</summary>
    public string Markup { get; set; } = "Highlight";
}

/// <summary>The whole persisted app state (one JSON file).</summary>
public sealed class AppState : IJsonOnDeserialized
{
    private List<RecentFile> _recents = [];
    public List<RecentFile> Recents
    {
        get => _recents;
        set => _recents = value ?? [];
    }
    public Dictionary<Guid, string> FavoriteFolders { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        Recents.RemoveAll(r => string.IsNullOrWhiteSpace(r?.Path));
        AdditionalWindows ??= [];
        AdditionalWindows.RemoveAll(w => w is null);
        FavoriteFolders ??= [];
        Settings ??= new();
        Session ??= new();
        var windowIds = new HashSet<string>();
        int slot = 0;
        foreach (var session in AdditionalWindows.Prepend(Session))
        {
            // Older files contain an ordered list with no IDs. Independent readers of
            // the same file must assign the same IDs until the next successful save.
            if (string.IsNullOrEmpty(session.WindowId) || !windowIds.Add(session.WindowId))
            {
                do { session.WindowId = $"legacy-{slot++}"; } while (!windowIds.Add(session.WindowId));
            }
            session.OpenPaths ??= [];
            string? active = session.OpenPaths.ElementAtOrDefault(session.ActiveIndex);
            if (session.OpenPaths.RemoveAll(string.IsNullOrWhiteSpace) > 0)
                session.ActiveIndex = session.OpenPaths.IndexOf(active ?? "");
            foreach (var path in session.Positions.Where(p => p.Value == null).Select(p => p.Key).ToList())
                session.Positions.Remove(path);
        }
        foreach (var row in Recents)
        {
            row.Bookmarks ??= [];
            row.Bookmarks.RemoveAll(b => b is null);
            row.OtherVersionBookmarks ??= [];
            foreach (var (key, entries) in row.OtherVersionBookmarks.ToArray())
            {
                if (entries is null) row.OtherVersionBookmarks.Remove(key);
                else entries.RemoveAll(b => b is null);
            }
        }
    }

    public bool FolderNameTaken(Guid? existing, string name) =>
        FavoriteFolders.Any(p => p.Key != existing && string.Equals(p.Value, name, StringComparison.OrdinalIgnoreCase));

    public bool TrySetFolderName(Guid? existing, string name, out Guid folder)
    {
        folder = existing ?? Guid.NewGuid();
        name = name.Trim();
        if (existing.HasValue && !FavoriteFolders.ContainsKey(folder) || name.Length == 0 || FolderNameTaken(existing, name))
            return false;
        FavoriteFolders[folder] = name;
        return true;
    }

    public bool CanDropFavorite(string path, Guid? destination)
    {
        if (destination is Guid id && id != Guid.Empty && !FavoriteFolders.ContainsKey(id)) return false;
        if (!System.IO.Path.IsPathFullyQualified(path) ||
            !string.Equals(System.IO.Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase)) return false;
        var entry = FindRecent(path);
        return entry?.IsFavorite != true || (destination.HasValue && (entry.FavoriteFolderId ?? Guid.Empty) != destination);
    }

    public bool AddDroppedFavorites(IEnumerable<string> paths, Guid? destination)
    {
        bool changed = false;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!CanDropFavorite(path, destination)) continue;
            var entry = FindRecent(path);
            if (entry is null)
            {
                Remember(path, System.IO.Path.GetFileName(path), 0, 1, 0, 0);
                entry = Recents[0];
            }
            // The aggregate view accepts new favorites without moving existing ones.
            Guid? folder = destination == Guid.Empty ? null : destination ?? entry.FavoriteFolderId;
            entry.IsFavorite = true;
            entry.FavoriteFolderId = folder;
            changed = true;
        }
        return changed;
    }

    public void RemoveFavoriteFolder(Guid id)
    {
        FavoriteFolders.Remove(id);
        foreach (var row in Recents.Where(row => row.FavoriteFolderId == id))
            row.FavoriteFolderId = null;
        if (Settings.FavoriteFolderFilter == id) Settings.FavoriteFolderFilter = null;
    }
    public List<SessionState> AdditionalWindows { get; set; } = [];
    public SessionState Session { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public RecentFile? FindRecent(string path) =>
        Recents.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

    public void StoreCopyBookmarks(string path, string digest, List<BookmarkEntry> bookmarks)
    {
        var entry = FindRecent(path);
        if (entry is null)
        {
            Remember(path, System.IO.Path.GetFileName(path), 0, 0, 0, 0);
            entry = FindRecent(path)!;
        }
        entry.StoreBookmarks(digest, bookmarks, saved: true);
    }

    /// <summary>Records/updates a file's position and moves it to the top of recents.</summary>
    public void Remember(string path, string displayName, int pageIndex, double zoom, int rotation, double scrollFraction,
        bool? twoPage = null, bool? sidebarOpen = null, int? pageColumns = null)
    {
        var existing = FindRecent(path);
        var now = DateTime.UtcNow;
        int columns = pageColumns ?? (twoPage is bool paired ? (paired ? 2 : 1) : existing?.LayoutColumns ?? 1);
        Recents.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        Recents.Insert(0, new RecentFile
        {
            Path = path,
            DisplayName = displayName,
            LastOpenedUtc = now,
            AddedUtc = existing is null ? now : existing.AddedUtc,
            PageIndex = pageIndex,
            Zoom = zoom,
            Rotation = rotation,
            ScrollFraction = scrollFraction,
            SidebarOpen = sidebarOpen ?? existing?.SidebarOpen ?? true,
            TwoPage = columns >= 2,
            PageColumns = columns,
            IsLibraryCopy = existing?.IsLibraryCopy ?? false,
            Bookmarks = existing?.Bookmarks ?? [],
            BookmarkDigest = existing?.BookmarkDigest,
            OtherVersionBookmarks = existing?.OtherVersionBookmarks ?? [],
            IsFavorite = existing?.IsFavorite ?? false,
            FavoriteFolderId = existing?.FavoriteFolderId,
        });
    }

    /// <summary>
    /// Forgets one file: drops it from recents and from the restore session, so
    /// it does not reappear as a tab on the next launch. Returns true if there
    /// was anything to remove.
    ///
    /// </summary>
    public bool ForgetRecent(string path)
    {
        int removed = Recents.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        foreach (var session in AdditionalWindows.Prepend(Session))
        {
            string? active = session.OpenPaths.ElementAtOrDefault(session.ActiveIndex);
            session.OpenPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            session.Positions.Remove(path);
            session.ActiveIndex = session.OpenPaths.FindIndex(p => string.Equals(p, active, StringComparison.OrdinalIgnoreCase));
        }
        return removed > 0;
    }

    public void RelocateSessionPath(string oldPath, string newPath)
    {
        foreach (var session in AdditionalWindows.Prepend(Session))
        {
            for (int i = 0; i < session.OpenPaths.Count; i++)
                if (string.Equals(session.OpenPaths[i], oldPath, StringComparison.OrdinalIgnoreCase))
                    session.OpenPaths[i] = newPath;
            if (session.Positions.Remove(oldPath, out var position))
                session.Positions[newPath] = position;
        }
    }

    /// <summary>Forgets every recent file, and the session that referenced them.</summary>
    public void ForgetAllRecents()
    {
        Recents.Clear();
        var windowIds = new HashSet<string>();
        int slot = 0;
        foreach (var session in AdditionalWindows.Prepend(Session))
        {
            // Older files contain an ordered list with no IDs. Independent readers of
            // the same file must assign the same IDs until the next successful save.
            if (string.IsNullOrEmpty(session.WindowId) || !windowIds.Add(session.WindowId))
            {
                do { session.WindowId = $"legacy-{slot++}"; } while (!windowIds.Add(session.WindowId));
            }
            session.OpenPaths.Clear();
            session.Positions.Clear();
            session.ActiveIndex = -1;
        }
    }


}

/// <summary>
/// Loads/saves <see cref="AppState"/> as JSON in the PDFDesk folder under Documents.
/// A corrupt or missing file yields a fresh empty state — never throws to the
/// caller, since losing the recents list must not stop the app from opening.
/// </summary>
public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private string? _lastJson;

    public string LibraryDirectory { get; }

    public AppStateStore(string? directory = null)
    {
        string dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PDFDesk");
        Directory.CreateDirectory(dir);
        LibraryDirectory = Path.Combine(dir, "Library");
        _path = Path.Combine(dir, "PDFDesk.json");
    }

    public AppState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _lastJson = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppState>(_lastJson, Options) ?? new AppState();
                // Older files predate the per-tool styles; fill them in from
                // the flat ink settings rather than resetting the user's pen.
                loaded.Settings.MigrateToolStyles();
                return loaded;
            }
        }
        catch
        {
            // Corrupt state file: start fresh rather than block startup.
        }
        var fresh = new AppState();
        fresh.Settings.MigrateToolStyles();
        return fresh;
    }

    public bool Save(AppState state)
    {
        try
        {
            // Serialize concurrent windows' read/merge/replace operations.
            using var mutex = new Mutex(false, "PDFDeskState-" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant()))));
            try { mutex.WaitOne(); }
            catch (AbandonedMutexException) { /* The previous owner exited. */ }
            try
            {
                string? disk = File.Exists(_path) ? File.ReadAllText(_path) : null;
                var candidate = state;
                if (disk is not null && disk != _lastJson &&
                    JsonSerializer.Deserialize<AppState>(disk, Options) is { } latest)
                {
                    candidate = JsonSerializer.SerializeToNode(state, Options)!.Deserialize<AppState>(Options)!;
                    MergeState(candidate, latest);
                }
                string json = JsonSerializer.Serialize(candidate, Options);
                File.WriteAllText(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, overwrite: true);
                if (candidate != state)
                {
                    state.Settings = candidate.Settings;
                    state.Recents = candidate.Recents;
                    state.FavoriteFolders = candidate.FavoriteFolders;
                    state.Session = candidate.Session;
                    state.AdditionalWindows = candidate.AdditionalWindows;
                }
                _lastJson = json;
                return true;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write("Save user state", ex);
            return false;
        }
    }

    // Apply only the properties this instance changed since its last read.
    private static T MergeFields<T>(T baseline, T local, T disk) where T : class
    {
        static JsonNode? Merge(JsonNode? before, JsonNode? current, JsonNode? latest)
        {
            if (JsonNode.DeepEquals(before, current)) return latest?.DeepClone();
            if (current is JsonObject localObject && latest is JsonObject savedObject)
            {
                var oldObject = before as JsonObject;
                var result = (JsonObject)savedObject.DeepClone();
                foreach (string key in localObject.Select(p => p.Key).Union(oldObject?.Select(p => p.Key) ?? []))
                {
                    if (!localObject.ContainsKey(key)) result.Remove(key);
                    else if (!JsonNode.DeepEquals(oldObject?[key], localObject[key]))
                        result[key] = Merge(oldObject?[key], localObject[key], savedObject[key]);
                }
                return result;
            }
            return current?.DeepClone();
        }

        return Merge(JsonSerializer.SerializeToNode(baseline), JsonSerializer.SerializeToNode(local),
            JsonSerializer.SerializeToNode(disk))!.Deserialize<T>()!;
    }

    private static Dictionary<string, Dictionary<int, string>> BookmarkVersions(RecentFile row)
    {
        static Dictionary<int, string> Index(IEnumerable<BookmarkEntry> entries) =>
            entries.GroupBy(entry => entry.PageIndex).ToDictionary(group => group.Key, group => group.Last().Name);
        var versions = row.OtherVersionBookmarks.ToDictionary(pair => pair.Key, pair => Index(pair.Value));
        versions[row.BookmarkDigest ?? ""] = Index(row.Bookmarks);
        return versions;
    }

    private static List<BookmarkEntry> Entries(Dictionary<int, string>? pages) =>
        pages?.OrderBy(pair => pair.Key).Select(pair => new BookmarkEntry { PageIndex = pair.Key, Name = pair.Value }).ToList() ?? [];

    private static SessionState MergeSession(SessionState before, SessionState local, SessionState disk)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var merged = MergeFields(before, local, disk);
        // A tab position is a single observation; keep its zoom, page and scroll together.
        var positions = new Dictionary<string, TabPosition>(disk.Positions, comparer);
        foreach (var path in before.Positions.Keys.Except(local.Positions.Keys, comparer))
            positions.Remove(path);
        foreach (var (path, position) in local.Positions)
            if (before.Positions.GetValueOrDefault(path) != position) positions[path] = position;
        merged.Positions = positions;

        var selection = local.ActiveIndex != before.ActiveIndex || !local.OpenPaths.SequenceEqual(before.OpenPaths, comparer)
            ? local : disk;
        string? activePath = selection.OpenPaths.ElementAtOrDefault(selection.ActiveIndex);
        merged.ActiveIndex = activePath is null ? -1 : merged.OpenPaths.FindIndex(path => comparer.Equals(path, activePath));
        return merged;
    }

    private static void MergeWindows(AppState before, AppState local, AppState disk)
    {
        static List<SessionState> Windows(AppState state) => [state.Session, .. state.AdditionalWindows];
        var original = Windows(before);
        var current = Windows(local);
        var latest = Windows(disk);
        var baseline = original.ToDictionary(w => w.WindowId);
        var saved = latest.ToDictionary(w => w.WindowId);
        var edits = current.ToDictionary(w => w.WindowId);
        var localOrder = current.Select(w => w.WindowId);
        var diskOrder = latest.Select(w => w.WindowId);
        var order = localOrder.SequenceEqual(original.Select(w => w.WindowId))
            ? diskOrder.Union(localOrder) : localOrder.Union(diskOrder);
        var merged = new List<SessionState>();
        foreach (var id in order)
        {
            var here = edits.GetValueOrDefault(id);
            var there = saved.GetValueOrDefault(id);
            if (!baseline.TryGetValue(id, out var old))
                merged.Add(here ?? there!);
            else if (here != null && there != null)
                merged.Add(MergeSession(old, here, there));
        }
        local.Session = merged.FirstOrDefault() ?? new SessionState { ActiveIndex = -1 };
        local.AdditionalWindows = merged.Skip(1).ToList();
    }

    private void MergeState(AppState state, AppState latest)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var previous = _lastJson is null ? null : JsonSerializer.Deserialize<AppState>(_lastJson, Options);
        previous ??= new AppState();
        previous.Settings.MigrateToolStyles();
        latest.Settings.MigrateToolStyles();
        state.Settings = MergeFields(previous.Settings, state.Settings, latest.Settings);
        MergeWindows(previous, state, latest);
        var oldFolders = previous.FavoriteFolders;
        foreach (var deleted in oldFolders.Keys.Except(state.FavoriteFolders.Keys))
            latest.FavoriteFolders.Remove(deleted);
        foreach (var (id, name) in state.FavoriteFolders)
            if (!oldFolders.TryGetValue(id, out var oldName) || (oldName != name && latest.FavoriteFolders.ContainsKey(id)))
                latest.FavoriteFolders[id] = name;
        state.FavoriteFolders = latest.FavoriteFolders;
        Dictionary<string, RecentFile> IndexRecents(IEnumerable<RecentFile> rows) =>
            rows.Where(r => !string.IsNullOrWhiteSpace(r?.Path))
                .OrderByDescending(r => r.LastOpenedUtc)
                .DistinctBy(r => r.Path, comparer)
                .ToDictionary(r => r.Path, comparer);
        var before = IndexRecents(previous?.Recents ?? []);
        var local = IndexRecents(state.Recents);
        var merged = IndexRecents(latest.Recents);
        foreach (string removed in before.Keys.Except(local.Keys, comparer))
            merged.Remove(removed);
        foreach (var row in local.Values)
        {
            if (merged.TryGetValue(row.Path, out var saved))
            {
                var baseline = before.GetValueOrDefault(row.Path) ?? new RecentFile { Path = row.Path };
                var result = MergeFields(baseline, row, saved);
                var versions = MergeFields(BookmarkVersions(baseline), BookmarkVersions(row), BookmarkVersions(saved));
                string primary = result.BookmarkDigest ?? "";
                result.Bookmarks = Entries(versions.GetValueOrDefault(primary));
                versions.Remove(primary);
                result.OtherVersionBookmarks = versions.Where(pair => pair.Key.Length > 0)
                    .ToDictionary(pair => pair.Key, pair => Entries(pair.Value));
                merged[row.Path] = result;
                continue;
            }
            if (!before.TryGetValue(row.Path, out var old) ||
                JsonSerializer.Serialize(old, Options) != JsonSerializer.Serialize(row, Options))
                merged[row.Path] = row;
        }
        state.Recents = merged.Values.OrderByDescending(r => r.LastOpenedUtc).ToList();
        foreach (var row in state.Recents)
            if (row.FavoriteFolderId is Guid id && !state.FavoriteFolders.ContainsKey(id))
                row.FavoriteFolderId = null;
    }
}
