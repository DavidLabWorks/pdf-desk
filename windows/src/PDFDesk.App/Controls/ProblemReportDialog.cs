using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Services;
using Windows.System;

namespace PDFDesk.Controls;

internal static class ProblemReportDialog
{
    /// <summary>
    /// PDFDesk has no telemetry and makes no network requests, by design. That
    /// means a crash reaches nobody unless the user carries it out by hand, and
    /// until now nothing in the app said where the log even was. This shows the
    /// details a useful report needs and opens the two places to get them.
    /// </summary>
    internal static async Task ShowAsync(FrameworkElement owner)
    {
        string logPath = ErrorLog.Default.Path_;
        bool haveLog = File.Exists(logPath);

        var panel = (StackPanel)((DataTemplate)Application.Current.Resources["ReportProblemContent"]).LoadContent();
        var details = (StackPanel)panel.FindName("Details");
        var assembly = typeof(AppBuildInfo).Assembly;
        bool development = !AppBuildInfo.IsPackaged && assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration != "Release";
        string distribution = UiText.Translate(AppBuildInfo.IsPackaged ? "MSIX package" : development ? "Development build" : "portable");
        var rows = new List<(string Label, string Value)> { ("PDF Desk", $"{AppBuildInfo.CurrentVersion} · {distribution}") };
        if (development)
            rows.Add((UiText.Translate("Build ID"), assembly.ManifestModule.ModuleVersionId.ToString("N")[..12]));
        rows.Add(("Windows", Environment.OSVersion.Version.ToString()));
        rows.Add((UiText.Translate("App / system"), $"{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}"));
        foreach (var (label, value) in rows)
            details.Children.Add(PropertyRow.Create(label, value));

        var copy = (Button)panel.FindName("CopyDetails");
        copy.Click += (_, _) =>
        {
            try
            {
                ClipboardText.SetText(string.Join(Environment.NewLine, rows.Select(r => $"{r.Label}: {r.Value}")));
                copy.Content = UiText.Translate("Copied");
            }
            catch (Exception ex)
            {
                ErrorLog.Default.Write(nameof(ProblemReportDialog), ex);
                copy.Content = UiText.Translate("Retry copy");
            }
        };
        var log = (Button)panel.FindName("LogFolder");
        log.Visibility = haveLog ? Visibility.Visible : Visibility.Collapsed;
        ((TextBlock)panel.FindName("LogInfo")).Text = haveLog
            ? UiText.Translate("Attach errors.log when reporting a crash.")
            : UiText.Translate("No error log yet.");
        if (haveLog) ToolTipService.SetToolTip(log, logPath);
        log.Click += async (_, _) => await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(logPath));
        var dialog = new ContentDialog
        {
            Title = UiText.Current.ReportAProblemAction,
            Content = panel,
            PrimaryButtonText = UiText.Current.OpenIssueTracker,
            CloseButtonText = UiText.Current.Close,
        };
        var result = await DialogHost.ShowAsync(owner, dialog);
        if (result == ContentDialogResult.Primary)
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/DavidLabWorks/pdf-desk-windows/issues/new"));
        }

    }
}
