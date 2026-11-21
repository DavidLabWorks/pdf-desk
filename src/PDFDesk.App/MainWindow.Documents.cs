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

    private void AddTab(string path, RecentFile? restore, bool select, int? index = null)
    {
        var view = new DocumentView(path);
        _pendingRestore[view] = restore;
        view.Viewer.LinkActivated += Viewer_LinkActivated;
        view.Viewer.NightMode = _state.Settings.NightMode;
        SeedToolStyles(view.Viewer);
        view.Viewer.CompressionRequested += () => _ = CompressExportAsync(view.FilePath);
        view.Viewer.DocumentEdited += (_, _) => UpdateDirtyIndicator(view);
        view.Viewer.ActiveToolChanged += (_, tool) =>
        {
            if (view.Viewer == _activeViewer) SyncToolButtons(tool);
        };
        view.Viewer.StampPlaced += (_, placement) =>
        {
            if (placement.Tool == AnnotationTool.Image)
            {
                _state.Settings.ImageWidthPt = placement.WidthPt;
            }
            else
            {
                _state.Settings.SignatureWidthPt = placement.WidthPt;
            }
            _store.Save(_state);
        };
        view.SignaturesRead += (_, _) => UpdateToolbarForActive();
        view.Viewer.NoteRequested += Viewer_NoteRequested;
        view.Viewer.FormAppearanceRequested += Viewer_FormAppearanceRequested;
        AttachTextTool(view.Viewer);
        view.BookmarksChanged += (_, _) => PersistBookmarks(view);
        view.PagesEdited += (_, _) =>
        {
            UpdateDirtyIndicator(view);
            if (view == CurrentView)
            {
                UpdateToolbarForActive(); // page count / current page changed
            }
        };
        view.PageOpFailed += (_, message) => ShowError(message);
        view.ExtractRequested += (_, _) => _ = ExtractPagesAsync(view);
        view.UndoStateChanged += (_, _) =>
        {
            if (view == CurrentView)
            {
                UpdateUndoButtons();
            }
        };
        view.Loaded2 += (_, _) => { if (view == CurrentView) { UpdateToolbarForActive(); } };

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
    }

    /// <summary>Opens a path in a new tab, or activates the existing tab if already open.</summary>
    private void OpenOrActivate(string path)
    {
        foreach (var obj in Tabs.TabItems)
        {
            if (obj is TabViewItem { Tag: DocumentView v } tabItem &&
                string.Equals(v.FilePath, path, StringComparison.OrdinalIgnoreCase))
            {
                Tabs.SelectedItem = tabItem;
                return;
            }
        }

        AddTab(path, _state.FindRecent(path), select: true);
    }

    private async Task OpenTabInWindowAsync(TabViewItem tab, DocumentView view, bool move)
    {
        if (!_savingViews.Add(view)) return;
        MainWindow? destination = null;
        TabViewItem? createdTab = null;
        bool editedDuringTransfer = false;
        void OnEdit(object? sender, EventArgs args) => editedDuringTransfer = true;
        try
        {
            if (move && view.IsDocumentLoaded)
            {
                await view.Viewer.FinishEraserAsync();
                await view.Viewer.CommitTextAsync();
                if (view.Viewer.IsEditingText) throw new IOException(UiText.Current.TextCommitFailed);
                if (view.Viewer.IsFormFieldFocused) view.Viewer.KillFormFocus();
            }
            if (move && view.IsDirty)
            {
                var choice = await PromptSaveChangesAsync([view.DisplayName]);
                if (choice is null) return;
                if (choice.Value) await view.SaveInPlaceAsync();
            }
            CaptureState(view);
            view.Viewer.DocumentEdited += OnEdit;
            destination = App.CreateReaderWindow();
            destination.AddTab(view.FilePath, PositionOf(view)?.ForPath(view.FilePath), select: true);
            createdTab = (TabViewItem)destination.Tabs.SelectedItem;
            var copy = (DocumentView)createdTab.Tag;
            await copy.EnsureLoadedAsync(null);
            if (!App.Readers.Contains(destination) || !destination.Tabs.TabItems.Contains(createdTab)) return;
            if (!copy.IsDocumentLoaded || copy.LoadError is not null)
                throw new IOException(copy.LoadError ?? view.FilePath);
            if (move && App.Readers.Contains(this) && Tabs.TabItems.Contains(tab))
            {
                if (editedDuringTransfer || view.Viewer.IsEditingText || view.Viewer.IsFormFieldFocused)
                    throw new IOException(UiText.Current.SourceChangedDuringMove);
                CloseTab(tab);
            }
            SaveWindowSessions();
        }
        catch (Exception ex)
        {
            if (destination != null && App.Readers.Contains(destination))
            {
                if (createdTab != null && destination.Tabs.TabItems.Contains(createdTab)) destination.CloseTab(createdTab);
                if (destination.Tabs.TabItems.Count == 0) { destination._closeApproved = true; destination.Close(); }
            }
            if (!App.Readers.Contains(this)) return;
            RestoreWindow();
            ShowError(ex.Message);
        }
        finally { view.Viewer.DocumentEdited -= OnEdit; _savingViews.Remove(view); }
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

        if (!_restoringSession && CurrentView is { } view)
        {
            UpdateToolbarForActive();
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
            view.LoadBookmarks(_state.FindRecent(view.FilePath)?.Bookmarks ?? []);
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
    private void PersistBookmarks(DocumentView view)
    {
        var entry = _state.FindRecent(view.FilePath);
        if (entry is null)
        {
            CaptureState(view); // creates the recents entry with the current position
            entry = _state.FindRecent(view.FilePath);
        }
        if (entry is not null)
        {
            entry.Bookmarks = view.GetBookmarks();
            foreach (var other in App.Readers.SelectMany(w => w.AllDocumentViews()))
                if (other != view && other.IsDocumentLoaded && string.Equals(other.FilePath, view.FilePath, StringComparison.OrdinalIgnoreCase))
                    other.LoadBookmarks(entry.Bookmarks, refresh: true);
            _store.Save(_state);
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

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is DocumentView view)
        {
            CaptureState(view);
            view.Viewer.LinkActivated -= Viewer_LinkActivated;
            view.Viewer.NoteRequested -= Viewer_NoteRequested;
            view.Viewer.FormAppearanceRequested -= Viewer_FormAppearanceRequested;
            DetachTextTool(view.Viewer);
            view.Close();
            _pendingRestore.Remove(view);
            _detachedPositions.Remove(view);
        }
        Tabs.TabItems.Remove(tab);
        tab.Tag = null;
        UpdateStartPageVisibility();
    }

    private DocumentView? CurrentView =>
        (Tabs.SelectedItem as TabViewItem)?.Tag as DocumentView;
}
