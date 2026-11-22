using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace PDFDesk.Engine;

/// <summary>Embeds a Windows TrueType subset when PDF's standard fonts cannot encode the text.</summary>
internal static class PdfTextFallback
{
    internal sealed record SubsetFontData(byte[] Bytes, string ToUnicode, byte[] CidToGlyph, Dictionary<char, uint> Codes);
    private const string WinAnsiExtensions = "€‚ƒ„…†‡ˆ‰Š‹ŒŽ‘’“”•–—˜™š›œžŸ";
    private const ushort SubsetFont = 0x0001;
    private const ushort FontCollection = 0x0004;
    private const ushort WindowsPlatform = 3;
    private const ushort UnicodeBmpEncoding = 1;

    internal static bool IsNeeded(string text) => text.Any(c => !CanUseStandardFont(c));

    internal static bool CanUseStandardFont(char c) =>
        char.IsControl(c) || c is >= ' ' and <= '~' or >= '\u00A0' and <= '\u00FF' || WinAnsiExtensions.Contains(c);

    internal static SubsetFontData? CreateSubset(TextBoxContent content)
    {
        if (!OperatingSystem.IsWindows() || content.Text.Any(char.IsSurrogate))
        {
            return null;
        }

        string glyphs = new(content.Text.Where(c => content.UsesLocalFont ? !char.IsControl(c) : !CanUseStandardFont(c)).Distinct().ToArray());
        if (glyphs.Length == 0 || glyphs.Length > ushort.MaxValue)
        {
            return null;
        }

        string directory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        (string File, string Family, string LocalizedFamily, bool Bold) sans =
            ("msyh.ttc", "Microsoft YaHei", "微软雅黑", false);
        (string File, string Family, string LocalizedFamily, bool Bold) sansBold =
            ("msyhbd.ttc", "Microsoft YaHei", "微软雅黑", true);
        (string File, string Family, string LocalizedFamily, bool Bold) serif =
            ("simsun.ttc", "SimSun", "宋体", false);
        (string File, string Family, string LocalizedFamily, bool Bold) traditional =
            ("msjh.ttc", "Microsoft JhengHei", "微軟正黑體", false);
        (string File, string Family, string LocalizedFamily, bool Bold) traditionalBold =
            ("msjhbd.ttc", "Microsoft JhengHei", "微軟正黑體", true);
        (string File, string Family, string LocalizedFamily, bool Bold) deng =
            ("Deng.ttf", "DengXian", "等线", false);
        (string File, string Family, string LocalizedFamily, bool Bold) dengBold =
            ("Dengb.ttf", "DengXian", "等线", true);
        var candidates = content.Font switch
        {
            PdfStandardFont.MicrosoftYaHei => content.Bold ? new[] { sansBold } : new[] { sans },
            PdfStandardFont.SimSun => new[] { serif },
            PdfStandardFont.DengXian => content.Bold ? new[] { dengBold } : new[] { deng },
            _ => content.Bold ? new[] { sansBold, traditionalBold, dengBold, sans, traditional, deng, serif }
            : content.Font == PdfStandardFont.Times ? new[] { serif, sans, traditional, deng }
            : new[] { sans, traditional, deng, serif }
        };

        foreach (var face in candidates)
        {
            string path = Path.Combine(directory, face.File);
            if (!File.Exists(path) || !CanEmbed(face.Family, face.LocalizedFamily, face.Bold, glyphs))
            {
                continue;
            }

            try
            {
                byte[] input = File.ReadAllBytes(path);
                ushort flags = Path.GetExtension(path) == ".ttc" ? (ushort)(SubsetFont | FontCollection) : SubsetFont;
                if (Subset(input, flags, glyphs) is { } subset && MapSubset(subset, glyphs) is { } mapped)
                {
                    return mapped;
                }
            }
            catch (IOException)
            {
                // An optional font can go away between checking and reading it.
            }
        }
        return null;
    }

    private static SubsetFontData? MapSubset(byte[] subset, string glyphs)
    {
        // A PDF CID identifies one Unicode character. Assign our own CIDs so
        // glyph aliases in the system font cannot change the written text.
        var cmap = FindUnicodeCmap(subset);
        if (cmap.IsEmpty)
        {
            return null;
        }

        var cidToGlyph = new byte[(glyphs.Length + 1) * 2];
        var codes = new Dictionary<char, uint>(glyphs.Length);
        var toUnicode = new StringBuilder("""
            /CIDInit /ProcSet findresource begin
            12 dict begin
            begincmap
            /CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def
            /CMapName /PDFDesk-ToUnicode def
            /CMapType 2 def
            1 begincodespacerange
            <0000> <FFFF>
            endcodespacerange

            """);
        for (int start = 0; start < glyphs.Length; start += 100)
        {
            int count = Math.Min(100, glyphs.Length - start);
            toUnicode.AppendLine($"{count} beginbfchar");
            for (int i = start; i < start + count; i++)
            {
                char character = glyphs[i];
                ushort glyph = ReadGlyph(cmap, character);
                if (glyph == 0)
                {
                    return null;
                }
                int cid = i + 1;
                BinaryPrimitives.WriteUInt16BigEndian(cidToGlyph.AsSpan(cid * 2), glyph);
                codes.Add(character, (uint)cid);
                toUnicode.AppendLine($"<{cid:X4}> <{(ushort)character:X4}>");
            }
            toUnicode.AppendLine("endbfchar");
        }
        toUnicode.AppendLine("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend");
        return new SubsetFontData(subset, toUnicode.ToString(), cidToGlyph, codes);
    }

    private static ReadOnlySpan<byte> FindUnicodeCmap(ReadOnlySpan<byte> font)
    {
        // FontSub returns a standalone TrueType face. Find its Windows BMP cmap.
        int tableCount = ReadUInt16(font, 4);
        for (int index = 0; index < tableCount; index++)
        {
            int table = 12 + index * 16;
            if (!font.Slice(table, 4).SequenceEqual("cmap"u8))
            {
                continue;
            }
            int offset = BinaryPrimitives.ReadInt32BigEndian(font[(table + 8)..]);
            var cmap = font[offset..];
            int subtableCount = ReadUInt16(cmap, 2);
            for (int i = 0; i < subtableCount; i++)
            {
                int entry = 4 + i * 8;
                if (ReadUInt16(cmap, entry) == WindowsPlatform && ReadUInt16(cmap, entry + 2) == UnicodeBmpEncoding)
                {
                    int start = BinaryPrimitives.ReadInt32BigEndian(cmap[(entry + 4)..]);
                    if (ReadUInt16(cmap, start) == 4)
                    {
                        return cmap.Slice(start, ReadUInt16(cmap, start + 2));
                    }
                }
            }
            break;
        }
        return [];
    }

    private static ushort ReadGlyph(ReadOnlySpan<byte> cmap, char character)
    {
        int segments = ReadUInt16(cmap, 6) / 2;
        int endCodes = 14;
        int startCodes = endCodes + 2 * segments + 2;
        int deltas = startCodes + 2 * segments;
        int rangeOffsets = deltas + 2 * segments;
        for (int i = 0; i < segments; i++)
        {
            if (character > ReadUInt16(cmap, endCodes + 2 * i))
            {
                continue;
            }
            int startCode = ReadUInt16(cmap, startCodes + 2 * i);
            if (character < startCode)
            {
                break;
            }
            int delta = ReadUInt16(cmap, deltas + 2 * i);
            int offsetEntry = rangeOffsets + 2 * i;
            int offset = ReadUInt16(cmap, offsetEntry);
            if (offset == 0)
            {
                return unchecked((ushort)(character + delta));
            }
            int glyph = ReadUInt16(cmap, offsetEntry + offset + 2 * (character - startCode));
            return glyph == 0 ? (ushort)0 : unchecked((ushort)(glyph + delta));
        }
        return 0;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static bool CanEmbed(string family, string localizedFamily, bool bold, string glyphs)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
        {
            return false;
        }

        IntPtr font = CreateFontW(0, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 0, 0, family);
        try
        {
            if (font == IntPtr.Zero)
            {
                return false;
            }
            IntPtr previous = SelectObject(dc, font);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1))
            {
                return false;
            }
            try
            {
                // GDI can substitute a different font when the requested face is missing.
                var actual = new StringBuilder(64);
                if (GetTextFaceW(dc, actual.Capacity, actual) == 0)
                {
                    return false;
                }
                string name = actual.ToString();
                if (!name.Equals(family, StringComparison.OrdinalIgnoreCase) && name != localizedFamily)
                {
                    return false;
                }

                byte[] embeddingFlags = new byte[2];
                const uint os2Table = 0x322F534F;
                // OS/2 fsType, big endian. Require permission for editable embedding and subsetting.
                if (GetFontData(dc, os2Table, 8, embeddingFlags, 2) != 2)
                {
                    return false;
                }
                int rights = (embeddingFlags[0] << 8) | embeddingFlags[1];
                if ((rights & 0x0302) != 0 || ((rights & 4) != 0 && (rights & 8) == 0))
                {
                    return false;
                }

                var indices = new ushort[glyphs.Length];
                return GetGlyphIndicesW(dc, glyphs, glyphs.Length, indices, 1) != uint.MaxValue
                    && Array.TrueForAll(indices, index => index is not (0 or ushort.MaxValue));
            }
            finally
            {
                SelectObject(dc, previous);
            }
        }
        finally
        {
            if (font != IntPtr.Zero)
            {
                DeleteObject(font);
            }
            DeleteDC(dc);
        }
    }

    private static byte[]? Subset(byte[] input, ushort flags, string glyphs)
    {
        ushort[] codes = glyphs.Select(c => (ushort)c).ToArray();
        uint status = CreateFontPackage(input, (uint)input.Length, out IntPtr output, out _, out uint written,
            flags, 0, 0, 0, WindowsPlatform, UnicodeBmpEncoding, codes, (ushort)codes.Length,
            Allocate, Reallocate, Free, IntPtr.Zero);
        try
        {
            if (status != 0 || output == IntPtr.Zero || written == 0)
            {
                return null;
            }
            var subset = new byte[written];
            Marshal.Copy(output, subset, 0, subset.Length);
            return subset;
        }
        finally
        {
            if (status == 0 && output != IntPtr.Zero)
            {
                Free(output);
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AllocateMemory(nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReallocateMemory(IntPtr buffer, nuint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeMemory(IntPtr buffer);

    private static readonly AllocateMemory Allocate = size => Marshal.AllocHGlobal((nint)size);
    private static readonly ReallocateMemory Reallocate = (buffer, size) => Marshal.ReAllocHGlobal(buffer, (nint)size);
    private static readonly FreeMemory Free = Marshal.FreeHGlobal;

    [DllImport("fontsub.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint CreateFontPackage(byte[] source, uint sourceSize, out IntPtr package,
        out uint packageSize, out uint bytesWritten, ushort flags, ushort collectionIndex, ushort format,
        ushort language, ushort platform, ushort encoding, ushort[] keepList, ushort keepCount,
        AllocateMemory allocate, ReallocateMemory reallocate, FreeMemory free, IntPtr reserved);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charset, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr dc, int count, StringBuilder faceName);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern uint GetFontData(IntPtr dc, uint table, uint offset, [Out] byte[] bytes, uint length);
    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr dc, string text, int count, [Out] ushort[] indices, uint flags);
}
