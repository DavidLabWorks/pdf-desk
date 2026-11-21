namespace PDFDesk.Controls;

internal static class DocumentNameFormatter
{
    internal static string Fit(string name, Func<string, bool> fits)
    {
        if (fits(name)) return name;

        // Slice at grapheme boundaries so an ellipsis cannot split a visible character.
        int[] starts = System.Globalization.StringInfo.ParseCombiningCharacters(name);
        int low = 1, high = starts.Length - 1;
        string best = "…";
        while (low <= high)
        {
            int keep = low + (high - low) / 2;
            int suffix = keep / 2;
            string tail = suffix == 0 ? "" : name[starts[^suffix]..];
            string candidate = name[..starts[keep - suffix]] + "…" + tail;
            if (fits(candidate))
            {
                best = candidate;
                low = keep + 1;
            }
            else
            {
                high = keep - 1;
            }
        }
        return best;
    }
}
