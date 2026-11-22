using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace PDFDesk.Controls;

public sealed class ShelfGridView : GridView
{
    private readonly double _cardWidth = (double)Application.Current.Resources["ShelfCardWidth"];
    private int _columns;

    protected override Size MeasureOverride(Size availableSize)
    {
        // On first display the base measure creates the panel from its template.
        if (ItemsPanelRoot is null)
            base.MeasureOverride(availableSize);

        if (ItemsPanelRoot is ItemsWrapGrid panel && double.IsFinite(availableSize.Width))
        {
            double minSlot = _cardWidth + 16;
            // The scrolling viewport can be narrower than the outer measure
            // while the vertical scrollbar appears during a resize.
            const double scrollbarGutter = 16;
            double available = Math.Max(1, availableSize.Width - Padding.Left - Padding.Right - scrollbarGutter);
            int columns = Math.Max(1, (int)(available / minSlot));
            // Leave a small dead band so a wavering window edge cannot toggle a column.
            if (_columns > 0 && columns > _columns && available < columns * minSlot + 12)
                columns = Math.Max(_columns, columns - 1);
            _columns = columns;
            if (panel.MaximumRowsOrColumns != columns)
            {
                panel.MaximumRowsOrColumns = columns;
            }
            double scale = XamlRoot?.RasterizationScale ?? 1;
            // Leave a physical pixel at the row end for fractional-DPI layout rounding.
            double width = Math.Max(1, Math.Floor((available * scale - 1) / columns)) / scale;
            if (panel.ItemWidth != width) panel.ItemWidth = width;
        }
        return base.MeasureOverride(availableSize);
    }

}
