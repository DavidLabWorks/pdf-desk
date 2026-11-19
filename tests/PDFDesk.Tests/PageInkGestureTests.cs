using PDFDesk.Engine;

namespace PDFDesk.Tests;

public class PageInkGestureTests
{
    [Theory]
    [InlineData(1, 0.3, 0)]
    [InlineData(2, 2.0, 1)]
    public void FastStrokeVisitsEveryRowAndStopsAtEachPageEdge(int columns, double zoom, int rotation)
    {
        var layout = new PageLayout(Enumerable.Repeat((120f, 180f), columns * 3).ToArray(),
            zoom, rotation, 0, 0, columns);
        var first = layout.GetPageRect(0);
        var last = layout.GetPageRect(columns * 2);
        double x = first.X + 10;
        var stroke = new PageInkGesture(layout, x, first.Y - 10);
        stroke.Append(x, last.Bottom + 10);
        Assert.Equal(3, stroke.Pieces.Count);
        for (int row = 0; row < 3; row++)
        {
            var piece = stroke.Pieces[row];
            Assert.Equal(row * columns, piece.Page);
            var rect = layout.GetPageRect(piece.Page);
            Assert.Equal(rect.Y, piece.Points[0].Y, 6);
            Assert.Equal(rect.Bottom, piece.Points[^1].Y, 6);
        }
    }

    [Fact]
    public void ReturningAcrossSpreadStartsSeparateInkOnOriginalPage()
    {
        var layout = new PageLayout([(100, 100), (100, 100)], 1, 0, twoPage: true);
        var left = layout.GetPageRect(0);
        var right = layout.GetPageRect(1);
        double y = left.Y + 10;
        var stroke = new PageInkGesture(layout, left.X + 5, y);
        stroke.Append(right.X + 10, y);
        stroke.Append(left.X + 8, y + 20);
        Assert.Equal(new[] { 0, 1, 0 }, stroke.Pieces.Select(piece => piece.Page));
        Assert.Equal(left.Right, stroke.Pieces[0].Points[^1].X, 6);
        Assert.Equal(right.X, stroke.Pieces[1].Points[0].X, 6);
        Assert.Equal(left.Right, stroke.Pieces[2].Points[0].X, 6);
    }
}
