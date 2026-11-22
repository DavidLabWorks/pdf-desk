using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PDFDesk.Services;

/// <summary>WinUI allows one content dialog per XAML root. Call on the UI thread.</summary>
public static class DialogHost
{
    private static readonly HashSet<XamlRoot> OpenRoots = [];

    public static bool IsOpen(FrameworkElement owner) => OpenRoots.Contains(owner.XamlRoot);

    /// <summary>
    /// Shows a dialog, or returns <see cref="ContentDialogResult.None"/> if one
    /// is already open. <c>None</c> is what the Close button returns, so every
    /// existing call site already treats it as the safe/cancel path.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(FrameworkElement owner, ContentDialog dialog)
    {
        var root = owner.XamlRoot;
        if (root is null || !OpenRoots.Add(root))
        {
            ErrorLog.Default.Write("DialogHost", $"Suppressed a second dialog: {dialog.Title}");
            return ContentDialogResult.None;
        }

        try
        {
            // Popups live outside the content tree and otherwise follow the OS theme.
            dialog.XamlRoot = root;
            if (root.Content is FrameworkElement content)
                dialog.RequestedTheme = content.ActualTheme;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            // A dialog failing to display must never take the app down.
            ErrorLog.Default.Write("DialogHost", ex);
            return ContentDialogResult.None;
        }
        finally
        {
            OpenRoots.Remove(root);
        }
    }
}
