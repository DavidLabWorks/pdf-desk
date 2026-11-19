using System.Text.Json;
using System.Text.Json.Serialization;

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
}

/// <summary>What was open last time, so a restart can reopen it.</summary>
public sealed class SessionState
{
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
public sealed class AppState
{
    public List<RecentFile> Recents { get; set; } = [];
    public Dictionary<Guid, string> FavoriteFolders { get; set; } = [];

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
    public SessionState Session { get; set; } = new();
    public AppSettings Settings { get; set; } = new();

    public RecentFile? FindRecent(string path) =>
        Recents.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));

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
        Session.OpenPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    /// <summary>Forgets every recent file, and the session that referenced them.</summary>
    public void ForgetAllRecents()
    {
        Recents.Clear();
        Session.OpenPaths.Clear();
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

    public void Save(AppState state)
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
                if (disk is not null && disk != _lastJson)
                {
                    var latest = JsonSerializer.Deserialize<AppState>(disk, Options);
                    if (latest is not null)
                        MergeRecents(state, latest);
                }
                string json = JsonSerializer.Serialize(state, Options);
                File.WriteAllText(_path + ".tmp", json);
                File.Move(_path + ".tmp", _path, overwrite: true);
                _lastJson = json;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write("Save user state", ex);
        }
    }

    private void MergeRecents(AppState state, AppState latest)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var previous = _lastJson is null ? null : JsonSerializer.Deserialize<AppState>(_lastJson, Options);
        var oldFolders = previous?.FavoriteFolders ?? [];
        foreach (var deleted in oldFolders.Keys.Except(state.FavoriteFolders.Keys))
            latest.FavoriteFolders.Remove(deleted);
        foreach (var (id, name) in state.FavoriteFolders)
            if (!oldFolders.TryGetValue(id, out var oldName) || (oldName != name && latest.FavoriteFolders.ContainsKey(id)))
                latest.FavoriteFolders[id] = name;
        state.FavoriteFolders = latest.FavoriteFolders;
        var before = (previous?.Recents ?? []).ToDictionary(r => r.Path, comparer);
        var local = state.Recents.ToDictionary(r => r.Path, comparer);
        var merged = latest.Recents.ToDictionary(r => r.Path, comparer);
        foreach (string removed in before.Keys.Except(local.Keys, comparer))
            merged.Remove(removed);
        foreach (var row in local.Values)
        {
            if (before.TryGetValue(row.Path, out var baseline) && merged.TryGetValue(row.Path, out var saved))
            {
                // A reading-position save must preserve a star changed in another window.
                bool localFavoriteChanged = row.IsFavorite != baseline.IsFavorite || row.FavoriteFolderId != baseline.FavoriteFolderId;
                bool favorite = localFavoriteChanged ? row.IsFavorite : saved.IsFavorite;
                var folder = localFavoriteChanged ? row.FavoriteFolderId : saved.FavoriteFolderId;
                baseline.IsFavorite = row.IsFavorite;
                baseline.FavoriteFolderId = row.FavoriteFolderId;
                if (JsonSerializer.Serialize(baseline, Options) == JsonSerializer.Serialize(row, Options))
                {
                    saved.IsFavorite = favorite;
                    saved.FavoriteFolderId = folder;
                    continue;
                }
                row.IsFavorite = favorite;
                row.FavoriteFolderId = folder;
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
