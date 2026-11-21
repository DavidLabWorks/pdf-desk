using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using PDFDesk.Engine;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using Windows.UI;

namespace PDFDesk.Controls;

internal static class CanvasScreenshot
{
    internal static InMemoryRandomAccessStream? Render(PdfDocument document, PageLayout layout, Point[] points,
        bool freehand, double scale, Color background, bool night, CancellationToken cancellation)
    {
        double left = points.Min(p => p.X), top = points.Min(p => p.Y);
        double width = points.Max(p => p.X) - left, height = points.Max(p => p.Y) - top;
        if (width < 2 || height < 2) return null;
        var device = CanvasDevice.GetSharedDevice();
        double limit = Math.Min(4096, device.MaximumBitmapSizeInPixels);
        scale = Math.Min(scale, limit / Math.Max(width, height));
        int w = Math.Max(1, (int)Math.Ceiling(width * scale)), h = Math.Max(1, (int)Math.Ceiling(height * scale));
        using var target = new CanvasRenderTarget(device, w, h, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Colors.Transparent);
            using var mask = freehand ? Outline(device, points, left, top, scale) : null;
            if (mask != null && mask.ComputeArea() < 1) return null;
            using var clip = mask == null ? null : drawing.CreateLayer(1, mask);
            drawing.FillRectangle(0, 0, w, h, background);
            var (first, last) = layout.PagesInVerticalRange(top, top + height);
            for (int page = first; page <= last; page++)
            {
                cancellation.ThrowIfCancellationRequested();
                var rect = layout.GetPageRect(page);
                if (rect.X >= left + width || rect.Right <= left || rect.Bottom <= top) continue;
                int x = (int)Math.Round((rect.X - left) * scale), y = (int)Math.Round((rect.Y - top) * scale);
                float pageScale = (float)(layout.Zoom * scale);
                var (pageWidth, pageHeight) = document.GetPagePixelSize(page, pageScale, layout.Rotation);
                int destX = Math.Max(0, x), destY = Math.Max(0, y);
                int span = Math.Min(w - destX, pageWidth - (destX - x));
                int rows = Math.Min(h - destY, pageHeight - (destY - y));
                if (span <= 0 || rows <= 0) continue;
                var region = document.RenderRegion(page, pageScale, layout.Rotation, destX - x, destY - y, span, rows);
                try
                {
                    if (night)
                        for (int i = 0; i < region.Stride * rows; i += 4)
                        {
                            region.Pixels[i] ^= 255;
                            region.Pixels[i + 1] ^= 255;
                            region.Pixels[i + 2] ^= 255;
                        }
                    using var image = CanvasBitmap.CreateFromBytes(device, region.Pixels, span, rows, DirectXPixelFormat.B8G8R8A8UIntNormalized);
                    drawing.DrawImage(image, destX, destY);
                }
                finally { region.Return(); }
            }
        }
        var png = new InMemoryRandomAccessStream();
        try
        {
            target.SaveAsync(png, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
            png.Seek(0);
            return png;
        }
        catch { png.Dispose(); throw; }
    }

    // The standard Windows bitmap clipboard format may discard alpha. Composite before
    // publishing that representation; the separate PNG still carries the original mask.
    internal static async Task<InMemoryRandomAccessStream> WhiteBitmapAsync(IRandomAccessStream png)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var source = png.CloneStream();
        source.Seek(0);
        using var image = await CanvasBitmap.LoadAsync(device, source, 96);
        using var target = new CanvasRenderTarget(device, image.SizeInPixels.Width, image.SizeInPixels.Height, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Colors.White);
            drawing.DrawImage(image);
        }
        var output = new InMemoryRandomAccessStream();
        try
        {
            await target.SaveAsync(output, CanvasBitmapFileFormat.Bmp);
            output.Seek(0);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    private static CanvasGeometry Outline(CanvasDevice device, Point[] points, double left, double top, double scale)
    {
        using var builder = new CanvasPathBuilder(device);
        for (int i = 0; i < points.Length; i++)
        {
            float x = (float)((points[i].X - left) * scale), y = (float)((points[i].Y - top) * scale);
            if (i == 0) builder.BeginFigure(x, y);
            else builder.AddLine(x, y);
        }
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }
}
