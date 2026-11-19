using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace PDFDesk.Services;

internal sealed record LaunchRequest(string[] Files, int? Page = null, double? Zoom = null)
{
    public static LaunchRequest FromCurrentProcess(string[] args)
    {
        // Resolve relative paths before handing off to a process with a different working directory.
        if (args.Length > 0 && File.Exists(args[0]))
        {
            var files = new List<string> { Path.GetFullPath(args[0]) };
            int? page = null;
            double? zoom = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--page" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    page = p;
                    i++;
                }
                else if (args[i] == "--zoom" && i + 1 < args.Length && double.TryParse(args[i + 1], out double z) && double.IsFinite(z))
                {
                    zoom = z;
                    i++;
                }
                else if (File.Exists(args[i]))
                {
                    files.Add(Path.GetFullPath(args[i]));
                }
            }
            return new(files.ToArray(), page, zoom);
        }

        // The Store package's file association can also arrive as a Windows file activation.
        try
        {
            if (AppInstance.GetCurrent().GetActivatedEventArgs().Data is IFileActivatedEventArgs activation)
            {
                return new(activation.Files.OfType<StorageFile>()
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .ToArray());
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Default.Write(nameof(LaunchRequest), ex);
        }
        return new([]);
    }
}
