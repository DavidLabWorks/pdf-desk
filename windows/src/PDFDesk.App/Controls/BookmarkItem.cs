using System.ComponentModel;

namespace PDFDesk.Controls;

/// <summary>A user bookmark row in the sidebar's Bookmarks pane.</summary>
public sealed class BookmarkItem : INotifyPropertyChanged
{
    private string _name;
    private int _pageIndex;

    public BookmarkItem(int pageIndex, string name)
    {
        _pageIndex = pageIndex;
        _name = CustomName(name);
    }

    public string Name
    {
        get => HasCustomName ? _name : PageLabel;
        set
        {
            _name = CustomName(value);
            RefreshLabels();
        }
    }

    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            _pageIndex = value;
            RefreshLabels();
        }
    }

    public string PageLabel => UiText.Format("Page {0}", _pageIndex + 1);
    public bool HasCustomName => _name.Length > 0;

    private string CustomName(string value) =>
        string.IsNullOrWhiteSpace(value) || value == $"Page {_pageIndex + 1}" || value == $"第 {_pageIndex + 1} 页"
            ? string.Empty : value;

    internal void RefreshLabels() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public event PropertyChangedEventHandler? PropertyChanged;
}
