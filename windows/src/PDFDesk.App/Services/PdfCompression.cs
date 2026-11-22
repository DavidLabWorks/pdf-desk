using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace PDFDesk.Services;

internal static class PdfCompression
{
    private static int Quality(int preset) => preset switch
    {
        0 => 42, 1 => 72, 2 => 92, _ => throw new ArgumentOutOfRangeException(nameof(preset))
    };

    public static async Task GenerateAsync(string source, string output, int preset, CancellationToken token, IProgress<int>? progress = null)
    {
        int quality = Quality(preset);
        string directory = Path.GetDirectoryName(output)!;
        string index = Path.Combine(directory, "images.json");
        if (!File.Exists(index))
        {
            string pending = index + ".pending";
            await RunAsync(["--json-output", "--json-stream-data=file", "--decode-level=none", source, pending], token);
            File.Move(pending, index);
        }
        using var manifest = File.OpenRead(index);
        var objects = (await JsonNode.ParseAsync(manifest, cancellationToken: token))!["qpdf"]![1]!.AsObject();
        var changed = new JsonObject();
        int number = 0, processed = 0, lastPercent = -1;
        foreach (var entry in objects)
        {
            token.ThrowIfCancellationRequested();
            int percent = 10 + (int)(70L * ++processed / objects.Count);
            if (percent != lastPercent)
            {
                progress?.Report(percent);
                lastPercent = percent;
            }
            if (entry.Value?["stream"] is not JsonObject stream || stream["dict"] is not JsonObject dict ||
                !EligiblePhoto(dict, objects)) continue;
            string raw = stream["datafile"]!.GetValue<string>();
            string jpeg = Path.Combine(directory, $"photo-{++number}.jpg");
            var dimensions = await EncodePhotoAsync(raw, jpeg, dict, preset, token);
            if (dimensions is not { } size || new FileInfo(jpeg).Length >= new FileInfo(raw).Length) continue;
            var updated = (JsonObject)dict.DeepClone();
            updated["/Width"] = size.Width;
            updated["/Height"] = size.Height;
            changed[entry.Key] = new JsonObject { ["stream"] = new JsonObject { ["dict"] = updated, ["datafile"] = jpeg } };
        }
        var args = new List<string> { "--object-streams=generate", "--recompress-flate", "--compression-level=9",
            "--optimize-images", "--jpeg-quality=" + quality };
        if (changed.Count > 0)
        {
            string patch = Path.Combine(directory, "changes.json");
            using (var file = File.Create(patch))
                await JsonSerializer.SerializeAsync(file, new JsonObject { ["qpdf"] = new JsonArray(new JsonObject { ["jsonversion"] = 2 }, changed) }, cancellationToken: token);
            args.Add("--update-from-json=" + patch);
        }
        args.Add(source);
        args.Add(output);
        await RunAsync(args.ToArray(), token);
        progress?.Report(92);
        await RunAsync(["--check", output], token, allowWarnings: false);
    }

    // Sample up to three byte-weighted quantiles. Repeated quantiles represent a dominant image.
    internal static async Task<long[]?> EstimateAsync(string source, string directory, CancellationToken token)
    {
        var file = new FileInfo(source);
        var key = (file.FullName, file.Length, file.LastWriteTimeUtc);
        var previous = Volatile.Read(ref _lastEstimate);
        if (previous is not null && previous.Key == key) return previous.Sizes;
        string index = Path.Combine(directory, "estimate.json");
        await RunAsync(["--json-output", "--json-stream-data=none", source, index], token);
        if (new FileInfo(index).Length > 12 * 1024 * 1024) return null;
        using var manifest = File.OpenRead(index);
        var objects = (await JsonNode.ParseAsync(manifest, cancellationToken: token))!["qpdf"]![1]!.AsObject();
        var photos = new List<(string Key, long Bytes, JsonObject Dict)>();
        foreach (var entry in objects)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Value?["stream"]?["dict"] is JsonObject dict && EligiblePhoto(dict, objects) &&
                StreamLength(dict, objects) is long bytes && bytes >= 8192)
                photos.Add((entry.Key, bytes, dict));
        }
        long total = photos.Sum(p => p.Bytes);
        long original = new FileInfo(source).Length;
        if (total < original * 0.15 || total == 0) return null;
        photos.Sort((a, b) => a.Bytes.CompareTo(b.Bytes));
        int count = Math.Min(3, photos.Count);
        // Scanned books often store hundreds of evenly sized pages using the same scanner.
        // One byte-weighted median page avoids nine full-resolution JPEG encodes.
        if (photos.Count >= 50 && total >= original * 0.9)
        {
            var median = photos[photos.Count / 2].Dict;
            int width = (int)median["/Width"]!, height = (int)median["/Height"]!;
            long comparableBytes = photos.Where(p =>
                Math.Abs((int)p.Dict["/Width"]! - width) <= width * 0.1 &&
                Math.Abs((int)p.Dict["/Height"]! - height) <= height * 0.1).Sum(p => p.Bytes);
            if (comparableBytes >= total * 0.9) count = 1;
        }
        var weights = new Dictionary<int, int>();
        long end = photos[0].Bytes;
        int position = 0;
        for (int i = 0; i < count; i++)
        {
            double target = (i + 0.5) / count * total;
            while (end < target && position + 1 < photos.Count) end += photos[++position].Bytes;
            weights[position] = weights.GetValueOrDefault(position) + 1;
        }
        string sample = Path.Combine(directory, "sample.json");
        var args = new List<string> { "--json-output", "--json-stream-data=file", "--decode-level=none" };
        foreach (int i in weights.Keys)
        {
            var photo = photos[i];
            if (photo.Bytes > 8 * 1024 * 1024 ||
                !long.TryParse(photo.Dict["/Width"]?.ToString(), out var width) ||
                !long.TryParse(photo.Dict["/Height"]?.ToString(), out var height) ||
                (double)width * height > 12000000) return null;
            args.Add("--json-object=" + photo.Key[4..^2].Replace(' ', ','));
        }
        args.Add(source);
        args.Add(sample);
        await RunAsync(args.ToArray(), token);
        using var selected = File.OpenRead(sample);
        var streams = (await JsonNode.ParseAsync(selected, cancellationToken: token))!["qpdf"]![1]!;
        var savings = new double[3];
        string jpeg = Path.Combine(directory, "sample.jpg");
        foreach (var (i, weight) in weights)
        {
            var photo = photos[i];
            string raw = streams[photo.Key]!["stream"]!["datafile"]!.GetValue<string>();
            for (int preset = 0; preset < savings.Length; preset++)
            {
                token.ThrowIfCancellationRequested();
                if (await EncodePhotoAsync(raw, jpeg, photo.Dict, preset, token) is null) return null;
                savings[preset] += weight * Math.Max(0, 1 - new FileInfo(jpeg).Length / (double)photo.Bytes);
            }
        }
        var sizes = savings.Select(fraction => Math.Max(1L, original - (long)(fraction / count * Math.Min(total, original)))).ToArray();
        file.Refresh();
        if (file.Exists && file.Length == key.Length && file.LastWriteTimeUtc == key.LastWriteTimeUtc)
            Volatile.Write(ref _lastEstimate, new CachedEstimate(key, sizes));
        return sizes;
    }

    private sealed record CachedEstimate((string, long, DateTime) Key, long[] Sizes);
    private static CachedEstimate? _lastEstimate;

    private static long? StreamLength(JsonObject dict, JsonObject objects)
    {
        var length = dict["/Length"] as JsonValue;
        if (length is not null && length.TryGetValue<string>(out var reference))
            length = objects["obj:" + reference]?["value"] as JsonValue;
        return length is not null && length.TryGetValue<long>(out var bytes) ? bytes : null;
    }

    private static bool EligiblePhoto(JsonObject dict, JsonObject objects)
    {
        if (dict["/Subtype"]?.ToString() != "/Image" || dict["/BitsPerComponent"]?.ToString() != "8" ||
            dict.ContainsKey("/Mask") || dict.ContainsKey("/SMask") || dict.ContainsKey("/Decode") ||
            !IsRgb(dict["/ColorSpace"], objects)) return false;
        JsonNode? filter = dict["/Filter"];
        if (filter is JsonArray { Count: 1 } filters) filter = filters[0];
        if (filter?.ToString() != "/DCTDecode") return false;
        var parms = dict["/DecodeParms"];
        if (parms is JsonArray { Count: 1 } parameters) parms = parameters[0];
        if (parms is not null and not JsonObject { Count: 0 }) return false;
        return true;
    }

    private static async Task<(uint Width, uint Height)?> EncodePhotoAsync(string raw, string jpeg,
        JsonObject dict, int preset, CancellationToken token)
    {
        using var input = File.OpenRead(raw);
        if (input.Length < 8192) return null;
        using var inputStream = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, inputStream);
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        if (dict["/Width"]?.ToString() != width.ToString() || dict["/Height"]?.ToString() != height.ToString()) return null;
        int maxEdge = preset switch { 0 => 1600, 1 => 2400, _ => 4096 };
        double scale = Math.Min(1, (double)maxEdge / Math.Max(width, height));
        width = Math.Max(1, (uint)Math.Round(width * scale));
        height = Math.Max(1, (uint)Math.Round(height * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        token.ThrowIfCancellationRequested();
        using (var file = File.Create(jpeg))
        using (var target = file.AsRandomAccessStream())
        {
            var options = new BitmapPropertySet
            {
                ["ImageQuality"] = new BitmapTypedValue(Quality(preset) / 100f, PropertyType.Single)
            };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, target, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
        }
        return (width, height);
    }

    private static bool IsRgb(JsonNode? space, JsonObject objects)
    {
        if (space is JsonValue reference && objects["obj:" + reference] is JsonObject obj) space = obj["value"];
        return space?.ToString() == "/DeviceRGB" ||
            space is JsonArray { Count: 2 } icc && icc[0]?.ToString() == "/ICCBased" &&
            objects["obj:" + icc[1]]?["stream"]?["dict"]?["/N"]?.ToString() == "3";
    }

    private static async Task RunAsync(string[] arguments, CancellationToken token, bool allowWarnings = true)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Tools", "qpdf", "qpdf.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start qpdf.");
        using var cancel = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        });
        var errors = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        await Task.WhenAll(process.WaitForExitAsync(), errors, stdout);
        token.ThrowIfCancellationRequested();
        // qpdf returns 3 when it completes with recoverable input warnings.
        // The final output check still requires a clean exit.
        if (process.ExitCode != 0 && !(allowWarnings && process.ExitCode == 3))
            throw new InvalidDataException((await errors).Trim());
    }
}
