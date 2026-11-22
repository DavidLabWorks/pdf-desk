using System.Globalization;
using System.Text;
using System.Text.Json;
using Clipper2Lib;
using PDFDesk.PdfiumInterop;

namespace PDFDesk.Engine;

// Keep the surviving footprint editable across saves. Its standard PDF appearance
// is a filled vector path, so external readers also show the holes at every zoom.
public sealed record InkFootprint(double[][] Loops, byte R, byte G, byte B, byte A)
{
    private const string Key = "PDFDeskInkFootprint";
    internal long Bytes => Loops.Sum(loop => (long)loop.Length * sizeof(double));
    internal static InkFootprint? Read(IntPtr annot)
    {
        string data = PdfiumNative.GetAnnotString(annot, Key);
        if (string.IsNullOrEmpty(data)) return null;
        try
        {
            var shape = JsonSerializer.Deserialize<InkFootprint>(data);
            return shape?.Loops != null && shape.Loops.All(loop => loop is { Length: >= 6 }
                && loop.Length % 2 == 0 && loop.All(double.IsFinite)) ? shape : null;
        }
        catch (JsonException) { return null; }
    }

    internal PathsD Polygons() => new(Loops.Select(loop =>
        new PathD(Enumerable.Range(0, loop.Length / 2).Select(i => new PointD(loop[i * 2], loop[i * 2 + 1])))));

    internal static double[][] Coordinates(PathsD paths)
    {
        var loops = new double[paths.Count][];
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var coordinates = new double[path.Count * 2];
            for (int j = 0; j < path.Count; j++)
            {
                coordinates[j * 2] = path[j].x;
                coordinates[j * 2 + 1] = path[j].y;
            }
            loops[i] = coordinates;
        }
        return loops;
    }

    internal static bool Write(IntPtr annot, InkFootprint? shape)
    {
        if (!PdfiumNative.ClearAnnotAppearance(annot)) return false;
        if (shape == null) return PdfiumNative.SetAnnotString(annot, Key, "");
        var stream = new StringBuilder();
        if (shape.A < 255) stream.Append("/GS gs\n");
        stream.Append(CultureInfo.InvariantCulture, $"{shape.R / 255d:0.######} {shape.G / 255d:0.######} {shape.B / 255d:0.######} rg\n");
        foreach (var loop in shape.Loops)
        {
            for (int i = 0; i < loop.Length; i += 2)
                stream.Append(CultureInfo.InvariantCulture, $"{loop[i]:0.###} {loop[i + 1]:0.###} {(i == 0 ? "m" : "l")}\n");
            stream.Append("h\n");
        }
        stream.Append("f*\n");
        return PdfiumNative.SetAnnotAppearance(annot, stream.ToString())
            && PdfiumNative.SetAnnotString(annot, Key, JsonSerializer.Serialize(shape));
    }

    internal static PathsD Sweep(PathD points, double radius) =>
        points.All(p => p == points[0])
            ? new PathsD { Clipper.Ellipse(points[0], radius, radius, 48) }
            : Clipper.InflatePaths(new PathsD { points }, radius, JoinType.Round, EndType.Round, precision: 3, arcTolerance: 0.01);
}
