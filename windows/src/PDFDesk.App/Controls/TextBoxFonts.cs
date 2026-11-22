using PDFDesk.Engine;

namespace PDFDesk.Controls;

/// <summary>Font choices shared by the text toolbar and on-page editor.</summary>
internal static class TextBoxFonts
{
    /// <summary>The families offered, in the order the picker shows them.</summary>
    internal static readonly PdfStandardFont[] Families = Array.FindAll<PdfStandardFont>(
    [
        PdfStandardFont.Helvetica,
        PdfStandardFont.Times,
        PdfStandardFont.Courier,
        PdfStandardFont.MicrosoftYaHei,
        PdfStandardFont.SimSun,
        PdfStandardFont.DengXian,
    ], IsInstalled);

    private static bool IsInstalled(PdfStandardFont font)
    {
        string? file = font switch
        {
            PdfStandardFont.MicrosoftYaHei => "msyh.ttc",
            PdfStandardFont.SimSun => "simsun.ttc",
            PdfStandardFont.DengXian => "Deng.ttf",
            _ => null,
        };
        return file is null || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file));
    }

    /// <summary>What the picker calls each one. The PDF names would mean nothing to a reader.</summary>
    internal static string DisplayName(PdfStandardFont font) => font switch
    {
        PdfStandardFont.Times => "Serif",
        PdfStandardFont.Courier => "Monospace",
        PdfStandardFont.MicrosoftYaHei => "微软雅黑",
        PdfStandardFont.SimSun => "宋体",
        PdfStandardFont.DengXian => "等线",
        _ => "Sans",
    };

    /// <summary>The Windows family that measures the same as the PDF one.</summary>
    internal static string WindowsFamily(PdfStandardFont font) => font switch
    {
        PdfStandardFont.Times => "Times New Roman",
        PdfStandardFont.Courier => "Courier New",
        PdfStandardFont.MicrosoftYaHei => "Microsoft YaHei",
        PdfStandardFont.SimSun => "SimSun",
        PdfStandardFont.DengXian => "DengXian",
        _ => "Arial",
    };

    /// <summary>Sizes offered, in points. Matches the set the form-field dialog uses.</summary>
    internal static readonly double[] Sizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48];

    /// <summary>
    /// One step up or down <see cref="Sizes"/> from wherever the text currently
    /// is, clamped at both ends.
    ///
    /// A size that is not in the list — read back from a box some other tool
    /// wrote — steps to the nearest one that is, in the direction asked for,
    /// rather than snapping to the closest and appearing not to move.
    /// </summary>
    internal static double StepSize(double from, int direction)
    {
        if (direction > 0)
        {
            foreach (double size in Sizes)
            {
                if (size > from + 0.01)
                {
                    return size;
                }
            }
            return Sizes[^1];
        }

        for (int i = Sizes.Length - 1; i >= 0; i--)
        {
            if (Sizes[i] < from - 0.01)
            {
                return Sizes[i];
            }
        }
        return Sizes[0];
    }
}
