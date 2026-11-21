using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PDFDesk.PdfiumInterop;

namespace PDFDesk.Engine;

public sealed partial class PdfDocument
{
    // qpdf's page selection also imports the document-level AcroForm field tree.
    private static string RunQpdf(string directory, params IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Tools", "qpdf", "qpdf.exe"))
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // qpdf reads one argument per line. The Windows command line stays short
        // even for thousands of non-contiguous pages.
        File.WriteAllLines(Path.Combine(directory, "args.txt"), arguments);
        start.ArgumentList.Add("@args.txt");
        using var process = Process.Start(start) ?? throw new IOException("Could not start qpdf.");
        var errors = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        string error = errors.GetAwaiter().GetResult();
        if (process.ExitCode is not (0 or 3)) throw new IOException(error.Trim());
        return output;
    }

    internal static string FormatPageRanges(IReadOnlyList<int> pages)
    {
        var ranges = new List<string>();
        for (int i = 0; i < pages.Count; i++)
        {
            int first = pages[i] + 1;
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            int last = pages[i] + 1;
            ranges.Add(first == last ? $"{first}" : $"{first}-{last}");
        }
        return string.Join(",", ranges);
    }

    private static bool NeedsLinkedOrFormImport(IntPtr source, int count)
    {
        if (PdfiumNative.GetFormType(source) == 1) return true;
        for (int i = 0; i < count; i++)
        {
            var page = PdfiumNative.LoadPage(source, i);
            if (page == IntPtr.Zero) continue;
            try
            {
                int position = 0;
                while (PdfiumNative.LinkEnumerate(page, ref position, out var link))
                {
                    if (PdfiumNative.LinkGetDest(source, link) != IntPtr.Zero) return true;
                    var action = PdfiumNative.LinkGetAction(link);
                    if (action != IntPtr.Zero && PdfiumNative.ActionGetType(action) == PdfiumNative.ActionGoto)
                        return true;
                }
            }
            finally { PdfiumNative.ClosePage(page); }
        }
        return false;
    }

    private byte[] ExportLinkedOrFormPages(List<int> pages)
    {
        string directory = Directory.CreateTempSubdirectory("PDFDesk-pages-").FullName;
        try
        {
            SaveAs(Path.Combine(directory, "source.pdf"));
            string range = FormatPageRanges(pages);
            string input = PdfiumNative.HasNamedDestinations(_handle)
                ? ResolveNamedLinks(directory, "source.pdf") : "source.pdf";
            RunQpdf(directory, "--empty", "--pages", input, range, "--", "output.pdf");
            return File.ReadAllBytes(Path.Combine(directory, "output.pdf"));
        }
        finally { Directory.Delete(directory, true); }
    }

    internal void DuplicatePages(int[] pages, int destination)
    {
        RequireWritable();
        lock (PdfiumLibrary.Lock)
        {
            if (!pages.Any(page => GetLinks(page).Any(link => link.IsInternal)))
            {
                InsertPages(ExportPages(pages), destination);
                return;
            }

            // Select the pages again from the same PDF. An intermediate PDF
            // cannot reference destinations on pages outside the selection.
            RewriteDocument(directory =>
            {
                List<string> args = ["source.pdf", "--pages"];
                if (destination > 0) args.AddRange([".", $"1-{destination}"]);
                args.AddRange([".", FormatPageRanges(pages)]);
                if (destination < PageCount) args.AddRange([".", $"{destination + 1}-z"]);
                args.AddRange(["--", "output.pdf"]);
                RunQpdf(directory, args);
            });
        }
    }

    private void InsertLinkedOrFormPages(byte[] source, int destination, bool hasNames)
    {
        RewriteDocument(directory =>
        {
            File.WriteAllBytes(Path.Combine(directory, "insert.pdf"), source);
            List<string> args = ["source.pdf", "--pages"];
            if (destination > 0) args.AddRange([".", $"1-{destination}"]);
            args.AddRange([hasNames ? ResolveNamedLinks(directory, "insert.pdf") : "insert.pdf", "1-z"]);
            if (destination < PageCount) args.AddRange([".", $"{destination + 1}-z"]);
            args.AddRange(["--", "output.pdf"]);
            RunQpdf(directory, args);
        });
    }

    // qpdf remaps page object references on import, but names belong to the source
    // catalog. Expand local named links before copying pages into another catalog.
    private static string ResolveNamedLinks(string directory, string input)
    {
        var json = JsonNode.Parse(RunQpdf(directory, "--json", "--json-key=qpdf", input))!;
        var objects = json["qpdf"]![1]!.AsObject();
        JsonNode? Resolve(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var id) && objects["obj:" + id] is JsonObject obj
                ? obj["value"] : node;

        var catalog = Resolve(objects["trailer"]!["value"]!["/Root"])!;
        var destinations = new Dictionary<string, JsonNode>();
        void Add(string key, JsonNode? node)
        {
            var dest = Resolve(node);
            if (dest is JsonObject dict) dest = Resolve(dict["/D"]);
            if (dest is JsonArray) destinations[key] = dest;
        }
        if (Resolve(catalog["/Dests"]) is JsonObject legacy)
            foreach (var (key, dest) in legacy) Add(key, dest);

        var visited = new HashSet<JsonNode>();
        void ReadTree(JsonNode? node)
        {
            if (Resolve(node) is not JsonObject tree || !visited.Add(tree)) return;
            if (Resolve(tree["/Names"]) is JsonArray names)
                for (int i = 0; i + 1 < names.Count; i += 2)
                    if (names[i] is JsonValue name) Add(name.ToString(), names[i + 1]);
            if (Resolve(tree["/Kids"]) is JsonArray kids)
                foreach (var child in kids) ReadTree(child);
        }
        if (Resolve(catalog["/Names"]) is JsonObject namesDict) ReadTree(namesDict["/Dests"]);

        bool Expand(JsonNode? node)
        {
            bool changed = false;
            if (node is JsonArray array)
                foreach (var child in array) changed |= Expand(child);
            if (node is not JsonObject dict) return changed;
            foreach (var (_, child) in dict) changed |= Expand(child);
            string? key = dict["/Subtype"]?.ToString() == "/Link" ? "/Dest" :
                dict["/S"]?.ToString() == "/GoTo" ? "/D" : null;
            if (key != null && Resolve(dict[key]) is JsonValue name && destinations.TryGetValue(name.ToString(), out var dest))
            {
                dict[key] = dest.DeepClone();
                changed = true;
            }
            return changed;
        }
        var updates = new JsonObject();
        foreach (var (id, obj) in objects)
            if (obj is JsonObject entry && Expand(entry["value"])) updates[id] = entry.DeepClone();
        if (updates.Count == 0) return input;
        var patch = new JsonObject { ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2 }, updates) };
        File.WriteAllText(Path.Combine(directory, "links.json"), patch.ToJsonString());
        RunQpdf(directory, input, "--update-from-json=links.json", "explicit-links.pdf");
        return "explicit-links.pdf";
    }

    // PDFium only appends annotations. Restore the original array position so earlier
    // history entries, widget references and stacking order continue to refer to the same objects.
    public void RestoreDeletedAnnotation(AnnotationSpec spec, int index, int? replacingIndex = null)
    {
        RequireWritable();
        lock (PdfiumLibrary.Lock)
        {
            long revision = UnsavedRevision;
            AddAnnotationFromSpec(spec);
            try
            {
                var annotations = GetAnnotations(spec.PageIndex);
                int appended = annotations[^1].Index;
                if (replacingIndex is int resized)
                {
                    if (resized < 0 || resized >= appended || index < 0 || index >= appended)
                        throw new InvalidOperationException("The resized annotation could not be located.");
                    if (index == resized && resized == appended - 1)
                    {
                        if (!RemoveAnnotation(spec.PageIndex, resized))
                            throw new InvalidOperationException("Could not replace the resized annotation.");
                        return;
                    }
                }
                else if (appended == index) return;
                RewriteDocument(directory => ReorderLastAnnotation(directory, spec.PageIndex, index, replacingIndex));
            }
            catch
            {
                if (RemoveLastAnnotation(spec.PageIndex)) Interlocked.Exchange(ref _unsavedRevision, revision);
                throw;
            }
        }
    }

    private static void ReorderLastAnnotation(string directory, int page, int index, int? replacingIndex)
    {
        string listing = RunQpdf(directory, "--show-pages", "source.pdf");
        var match = Regex.Match(listing, $@"(?m)^page {page + 1}: (\d+ \d+ R)\s*$");
        if (!match.Success) throw new InvalidDataException("Page object was not found.");
        string reference = match.Groups[1].Value;
        JsonNode ReadObject(string id) => JsonNode.Parse(RunQpdf(directory, "--json-output",
            "--json-object=" + id[..^2].Replace(' ', ','), "source.pdf"))!;
        var patch = ReadObject(reference);
        var annots = patch["qpdf"]![1]!["obj:" + reference]!["value"]!["/Annots"];
        if (annots is JsonValue indirect)
        {
            reference = indirect.GetValue<string>();
            patch = ReadObject(reference);
            annots = patch["qpdf"]![1]!["obj:" + reference]!["value"];
        }
        var array = annots!.AsArray();
        var last = array[^1];
        array.RemoveAt(array.Count - 1);
        if (replacingIndex is int resized) array.RemoveAt(resized);
        array.Insert(index, last);
        File.WriteAllText(Path.Combine(directory, "patch.json"), patch.ToJsonString());
        RunQpdf(directory, "source.pdf", "--update-from-json=patch.json", "output.pdf");
    }

    public byte[] CapturePageTree()
    {
        lock (PdfiumLibrary.Lock)
        {
            FormKillFocus();
            using var stream = new MemoryStream();
            WritePdfLocked(stream);
            return stream.ToArray();
        }
    }

    public void RestorePageTree(byte[] snapshot)
    {
        RequireWritable();
        lock (PdfiumLibrary.Lock)
            RewriteDocument(directory => File.WriteAllBytes(Path.Combine(directory, "output.pdf"), snapshot), captureSource: false);
    }

    private void RewriteDocument(Action<string> transform, bool captureSource = true)
    {
        string directory = Directory.CreateTempSubdirectory("PDFDesk-edit-").FullName;
        try
        {
            if (captureSource) SaveAs(Path.Combine(directory, "source.pdf"));
            transform(directory);
            var access = new FileAccessAdapter(Path.Combine(directory, "output.pdf"));
            var handle = PdfiumNative.LoadCustomDocument(access, null);
            if (handle == IntPtr.Zero)
            {
                access.Dispose();
                throw PdfiumNative.LastError();
            }
            ReleaseAllPagesLocked();
            DisposeFormsLocked();
            PdfiumNative.CloseDocument(_handle);
            _fileAccess.Dispose();
            _fileAccess = access;
            _handle = handle;
            ReadPageMetricsLocked();
            InitializeFormsLocked();
            IsDirty = true;
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Services.ErrorLog.Default.Write(nameof(RewriteDocument), ex);
            }
        }
    }
}
