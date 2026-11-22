using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace PDFDesk.Controls;

/// <summary>
/// A recent-document card for the homepage grid. <see cref="Thumbnail"/> starts
/// null and is filled in once rendered on a background thread.
/// </summary>
public sealed class RecentCard : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    internal bool PreviewRequested { get; set; }

    public string Path { get; }
    public string DisplayName { get; }
    public string DirectoryName => System.IO.Path.GetDirectoryName(Path) ?? Path;

    private readonly string _lastOpenedForCard;
    public string Metadata => $"{_lastOpenedForCard} | {FileSize}";
    public string FileSize { get; private set; } = "—";
    internal DateTime LastOpenedUtc { get; }
    public string LastOpened { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    private bool _canSelect;
    public bool CanSelect
    {
        get => _canSelect;
        set
        {
            if (_canSelect == value) return;
            _canSelect = value;
            PropertyChanged?.Invoke(this, new(nameof(CanSelect)));
        }
    }

    private bool _isFavorite;
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            PropertyChanged?.Invoke(this, new(nameof(IsFavorite)));
            PropertyChanged?.Invoke(this, new(nameof(StarGlyph)));
            PropertyChanged?.Invoke(this, new(nameof(FavoriteAction)));
        }
    }
    public string StarGlyph => IsFavorite ? "\uE735" : "\uE734";
    public string FavoriteAction => IsFavorite ? UiText.Current.FavoriteOptions : UiText.Current.AddToFavorites;

    public RecentCard(string path, string displayName, DateTime lastOpenedUtc, bool isFavorite)
    {
        LastOpenedUtc = lastOpenedUtc;
        Path = path;
        DisplayName = displayName;
        _isFavorite = isFavorite;
        DateTime? localOpened = lastOpenedUtc == default ? null : lastOpenedUtc.ToLocalTime();
        _lastOpenedForCard = localOpened?.ToString("M/d HH:mm") ?? "—";
        LastOpened = localOpened?.ToString("g") ?? "";
    }

    public async Task LoadFileSizeAsync()
    {
        try
        {
            long bytes = await Task.Run(() => new FileInfo(Path).Length);
            FileSize = bytes switch
            {
                < 1024 => $"{bytes} B",
                < 1024 * 1024 => $"{bytes / 1024d:0.#} KB",
                < 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
                _ => $"{bytes / (1024d * 1024 * 1024):0.#} GB"
            };
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileSize)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metadata)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The recent entry can outlive its local file.
        }
    }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, ThumbnailChanged);
            PropertyChanged?.Invoke(this, HasThumbnailChanged);
            PropertyChanged?.Invoke(this, ShowPlaceholderChanged);
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Document-glyph placeholder shown until (or instead of) a thumbnail.</summary>
    public bool ShowPlaceholder => _thumbnail is null;

    private static readonly PropertyChangedEventArgs ThumbnailChanged = new(nameof(Thumbnail));
    private static readonly PropertyChangedEventArgs HasThumbnailChanged = new(nameof(HasThumbnail));
    private static readonly PropertyChangedEventArgs ShowPlaceholderChanged = new(nameof(ShowPlaceholder));
    public event PropertyChangedEventHandler? PropertyChanged;
}
