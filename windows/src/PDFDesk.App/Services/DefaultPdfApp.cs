using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.System;

namespace PDFDesk.Services;

internal static class DefaultPdfApp
{
    private const string ProgId = "DavidLabWorks.PDFDesk.PDF";
    private const string Capabilities = @"Software\DavidLabWorks\PDFDesk\Capabilities";

    internal static bool IsDefault(string? appId)
    {
        if (appId is not null)
        {
            return string.Equals(QueryAssociation(21), appId, StringComparison.OrdinalIgnoreCase); // ASSOCSTR_APPID
        }
        return Environment.ProcessPath is { } exe && string.Equals(QueryAssociation(2), exe, StringComparison.OrdinalIgnoreCase); // ASSOCSTR_EXECUTABLE
    }

    internal static async Task<bool> OpenSettingsAsync(string? appId)
    {
        const string settings = "ms-settings:defaultapps";
        if (appId is null)
        {
            RegisterPortable();
        }
        string target = appId is null ? "registeredAppUser=PDFDesk" : "registeredAUMID=" + Uri.EscapeDataString(appId);
        return await Launcher.LaunchUriAsync(new Uri($"{settings}?{target}"))
            || await Launcher.LaunchUriAsync(new Uri(settings));
    }

    private static void RegisterPortable()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        using var user = Registry.CurrentUser;
        using var fileType = user.CreateSubKey($@"Software\Classes\{ProgId}");
        fileType.SetValue("", "PDF Document");
        using var application = fileType.CreateSubKey("Application");
        application.SetValue("ApplicationName", "PDF Desk");
        using var executable = user.CreateSubKey(@"Software\Classes\Applications\PDFDesk.exe");
        executable.SetValue("FriendlyAppName", "PDF Desk");
        using var command = fileType.CreateSubKey(@"shell\open\command");
        command.SetValue("", $"\"{exe}\" \"%1\"");
        using var icon = fileType.CreateSubKey("DefaultIcon");
        icon.SetValue("", $"\"{exe}\",0");
        using var openWith = user.CreateSubKey(@"Software\Classes\.pdf\OpenWithProgids");
        openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        using var capabilities = user.CreateSubKey(Capabilities);
        capabilities.SetValue("ApplicationName", "PDF Desk");
        capabilities.SetValue("ApplicationDescription", "PDF Desk PDF reader");
        using var associations = capabilities.CreateSubKey("FileAssociations");
        associations.SetValue(".pdf", ProgId);
        using var registered = user.CreateSubKey(@"Software\RegisteredApplications");
        registered.SetValue("PDFDesk", Capabilities);
        SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED
    }

    private static string? QueryAssociation(uint kind)
    {
        uint length = 0;
        AssocQueryStringW(0, kind, ".pdf", null, null, ref length);
        if (length == 0) return null;
        var buffer = new StringBuilder((int)length);
        return AssocQueryStringW(0, kind, ".pdf", null, buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association, string? extra,
        StringBuilder? result, ref uint length);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
