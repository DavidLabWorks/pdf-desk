namespace PDFDesk.Engine;

/// <summary>
/// Immutable layout of page rows at a given zoom and rotation:
/// each page's rectangle in document space (DIPs), centered horizontally.
/// Rebuilt whenever zoom or rotation changes — construction is O(pages) and
/// allocation-light, so that's fine even for huge documents.
/// </summary>
public sealed class PageLayout
{
    public const double PageGap = 16;
    public const double Margin = 24;

    private readonly DipRect[] _pageRects;
    private readonly double[] _rowBottoms;

    private readonly int _columns;
    public double Zoom { get; }
    public int Rotation { get; }
    public double TotalWidth { get; }
    public double TotalHeight { get; }
    public int PageCount => _pageRects.Length;

    /// <param name="pageSizesPoints">Page sizes in PDF points.</param>
    /// <param name="zoom">DIPs per PDF point (1.0 ≈ 100% at 96 DPI).</param>
    /// <param name="rotation">View rotation in quarter turns clockwise (0–3).</param>
    /// <param name="minViewportWidth">Pages are centered within at least this width.</param>
    /// <param name="minViewportHeight">Pages are centered within at least this height, so the canvas always fills the viewport.</param>
    /// <param name="twoPage">Whether to lay out two pages per row.</param>
    public PageLayout(IReadOnlyList<(float Width, float Height)> pageSizesPoints, double zoom, int rotation, double minViewportWidth = 0, double minViewportHeight = 0, bool twoPage = false)
        : this(pageSizesPoints, zoom, rotation, minViewportWidth, minViewportHeight, twoPage ? 2 : 1)
    {
    }

    public PageLayout(IReadOnlyList<(float Width, float Height)> pageSizesPoints, double zoom, int rotation,
        double minViewportWidth, double minViewportHeight, int columns, double horizontalMargin = Margin)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columns, 3);
        _columns = columns;
        Zoom = zoom;
        Rotation = ((rotation % 4) + 4) % 4;

        _pageRects = new DipRect[pageSizesPoints.Count];
        _rowBottoms = new double[(PageCount + _columns - 1) / _columns];

        double maxWidth = 0;
        // Store each row relative to its centre, then translate into the viewport.
        double y = Margin;
        for (int i = 0; i < PageCount; i += _columns)
        {
            int end = Math.Min(i + _columns, PageCount);
            double rowWidth = 0;
            double rowHeight = 0;
            for (int page = i; page < end; page++)
            {
                double width = RotatedWidth(pageSizesPoints[page]) * zoom;
                double height = RotatedHeight(pageSizesPoints[page]) * zoom;
                if (page > i) rowWidth += PageGap;
                _pageRects[page] = new DipRect(rowWidth, y, width, height);
                rowWidth += width;
                rowHeight = Math.Max(rowHeight, height);
            }
            for (int page = i; page < end; page++)
            {
                var rect = _pageRects[page];
                _pageRects[page] = rect with { X = rect.X - rowWidth / 2 };
            }
            _rowBottoms[i / _columns] = y + rowHeight;
            maxWidth = Math.Max(maxWidth, rowWidth);
            y += rowHeight + PageGap;
        }
        TotalWidth = Math.Max(maxWidth + 2 * horizontalMargin, minViewportWidth);
        double contentHeight = pageSizesPoints.Count > 0 ? y - PageGap + Margin : 2 * Margin;

        // When the whole document is shorter than the viewport (far zoom-out),
        // grow the layout to fill it and center the pages vertically — a canvas
        // smaller than the viewport shows a mismatched-background box.
        TotalHeight = Math.Max(contentHeight, minViewportHeight);
        double shift = (TotalHeight - contentHeight) / 2;
        for (int row = 0; row < _rowBottoms.Length; row++) _rowBottoms[row] += shift;
        for (int i = 0; i < PageCount; i++)
        {
            var r = _pageRects[i];
            _pageRects[i] = r with { X = r.X + TotalWidth / 2, Y = r.Y + shift };
        }
    }

    private double RotatedWidth((float Width, float Height) size) => Rotation % 2 == 0 ? size.Width : size.Height;
    private double RotatedHeight((float Width, float Height) size) => Rotation % 2 == 0 ? size.Height : size.Width;

    public DipRect GetPageRect(int pageIndex) => _pageRects[pageIndex];

    /// <summary>Pages whose rects intersect the vertical span [top, bottom). Binary search — O(log n).</summary>
    public (int First, int Last) PagesInVerticalRange(double top, double bottom)
    {
        if (_pageRects.Length == 0 || bottom <= top)
        {
            return (0, -1);
        }

        int first = FindFirstPageBelow(top);
        int last = first;
        while (last + 1 < _pageRects.Length && _pageRects[last + 1].Y < bottom)
        {
            last++;
        }
        return _pageRects[first].Y >= bottom ? (0, -1) : (first, last);
    }

    /// <summary>The page whose vertical span contains y (or the nearest page).</summary>
    public int PageAt(double y)
    {
        if (_pageRects.Length == 0)
        {
            return 0;
        }
        return FindFirstPageBelow(y);
    }

    public double PageNavigationOffset(int page, double viewportHeight) =>
        Math.Clamp(_pageRects[page].Y - PageGap / 2, 0, Math.Max(0, TotalHeight - viewportHeight));

    public int ReadingPage(double top, double viewportHeight, int currentPage)
    {
        if (PageCount == 0) return 0;
        // Several destinations can share an offset in a spread or at the end of the document.
        if ((uint)currentPage < PageCount && Math.Abs(top - PageNavigationOffset(currentPage, viewportHeight)) <= 1)
            return currentPage;

        int first = PageAt(top + PageGap);
        return (uint)currentPage < PageCount && currentPage / _columns == first / _columns ? currentPage : first;
    }

    public int PageAt(double x, double y)
    {
        int page = PageAt(y);
        int last = Math.Min(page + _columns - 1, PageCount - 1);
        while (page < last && x >= _pageRects[page].Right + PageGap / 2) page++;
        return page;
    }

    /// <summary>Index of the first page whose bottom edge is below y.</summary>
    private int FindFirstPageBelow(double y)
    {
        int lo = 0, hi = _rowBottoms.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_rowBottoms[mid] + PageGap <= y)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo * _columns;
    }
}
