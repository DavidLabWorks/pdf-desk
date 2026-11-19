namespace PDFDesk.Engine;

/// <summary>Visible, page-clipped pieces of one continuous pointer gesture, in layout coordinates.</summary>
public sealed class PageInkGesture(PageLayout layout, double startX, double startY)
{
    public sealed record Piece(int Page, List<(double X, double Y)> Points);
    private readonly List<Piece> _pieces = [];
    public IReadOnlyList<Piece> Pieces => _pieces;
    private readonly Dictionary<int, Piece> _lastByPage = [];
    private double _x = startX, _y = startY;

    public void Append(double x, double y)
    {
        if (_x == x && _y == y) return;
        double dx = x - _x, dy = y - _y;
        var (first, last) = layout.PagesInVerticalRange(Math.Min(_y, y), Math.BitIncrement(Math.Max(_y, y)));
        for (int page = first; page <= last; page++)
        {
            var rect = layout.GetPageRect(page);
            double enter = 0, leave = 1;
            if (!ClipAxis(_x, dx, rect.X, rect.Right, ref enter, ref leave) ||
                !ClipAxis(_y, dy, rect.Y, rect.Bottom, ref enter, ref leave) || enter >= leave)
                continue;

            if (enter != 0 || !_lastByPage.TryGetValue(page, out var piece) || piece.Points[^1] != (_x, _y))
            {
                piece = new Piece(page, [(_x + enter * dx, _y + enter * dy)]);
                _pieces.Add(piece);
                _lastByPage[page] = piece;
            }
            piece.Points.Add(leave == 1 ? (x, y) : (_x + leave * dx, _y + leave * dy));
        }
        _x = x;
        _y = y;
    }

    private static bool ClipAxis(double start, double delta, double min, double max, ref double enter, ref double leave)
    {
        if (delta == 0) return start >= min && start <= max;
        double a = (min - start) / delta, b = (max - start) / delta;
        enter = Math.Max(enter, Math.Min(a, b));
        leave = Math.Min(leave, Math.Max(a, b));
        return enter < leave;
    }
}
