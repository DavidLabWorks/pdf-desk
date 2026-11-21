using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFDesk.Services;
using PDFDesk.Engine;

namespace PDFDesk;

public sealed partial class MainWindow
{
    private object? _settingsReturnTab;
    private bool _syncingPreferences;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsPage.Visibility == Visibility.Visible) return;
        _settingsReturnTab = Tabs.SelectedItem;
        Tabs.SelectedItem = null;
        Workspace.Visibility = Visibility.Collapsed;
        HomeSelection.Visibility = Visibility.Collapsed;
        HomeButton.IsTabStop = true;
        _syncingPreferences = true;
        var settings = _state.Settings;
        ThemeChoice.SelectedIndex = settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageChoice.SelectedIndex = settings.Language switch { "en" => 1, "zh-CN" => 2, _ => 0 };
        RestoreChoice.IsOn = settings.RestoreSession;
        CloseBehaviorChoice.SelectedIndex = settings.CloseToTray ? 0 : 1;
        RefreshLibraryLocation();
        _syncingPreferences = false;
        SettingsPage.Visibility = Visibility.Visible;
        RefreshDefaultPdfStatus();
        SettingsBack.Focus(FocusState.Programmatic);
    }

    private void LeaveSettings()
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        Workspace.Visibility = Visibility.Visible;
        _settingsReturnTab = null;
    }

    private void SettingsBack_Click(object sender, RoutedEventArgs e)
    {
        var tab = _settingsReturnTab;
        LeaveSettings();
        if (tab != null && Tabs.TabItems.Contains(tab)) Tabs.SelectedItem = tab;
        else UpdateStartPageVisibility();
        GlobalSettingsButton.Focus(FocusState.Programmatic);
    }

    private void PreferenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        if (ReferenceEquals(sender, ThemeChoice))
        {
            _state.Settings.Theme = ThemeChoice.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" };
            foreach (var window in App.Readers) window.ApplyTheme(_state.Settings.Theme);
        }
        else
        {
            _state.Settings.Language = LanguageChoice.SelectedIndex switch { 1 => "en", 2 => "zh-CN", _ => "System" };
            foreach (var window in App.Readers)
            {
                window.ApplyLanguage();
                window.UpdateCaptionState();
                window.PopulateRecents();
            }
        }
        _store.Save(_state);
    }

    private void RefreshLibraryLocation()
    {
        LibraryPath.Text = LibraryFolder;
        ResetLibraryFolder.Visibility = _state.Settings.LibraryFolder == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetLibraryFolder(string? folder)
    {
        _state.Settings.LibraryFolder = folder;
        foreach (var window in App.Readers) window._library = new PdfLibrary(LibraryFolder);
        _store.Save(_state);
        RefreshLibraryLocation();
    }

    private async void ChangeLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await FilePickerHost.PickFolderAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (folder == null) return;
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            // Check write access before making this the destination for future imports.
            using (new FileStream(Path.Combine(path, $".{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            SetLibraryFolder(string.Equals(path, _store.LibraryDirectory, StringComparison.OrdinalIgnoreCase) ? null : path);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(new ContentDialog
            {
                Title = UiText.Current.LibraryFolderError,
                Content = ex.Message,
                CloseButtonText = UiText.Current.Close,
            });
        }
    }

    private void ResetLibraryFolder_Click(object sender, RoutedEventArgs e) => SetLibraryFolder(null);

    private void RestoreSession_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        _state.Settings.RestoreSession = RestoreChoice.IsOn;
        _store.Save(_state);
    }

    private void CloseBehavior_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPreferences || SettingsPage?.Visibility != Visibility.Visible) return;
        _state.Settings.CloseToTray = CloseBehaviorChoice.SelectedIndex == 0;
        _store.Save(_state);
    }

    private void ApplyLanguage()
    {
        string previousLanguage = RootGrid.Language;
        UiText.Current.SetLanguage(_state.Settings.Language);
        RootGrid.Language = UiText.Current.LanguageTag;
        if (previousLanguage != UiText.Current.LanguageTag)
        {
            foreach (var view in AllDocumentViews()) view.RefreshBookmarkLabels();
            HideToolOptions();
            _toolPanels.Clear();
            _signatureList = null;
        }
        UpdateQuickToolsToggleLabel();
        UpdateFocusReadingControls();
    }
}
