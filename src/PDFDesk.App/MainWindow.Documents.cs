using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using PDFDesk.Controls;
using PDFDesk.Engine;
using PDFDesk.Services;

namespace PDFDesk;

public sealed partial class MainWindow
{
    // ---------------------------------------------------------------- tabs

    private DocumentView AddTab(string path, RecentFile? restore, bool select, int? index = null, bool readOnly = false, DocumentView? existing = null, WindowsFileIdentity.NavigationKey? key = null)
    {
        key ??= existing?.NavigationKey ?? new WindowsFileIdentity.NavigationKey(path);
        var view = existing ?? new DocumentView(key, readOnly || MatchingTabs(key).Any(t => !t.View.IsReadOnlyView));
        view.HostWindow = this;
        _pendingRestore[view] = restore;
        view.Viewer.LinkActivated += Viewer_LinkActivated;
        view.Viewer.NightMode = _state.Settings.NightMode;
        SeedToolStyles(view.Viewer);
        view.Viewer.NoteRequested += Viewer_NoteRequested;
        view.Viewer.FormAppearanceRequested += Viewer_FormAppearanceRequested;
        AttachTextTool(view.Viewer);
        if (existing == null) AttachSessionEvents(view);

        // Tabs are strip-only (they live in the title bar); the view itself
        // is swapped into DocHost on selection. Tag carries the association.
        var tab = new TabViewItem
        {
            Header = view.DisplayName,
            Tag = view,
            Template = (ControlTemplate)RootGrid.Resources["DocumentTabTemplate"],
            Margin = new Thickness(2, 0, 2, 0),
            Padding = new Thickness(8, 0, 4, 0),
        };
        var move = new MenuFlyoutItem();
        var open = new MenuFlyoutItem();
        var menu = new MenuFlyout { Items = { open, move } };
        menu.Opening += (_, _) => { open.Text = UiText.Current.OpenInNewWindow; move.Text = UiText.Current.MoveTabToWindow; };
        open.Click += async (_, _) => await OpenTabInWindowAsync(tab, view, move: false);
        move.Click += async (_, _) => await OpenTabInWindowAsync(tab, view, move: true);
        tab.ContextFlyout = menu;
        if (index is int slot) Tabs.TabItems.Insert(slot, tab);
        else Tabs.TabItems.Add(tab);
        if (select)
        {
            Tabs.SelectedItem = tab;
        }
        UpdateDirtyIndicator(view);
        RefreshSnapshotBars();
        return view;
    }

    private static void AttachSessionEvents(DocumentView view)
    {
        view.Viewer.ScreenshotModeChanged += () => { if (view == view.HostWindow.CurrentView) view.HostWindow.SyncCaptureTools(); };
        view.Viewer.CompressionRequested += () => _ = view.HostWindow.CompressExportAsync(view.FilePath);
        view.Viewer.DocumentEdited += (_, _) => view.HostWindow.UpdateDirtyIndicator(view);
        view.Viewer.ActiveToolChanged += (_, tool) =>
        {
            if (view.Viewer == view.HostWindow._activeViewer) view.HostWindow.SyncToolButtons(tool);
        };
        view.Viewer.StampPlaced += (_, placement) =>
        {
            if (placement.Tool == AnnotationTool.Image)
            {
                view.HostWindow._state.Settings.ImageWidthPt = placement.WidthPt;
            }
            else
            {
                view.HostWindow._state.Settings.SignatureWidthPt = placement.WidthPt;
            }
            _store.Save(view.HostWindow._state);
        };
        view.SignaturesRead += (_, _) => view.HostWindow.UpdateToolbarForActive();
        view.SearchInvalidated += () => { if (view == view.HostWindow.CurrentView) view.HostWindow.FindBar.ResetResults(); };
        view.SearchRefreshRequested += () => { if (view == view.HostWindow.CurrentView) view.HostWindow.FindBar.RefreshSearch(); };
        view.BookmarksChanged += (_, saved) => view.HostWindow.PersistBookmarks(view, saved);
        view.DraftCopySaved += (copyPath, digest, bookmarks) =>
        {
            view.HostWindow._state.StoreCopyBookmarks(copyPath, digest, bookmarks);
            view.HostWindow.SaveBookmarkState(view, copyPath, pdfSaved: true, successTitle: UiText.Translate("Copy saved"));
        };
        view.PagesEdited += (_, _) =>
        {
            view.HostWindow.UpdateDirtyIndicator(view);
            if (view == view.HostWindow.CurrentView)
            {
                view.HostWindow.UpdateToolbarForActive(); // page count / current page changed
            }
        };
        view.PageOpFailed += (_, message) => view.HostWindow.ShowError(message);
        view.ExtractRequested += (_, _) => _ = view.HostWindow.ExtractPagesAsync(view);
        view.UndoStateChanged += (_, _) =>
        {
            if (view == view.HostWindow.CurrentView)
            {
                view.HostWindow.UpdateUndoButtons();
            }
        };
        view.Loaded2 += (_, _) =>
        {
            RefreshSnapshotBars();
            if (view == view.HostWindow.CurrentView) view.HostWindow.UpdateToolbarForActive();
        };
    }

    private static IEnumerable<(MainWindow Window, TabViewItem Tab, DocumentView View)> MatchingTabs(WindowsFileIdentity.NavigationKey key) =>
        from window in App.Readers
        from tab in window.Tabs.TabItems.OfType<TabViewItem>()
        let view = tab.Tag as DocumentView
        where view != null && view.NavigationKey.Matches(key)
        select (window, tab, view);

    internal static bool HasOtherEditor(DocumentView view) =>
        MatchingTabs(view.NavigationKey).Any(t => t.View != view && !t.View.IsReadOnlyView);

    private static void RefreshSavedLocation(DocumentView writer)
    {
        var previous = writer.NavigationKey;
        var replacement = new WindowsFileIdentity.NavigationKey(writer.FilePath);
        foreach (var (_, _, view) in MatchingTabs(previous).ToList())
            view.NavigationKey = view.NavigationKey.RecheckAfterReplacement(replacement);
    }

    private static void RefreshSnapshotBars()
    {
        foreach (var view in App.Readers.SelectMany(w => w.AllDocumentViews()).Where(v => v.IsReadOnlyView))
        {
            view.RefreshSnapshotBar();
            if (view.HostWindow.CurrentView == view) view.HostWindow.UpdateSnapshotActions(view);
        }
    }

    private DocumentView OpenOrActivate(string path)
    {
        var key = new WindowsFileIdentity.NavigationKey(path);
        var target = MatchingTabs(key).OrderBy(t => t.View.IsReadOnlyView).FirstOrDefault();
        if (target.View == null) return AddTab(path, _state.FindRecent(path), true, key: key);
        target.Window.RestoreWindow();
        target.Window.Tabs.SelectedItem = target.Tab;
        return target.View;
    }

    internal async Task ReloadSnapshotAsync(DocumentView view, bool edit)
    {
        if (!view.IsReadOnlyView || !_savingViews.Add(view)) return;
        DocumentView? replacement = null;
        try
        {
            var matches = MatchingTabs(view.NavigationKey).ToList();
            if (edit && matches.Any(t => !t.View.IsReadOnlyView)) { OpenOrActivate(view.FilePath); return; }
            var old = matches.Single(t => t.View == view).Tab;
            var restore = PositionOf(view)?.ForPath(view.FilePath);
            replacement = AddTab(view.FilePath, restore, false, Tabs.TabItems.IndexOf(old), readOnly: !edit);
            await replacement.EnsureLoadedAsync(restore);
            if (replacement.LoadError != null) throw new IOException(replacement.LoadError);
            if (!App.Readers.Contains(this) || !Tabs.TabItems.Contains(old)) return;
            var tab = Tabs.TabItems.OfType<TabViewItem>().Single(t => ReferenceEquals(t.Tag, replacement));
            if (CurrentView == view) Tabs.SelectedItem = tab;
            CloseTab(old);
            replacement = null;
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (replacement != null && Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, replacement)) is { } failed)
                CloseTab(failed);
            _savingViews.Remove(view);
        }
    }

    private void UpdateSnapshotActions(DocumentView view)
    {
        bool hasEditor = HasOtherEditor(view);
        FocusEditor.Content = hasEditor ? UiText.Current.GoToEditor : UiText.Current.StartEditing;
        FocusEditor.IsEnabled = hasEditor || view.IsDocumentLoaded || view.LoadError != null;
        CopySnapshot.IsEnabled = view.IsDocumentLoaded;
    }

    private async void FocusEditor_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { IsReadOnlyView: true } view) await ReloadSnapshotAsync(view, true);
    }

    private async void CopySnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentView is { IsReadOnlyView: true }) await SaveAsActiveAsync();
    }

    private async Task OpenTabInWindowAsync(TabViewItem tab, DocumentView view, bool move)
    {
        if (!_savingViews.Add(view)) return;
        try
        {
            await view.EnsureLoadedAsync(_pendingRestore.GetValueOrDefault(view));
            if (move) await view.CommitPendingEditsAsync();
            if (!Tabs.TabItems.Contains(tab)) return;
            if ((move && (view.HasPendingOperation || _compressionTasks.ContainsKey(view.FilePath))) || view.LoadError != null)
                throw new IOException(view.LoadError ?? UiText.Current.SourceChangedDuringMove);
            var restore = PositionOf(view)?.ForPath(view.FilePath);
            var destination = App.CreateReaderWindow();
            if (move)
            {
                int index = Tabs.TabItems.IndexOf(tab);
                CloseTab(tab, transfer: true);
                try { destination.AddTab(view.FilePath, restore, true, existing: view); }
                catch
                {
                    if (destination.Tabs.TabItems.OfType<TabViewItem>().FirstOrDefault(t => ReferenceEquals(t.Tag, view)) is { } partial)
                        destination.CloseTab(partial, transfer: true);
                    AddTab(view.FilePath, restore, true, index, existing: view);
                    throw;
                }
            }
            else destination.AddTab(view.FilePath, restore, true, readOnly: true);
            SaveWindowSessions();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _savingViews.Remove(view); }
    }

    // Children owns a control immediately, even before WinUI updates its Parent.
    // Track each move independently of the selected tab and visual-tree timing.
    private readonly Dictionary<FrameworkElement, Panel> _readerControlHosts = [];

    private void MoveReaderControl(FrameworkElement control, Panel initialHost, Panel? target, int row)
    {
        var owner = _readerControlHosts.GetValueOrDefault(control, initialHost);
        Grid.SetRow(control, row);
        if (target?.Children.Contains(control) == true) return;
        if (owner.Children.Contains(control)) owner.Children.Remove(control);
        _readerControlHosts.Remove(control);
        if (target == null) return;
        target.Children.Add(control);
        _readerControlHosts[control] = target;
    }

    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = CurrentView;
        if (_presentationWindow != null) ExitPresentation();
        if (!_restoringSession && !App.Exiting && DocHost.Content is DocumentView previous && previous != selected &&
            previous.IsDocumentLoaded && PositionOf(previous) is { } position)
        {
            _detachedPositions[previous] = position;
            CaptureState(previous);
        }
        ViewActionsFlyout?.Hide();
        DocumentActionsFlyout?.Hide();
        HideToolOptions();
        if (selected != null && SettingsPage.Visibility == Visibility.Visible) LeaveSettings();
        var layout = selected?.ReaderLayout;
        MoveReaderControl(Toolbar, RootGrid, layout, 0);
        MoveReaderControl(ReaderOverlays, Workspace, layout, 1);
        MoveReaderControl(ReaderQuickTools, Workspace, layout, 1);
        MoveReaderControl(ExportQueue, RootGrid, layout ?? RootGrid, layout == null ? 2 : 1);
        Canvas.SetZIndex(ExportQueue, 1);
        UpdateExportQueue();
        DocHost.Content = selected;
        HookActiveViewer();
        if (_focusReading && _activeViewer?.ActiveTool is not (null or AnnotationTool.None))
            SetActiveTool(AnnotationTool.None);
        UpdateStartPageVisibility();

        UpdateToolbarForActive();
        if (!_restoringSession && CurrentView is { } view)
        {
            await LoadTabAsync(view);
        }
    }

    private async Task LoadTabAsync(DocumentView view)
    {
        await view.EnsureLoadedAsync(_pendingRestore.GetValueOrDefault(view));
        if (!App.Readers.Contains(this) || !AllDocumentViews().Contains(view)) return;
        bool firstLoad = _pendingRestore.Remove(view);

        if (view.LoadError is null)
        {
            if (view.Viewer.Document is { } document)
                view.LoadBookmarks(_state.FindRecent(view.FilePath)?.BookmarksFor(document.SavedDigest) ?? []);
            if (firstLoad && view.IsDocumentLoaded)
            {
                if (CurrentView == view || _state.FindRecent(view.FilePath) == null) CaptureState(view);
                _store.Save(_state);
                if (CurrentView is null) PopulateRecents();
            }
        }
        if (view.LoadError is { } error && view == CurrentView)
        {
            ShowError(UiText.Translate(error));
        }
        if (view == CurrentView)
        {
            UpdateToolbarForActive();
        }
    }

    /// <summary>Writes a view's bookmarks into the recents entry (creating one if needed) and saves.</summary>
    private void PersistBookmarks(DocumentView view, bool saved)
    {
        var entry = _state.FindRecent(view.FilePath);
        if (entry is null)
        {
            CaptureState(view); // creates the recents entry with the current position
            entry = _state.FindRecent(view.FilePath);
        }
        if (entry is not null && view.Viewer.Document is { } document)
        {
            var bookmarks = view.BookmarksForPersistence();
            string digest = document.SavedDigest;
            if (saved) RefreshSavedLocation(view);
            entry.StoreBookmarks(digest, bookmarks, saved);
            foreach (var other in App.Readers.SelectMany(w => w.AllDocumentViews()))
                if (other != view && other.Viewer.Document?.SavedDigest == digest && other.NavigationKey.Matches(view.NavigationKey))
                    other.LoadBookmarks(bookmarks, refresh: true);
            if (saved)
            {
                foreach (var (_, _, snapshot) in MatchingTabs(view.NavigationKey).Where(t => t.View.IsReadOnlyView))
                    snapshot.LatestSavedDigest = digest;
                RefreshSnapshotBars();
            }
            SaveBookmarkState(view, view.FilePath, pdfSaved: saved);
        }
    }

    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _bookmarkRetry;

    private void SaveBookmarkState(DocumentView view, string path, bool pdfSaved, string? successTitle = null)
    {
        if (_store.Save(_state))
        {
            _bookmarkRetry?.Stop();
            if (successTitle is not null) view.ShowNotice(successTitle, path, InfoBarSeverity.Success);
            return;
        }

        var heading = UiText.Translate(pdfSaved ? "PDF saved, bookmarks pending" : "Bookmarks not saved");
        view.ShowNotice(heading, UiText.Translate("Bookmarks will be saved automatically when storage is available."), InfoBarSeverity.Warning);
        if (_bookmarkRetry is null)
        {
            _bookmarkRetry = DispatcherQueue.CreateTimer();
            _bookmarkRetry.Tick += (timer, _) =>
            {
                if (_store.Save(SharedState)) timer.Stop();
                else timer.Interval = TimeSpan.FromSeconds(Math.Min(60, timer.Interval.TotalSeconds * 2));
            };
        }
        if (!_bookmarkRetry.IsRunning)
        {
            _bookmarkRetry.Interval = TimeSpan.FromSeconds(2);
            _bookmarkRetry.Start();
        }
    }

    private void ToggleBookmark()
    {
        if (CurrentView is not { IsDocumentLoaded: true, LoadError: null } view || _activeViewer is null)
        {
            return;
        }
        view.ToggleBookmark(_activeViewer.CurrentPage);
        if (view.IsPaneOpen)
        {
            view.ShowBookmarksPane(); // show the result where it landed
        }
    }


    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        => await CloseTabWithPromptAsync(args.Tab);

    private async Task CloseTabWithPromptAsync(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            if (_savingViews.Contains(view)) return;
            if (!await view.CloseOrganizerAsync()) return;
            await view.Viewer.CommitFormFocusAsync();
            await view.Viewer.CommitTextAsync();
            if (view.Viewer.IsEditingText) return;
        }
        if (tab.Tag is DocumentView { IsDirty: true } dirtyView)
        {
            var choice = await PromptSaveChangesAsync([dirtyView.DisplayName]);
            if (choice is null)
            {
                return; // cancelled
            }
            if (choice == true)
            {
                try
                {
                    await dirtyView.SaveInPlaceAsync();
                }
                catch (Exception ex)
                {
                    ShowError(UiText.Format("Save failed: {0}", ex.Message));
                    return;
                }
            }
        }
        CloseTab(tab);
    }

    /// <summary>true = save, false = discard, null = cancel.</summary>
    private async Task<bool?> PromptSaveChangesAsync(string[] names)
    {
        var dialog = new ContentDialog
        {
            Title = UiText.Current.SaveBeforeClosing,
            Content = names,
            ContentTemplate = (DataTemplate)RootGrid.Resources["UnsavedDocumentsTemplate"],
            PrimaryButtonText = names.Length == 1 ? UiText.Current.Save : UiText.Current.SaveAllChanges,
            SecondaryButtonText = UiText.Current.DiscardChanges,
            SecondaryButtonStyle = (Style)Application.Current.Resources["DestructiveDialogButtonStyle"],
            CloseButtonText = UiText.Current.Cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        return await ShowDialogAsync(dialog) switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    private void CloseTab(TabViewItem tab, bool transfer = false)
    {
        if (tab.Tag is DocumentView view)
        {
            CaptureState(view);
            view.Viewer.LinkActivated -= Viewer_LinkActivated;
            view.Viewer.NoteRequested -= Viewer_NoteRequested;
            view.Viewer.FormAppearanceRequested -= Viewer_FormAppearanceRequested;
            DetachTextTool(view.Viewer);
            if (!transfer) view.Close();
            _pendingRestore.Remove(view);
            _detachedPositions.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        tab.Tag = null;
        RefreshSnapshotBars();
        UpdateStartPageVisibility();
    }

    private DocumentView? CurrentView =>
        (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;
}
