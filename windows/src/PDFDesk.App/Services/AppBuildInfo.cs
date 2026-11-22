using System.Reflection;

namespace PDFDesk.Services;

internal static class AppBuildInfo
{
    internal static string CurrentVersion
    {
        get
        {
            if (IsPackaged)
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return v.Revision == 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            var assembly = typeof(AppBuildInfo).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? assembly.GetName().Version!.ToString(3);
        }
    }

    /// <summary>
    /// True for the MSIX (Store) build, false for the portable zip. Worth
    /// reporting in a bug: the two differ in how they are installed and
    /// updated, and only one of them is signed.
    /// <c>Package.Current</c> throws when there is no package identity, which
    /// is the documented way to ask.
    /// </summary>
    internal static bool IsPackaged
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current is not null;
            }
            catch
            {
                return false;
            }
        }
    }
}
